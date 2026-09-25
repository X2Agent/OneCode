using System.Text;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Agent;
using OneCode.App.Tui;
using OneCode.Core.Exec;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Loop;

/// <summary>
/// <c>/loop</c> 运行时有界循环：把"反复执行直到正确"从提示词约定变成 MAF <see cref="LoopAgent"/>
/// 驱动的确定性循环——每轮是一次完整 agent run，评估器只认确定性证据（检查命令退出码 /
/// 验证提供者），不认模型自评。
/// </summary>
/// <remarks>
/// 与旧提示词版 <c>/loop</c> 的差别：上限由 <c>LoopAgentOptions.MaxIterations</c> 强制、每轮验证
/// 结果与真实失败输出投影到 TUI、失败证据回注下一轮。不引入 GOAL 的 workspace / ledger /
/// receipt / 预算机制。
/// </remarks>
public interface IIterativeLoopService
{
    /// <summary>执行一次有界循环，逐轮投影进度与验证结果。</summary>
    IAsyncEnumerable<TuiEvent> RunAsync(
        string systemPrompt,
        LoopRunRequest request,
        string? modelId,
        IReadOnlyList<string>? imagePaths,
        CancellationToken ct = default);
}

public sealed class IterativeLoopService(
    IMainAgentRunner mainAgentRunner,
    IShellExecutor shellExecutor,
    IVerificationProvider? verificationProvider,
    IToolCatalog toolCatalog,
    IWorkingDirectoryAccessor workingDirectoryAccessor)
    : IIterativeLoopService
{
    /// <summary>per-run 状态登记在 <see cref="LoopContext.AdditionalProperties"/> 上的键。</summary>
    internal const string RunStateKey = "OneCode.IterativeLoop.RunState";

    /// <summary>feedback 中回显上一轮输出的截断长度（fresh context 下 agent 对此零记忆）。</summary>
    private const int LastOutputFeedbackChars = 800;

    /// <summary>检查命令超时（秒），与 Bash 工具默认值一致。</summary>
    private const int CheckTimeoutSeconds = 120;

    /// <summary>inner agent 单轮内的工具调用上限（与主会话 <c>maxTurns</c> 默认值一致）。</summary>
    private const int MaxTurnsPerIteration = 100;

    public async IAsyncEnumerable<TuiEvent> RunAsync(
        string systemPrompt,
        LoopRunRequest request,
        string? modelId,
        IReadOnlyList<string>? imagePaths,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var workingDirectory = workingDirectoryAccessor.WorkingDirectory;
        var state = new LoopRunState();
        using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);
        var changeVersion = transaction.CaptureChangeVersion();

        var inner = await mainAgentRunner.BuildAsAIAgentAsync(
            new MainAgentRunOptions
            {
                ModelId = modelId,
                SystemPrompt = systemPrompt,
                // 循环是自主执行体：不挂交互审批 broker——否则首轮 pending approval 就会让
                // LoopAgent 停下，整个循环空转。权限政策仍由 PermissionChecker 强制执行，
                // 调用方已被 LoopCommand 门在自主权限模式上。
                SuppressToolApproval = true,
                WorkingDirectory = workingDirectory,
                MaxTurns = MaxTurnsPerIteration,
                Tools = toolCatalog.Tools.Cast<AITool>().ToList(),
                WorkingMode = WorkingMode.Goal,
                // 有意不传 ConversationId：该值会让 AgentPipelineBuilder 挂上产品 ChatHistoryProvider
                // （TranscriptChatHistoryProvider），而 ChatHistoryProvider 是 agent 级 provider，
                // 由 PerServiceCallChatHistoryPersistingChatClient 在每次 service call 重新拉取。
                // FreshContextPerIteration 只重置 session，抵消不了这条读取路径——整段历史转录会被
                // 逐轮重新注入，与"原始任务 + 聚合反馈日志"的语义相悖。循环中间输出本就不进转录，
                // 去掉转录读取即可让每轮上下文回到该语义。
            },
            ct).ConfigureAwait(false);

        var progress = Channel.CreateUnbounded<TuiEvent>();
        var evaluator = new DelegateLoopEvaluator(async (loopContext, evalCt) =>
        {
            loopContext.AdditionalProperties.TryAdd(RunStateKey, state);
            state.Iterations = loopContext.Iteration;

            var lastOutput = ExtractLastOutput(loopContext, state);
            var (passed, feedback) = await CheckAsync(
                request, workingDirectory, transaction, changeVersion, evalCt).ConfigureAwait(false);
            state.LastFeedback = feedback;

            progress.Writer.TryWrite(new TuiModeProgress(
                WorkingMode.Goal,
                passed
                    ? $"🔁 循环 第 {state.Iterations}/{request.MaxIterations} 轮 · 检查通过"
                    : $"🔁 循环 第 {state.Iterations}/{request.MaxIterations} 轮 · 检查未通过，反馈注入下一轮",
                passed ? ModeProgressState.Running : ModeProgressState.Waiting));

            if (passed)
            {
                state.Completed = true;
                return LoopEvaluation.Stop();
            }

            // Continue(feedback) → LoopAgent 以 fresh context 重放原始任务 + 聚合反馈日志
            return LoopEvaluation.Continue(BuildFeedback(feedback, lastOutput));
        });

        var loopAgent = new LoopAgent(
            inner,
            evaluator,
            new LoopAgentOptions
            {
                MaxIterations = request.MaxIterations,
                FreshContextPerIteration = true,
                OnBehalfOfAuthorName = "loop-check",
                ExcludeOnBehalfOfMessages = true,
            });

        var runTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in loopAgent.RunStreamingAsync(
                    BuildMessages(request, imagePaths), cancellationToken: ct).ConfigureAwait(false))
                {
                    // 循环中间输出不进主对话转录：进度与验证结果以 TuiModeProgress 单独投影，
                    // 否则每轮完整 agent 输出会把转录刷满。
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                state.Failed = true;
                state.ErrorMessage = ex.Message;
            }
            finally
            {
                progress.Writer.TryComplete();
            }
        }, ct);

        // 评估器写进度、这里读出并 yield——两条管道并行，用户逐轮看到验证结果。
        await foreach (var evt in progress.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;

        await runTask.ConfigureAwait(false);

        // 未通过即回滚本轮循环产生的全部改动：不收敛的循环不该把工作区留在半改状态。
        if (state.Completed && !state.Failed)
            transaction.Commit();
        else
            transaction.Rollback();

        var outcome = Summarize(request, state);
        yield return new TuiModeProgress(
            WorkingMode.Goal,
            outcome.Completed
                ? $"✅ 循环通过：第 {outcome.Attempts}/{request.MaxIterations} 轮确定性检查通过"
                : $"❌ 循环未通过：{outcome.Attempts} 轮内检查始终未过（上限 {request.MaxIterations}），改动已回滚",
            outcome.Completed ? ModeProgressState.Completed : ModeProgressState.Failed);

        if (!string.IsNullOrWhiteSpace(state.ErrorMessage))
            yield return new TuiError($"循环执行失败：{state.ErrorMessage}");

        yield return new TuiModeProgress(WorkingMode.Goal, outcome.Summary);
    }

    /// <summary>
    /// 确定性检查：优先用户提供的检查命令（退出码 0 = 通过），否则退回验证提供者。
    /// 两者都不可用时判定失败——没有可判定证据的循环不准宣称完成。
    /// </summary>
    private async Task<(bool Passed, string Feedback)> CheckAsync(
        LoopRunRequest request,
        string workingDirectory,
        EditTransaction transaction,
        long changeVersion,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CheckCommand))
        {
            if (verificationProvider is null)
            {
                return (false, "未提供 --check 命令且未注册验证提供者：循环拿不到确定性证据，判为未通过。" +
                               "请用 /loop <任务> --check \"<命令>\" 指定以退出码为判据的检查命令。");
            }

            var changedFiles = transaction.GetModifiedFilesSince(changeVersion);
            var verification = await verificationProvider
                .VerifyAsync(workingDirectory, changedFiles, ct).ConfigureAwait(false);
            return verification is { Success: true, Skipped: false }
                ? (true, "验证提供者通过。")
                : (false, verification.FormatForLlm());
        }

        var result = await shellExecutor.ExecuteAsync(
            new ShellExecutionRequest(request.CheckCommand, workingDirectory, "bash", CheckTimeoutSeconds),
            ct).ConfigureAwait(false);

        if (result.ExitCode == 0)
            return (true, $"检查命令退出码 0：{request.CheckCommand}");

        var output = new StringBuilder();
        output.AppendLine(CultureInfo.InvariantCulture, $"检查命令退出码 {result.ExitCode}：{request.CheckCommand}");
        if (!string.IsNullOrWhiteSpace(result.Stdout)) output.AppendLine(result.Stdout.Trim());
        if (!string.IsNullOrWhiteSpace(result.Stderr)) output.AppendLine(result.Stderr.Trim());
        if (result.Truncated) output.AppendLine("(输出已截断)");
        return (false, output.ToString());
    }

    private static string BuildFeedback(string feedback, string lastOutput)
    {
        var builder = new StringBuilder();
        var truncated = GoalSubGoalAssessment.TruncateForSummary(lastOutput, LastOutputFeedbackChars);
        if (!string.IsNullOrEmpty(truncated))
        {
            builder.AppendLine("## 上一轮已完成的工作");
            builder.AppendLine(truncated);
            builder.AppendLine();
        }

        builder.AppendLine("## 检查未通过");
        builder.Append(feedback);
        builder.AppendLine();
        builder.AppendLine("修复上述未通过项后重新执行任务，不要重复已经正确的部分。");
        return builder.ToString();
    }

    private static IReadOnlyList<ChatMessage> BuildMessages(LoopRunRequest request, IReadOnlyList<string>? imagePaths)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(request.Task))
            contents.Add(new TextContent(request.Task));

        foreach (var path in imagePaths ?? [])
        {
            if (!File.Exists(path)) continue;
            var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };
            contents.Add(new DataContent(File.ReadAllBytes(path), mediaType));
        }

        return [new ChatMessage(ChatRole.User, contents)];
    }

    private string ExtractLastOutput(LoopContext loopContext, LoopRunState state)
    {
        var builder = new StringBuilder();
        foreach (var message in loopContext.LastResponse.Messages)
        {
            if (message.Role != ChatRole.Assistant) continue;
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        builder.Append(text.Text);
                        break;
                    case UsageContent usage when usage.Details is { } details:
                        state.InputTokens += (long)(details.InputTokenCount ?? 0);
                        state.OutputTokens += (long)(details.OutputTokenCount ?? 0);
                        break;
                }
            }
        }

        state.LastOutput = builder.ToString();
        return state.LastOutput;
    }

    private static LoopRunOutcome Summarize(LoopRunRequest request, LoopRunState state)
    {
        // LoopAgent 达到 MaxIterations 时先停后判：最后一轮不会进入评估器，
        // 因此"未完成但评估器跑过"必然意味着上限强停，真实轮次要 +1。
        var attempts = state.Completed
            ? state.Iterations
            : state.Iterations == 0 ? 1 : state.Iterations + 1;

        var summary = state.Completed
            ? $"第 {attempts} 轮通过：{state.LastFeedback}"
            : $"{attempts} 轮内未通过：{state.LastFeedback}";

        return new LoopRunOutcome(
            state.Completed && !state.Failed,
            attempts,
            summary,
            state.InputTokens,
            state.OutputTokens);
    }

    private sealed class LoopRunState
    {
        public int Iterations { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public string LastOutput { get; set; } = string.Empty;
        public string LastFeedback { get; set; } = string.Empty;
        public bool Completed { get; set; }
        public bool Failed { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
