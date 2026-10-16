using System.Threading.Channels;
using Microsoft.Extensions.AI;
using OneCode.App.Tui;

namespace OneCode.App.Services.Agent;

/// <summary>
/// GOAL 步内并行执行器：把分解器产出的 subTasks 编译为共享 MAF DAG
///（<see cref="AgentTaskWorkflowCompiler"/>，与 ParallelAgents 同一底座）并运行。
/// ReadOnly 任务并发、Write 任务按依赖拓扑序串行、上游失败下游 Blocked——语义全部继承底座。
/// 执行后运行确定性硬门禁（build/test，按 step 声明）；不做 LLM judge（DAG 步骤的成败
/// 由任务终态 + 硬门禁确定性判定，没有单条 agent 自述可供语义仲裁）。
/// </summary>
/// <remarks>
/// fork 各自持有独立 EditTransaction 并自行提交（step 级事务对本路径为空壳）：
/// DAG 整体失败时成功兄弟的改动保留在 worktree（partial-committed），由 replan 接续——
/// 与单代理步骤「失败即回滚」不同，这是并行写入的必然语义，运行时据此区分处理。
/// </remarks>
internal sealed class GoalSubTaskDagRunner(
    AgentTaskWorkflowCompiler compiler,
    AgentTaskWorkflowHost host,
    GoalSubGoalGateDependencies gateDependencies,
    ILogger<GoalSubTaskDagRunner> logger)
{
    private readonly GoalSubGoalHardGate _hardGate = new(
        gateDependencies.VerificationProvider,
        gateDependencies.DiagnosticRegistry);

    /// <summary>DAG 执行的任务数上限——超过即拒绝（分解器契约如此，防御性双保险）。</summary>
    internal const int MaxSubTasks = 5;

    public async Task<SubGoalExecution> RunAsync(
        GoalItem goal,
        GoalRunOptions options,
        ChannelWriter<TuiEvent> eventWriter,
        CancellationToken ct)
    {
        var subTasks = goal.SubTasks ?? [];
        try
        {
            if (subTasks.Count == 0 || subTasks.Count > MaxSubTasks)
            {
                return Failed(goal.Id,
                    $"Sub-task count {subTasks.Count} outside the allowed range 1..{MaxSubTasks}.");
            }

            var workflowTasks = subTasks
                .Select(task => new AgentWorkflowTask(
                    task.Id,
                    task.Prompt,
                    "general-purpose",
                    task.Description,
                    task.DependsOn,
                    InjectUpstreamResults: true,
                    task.ReadOnly ? AgentTaskExecutionAccess.ReadOnly : AgentTaskExecutionAccess.Write,
                    WorkingDirectory: options.WorkingDirectory))
                .ToList();

            try
            {
                AgentTaskWorkflowCompiler.Validate(workflowTasks);
            }
            catch (InvalidOperationException ex)
            {
                // fail-closed：非法任务图（环、未知依赖）不执行任何任务。
                return Failed(goal.Id, $"Invalid sub-task graph: {ex.Message}");
            }

            eventWriter.TryWrite(new TuiModeProgress(WorkingMode.Goal,
                $"子目标 #{goal.Id} · {subTasks.Count} 个并行子任务开始执行"));

            var parentCapabilities = ToolCapabilitySet.CreateUnrestricted(
                options.Tools.OfType<AIFunction>().Select(tool => tool.Name));
            var workflow = compiler.Compile(workflowTasks, BuildCacheSafeParams(options), parentCapabilities);
            var result = await host.RunAsync(workflow, workflowTasks, ct).ConfigureAwait(false);

            var succeeded = result.TaskOutcomes.Where(outcome => outcome.Success).ToList();
            var failed = result.TaskOutcomes.Where(outcome => !outcome.Success).ToList();
            var allCompleted = failed.Count == 0;
            var changedFiles = succeeded
                .SelectMany(outcome => outcome.ChangedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var summary = string.Join("; ", result.TaskOutcomes.Select(outcome =>
                $"{outcome.Description ?? outcome.TaskId}: {(outcome.Success ? "ok" : $"FAILED — {outcome.Error}")}"));

            // 硬门禁基于「成功兄弟」落盘后的工作树实况：RequiresBuild/RequiresTests/源码变更
            // 规则与单代理路径一致。整体失败时同样运行——部分成功兄弟的改动需要门禁裁决。
            var hardValidation = await _hardGate.ValidateAsync(
                goal,
                options.WorkingDirectory,
                changedFiles,
                [],
                summary,
                ct).ConfigureAwait(false);

            var status = allCompleted && hardValidation.Passed ? GoalStatus.Completed : GoalStatus.Failed;
            eventWriter.TryWrite(new TuiModeProgress(WorkingMode.Goal,
                status == GoalStatus.Completed
                    ? $"子目标 #{goal.Id} · 并行子任务全部通过"
                    : $"子目标 #{goal.Id} · 并行子任务未全部通过（{failed.Count} 失败/阻塞）"));

            var evidence = new SubGoalEvidence(
                summary,
                changedFiles,
                [],
                hardValidation.Evidence.Validations
                    .Select(v => new GoalValidationEvidence(v.Gate, v.Passed, v.Skipped, v.Summary))
                    .ToList(),
                hardValidation.Evidence.Diagnostics);

            return new SubGoalExecution(
                GoalId: goal.Id,
                Status: status,
                Attempts: 1,
                InputTokens: result.TaskOutcomes.Sum(outcome => outcome.InputTokens),
                OutputTokens: result.TaskOutcomes.Sum(outcome => outcome.OutputTokens),
                AgentOutput: result.FinalOutput,
                Evaluation: status == GoalStatus.Completed
                    ? "All parallel sub-tasks succeeded and hard validation passed"
                    : $"Parallel sub-tasks incomplete: {failed.Count} failed/blocked — {summary}",
                Evidence: evidence);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sub-goal {Id} DAG execution failed unexpectedly", goal.Id);
            return Failed(goal.Id, $"Unexpected error: {ex.Message}");
        }
    }

    internal static CacheSafeParams BuildCacheSafeParams(GoalRunOptions options)
        => new()
        {
            SystemPrompt = options.SystemPrompt,
            ModelId = options.ModelId,
            Tools = options.Tools.ToList(),
            ToolCapabilities = ToolCapabilitySet.CreateUnrestricted(
                options.Tools.OfType<AIFunction>().Select(tool => tool.Name)),
        };

    private static SubGoalExecution Failed(int goalId, string evaluation)
        => new(
            GoalId: goalId,
            Status: GoalStatus.Failed,
            Attempts: 1,
            InputTokens: 0,
            OutputTokens: 0,
            AgentOutput: "",
            Evaluation: evaluation);
}
