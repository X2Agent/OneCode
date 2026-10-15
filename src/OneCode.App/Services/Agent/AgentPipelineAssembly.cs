using OneCode.Core.Tokens;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Unified pipeline security + options assembly for Main and sub-agent paths.
/// Replaces the split between <c>AgentPipelineSecurityBuilder</c> and ad-hoc
/// <see cref="AgentPipelineOptionsFactory"/> call sites.
/// </summary>
public sealed class AgentPipelineAssembly(
    Core.Tools.IWorkingDirectoryAccessor workingDirectoryAccessor,
    IHookExecutionService hookExecutionService,
    Core.Tools.IVerificationProvider verificationProvider,
    IPermissionModeProvider modeProvider,
    IPermissionChecker permissionChecker,
    ITokenLedger tokenLedger)
{
    private const string PlanSealedMessage =
        "The plan is already submitted and this run is closed to tools. Do NOT call any tool or resubmit — " +
        "reply with one short sentence and stop; the user's approval prompt appears when you finish.";

    /// <summary>
    /// Builds <see cref="AgentPipelineOptions"/> for the Main agent (<see cref="PipelineProfile.Full"/>).
    /// </summary>
    /// <param name="options">主代理运行选项。</param>
    /// <param name="transaction">本轮编辑事务。</param>
    /// <param name="cwd">工作目录。</param>
    /// <param name="modelId">模型标识。</param>
    /// <param name="providerId">提供方标识。</param>
    /// <param name="budgetBaselineTokens">显式预算基线；null 表示就地快照 ledger（见 <see cref="BuildMainSecurityContext"/>）。</param>
    public AgentPipelineOptions BuildMainOptions(
        MainAgentRunOptions options,
        EditTransaction transaction,
        string cwd,
        string? modelId,
        string? providerId,
        long? budgetBaselineTokens = null)
    {
        var securityContext = BuildMainSecurityContext(cwd, options, transaction, modelId, providerId, budgetBaselineTokens);
        var roleOverrides = BuildMainRoleOverrides(options);
        // 待办停滞提醒只给计划执行 run：此时待办即步骤进度，停滞会直接表现为界面卡在某一步。
        var pipelineOptions = AgentPipelineOptionsFactory.Create(PipelineProfile.Full, securityContext, roleOverrides)
            with { EnableTodoStallNudge = options.SeedTodoTasks is { Count: > 0 } };

        // Plan 提交即收口：SubmitPlan 落封后，该 run 的工具调用一律被拒，模型只能纯文本收尾。
        // 探针读的是引擎在 run 起点放入 ambient 的封条引用（StartRun 捕获的执行上下文里可见）。
        return options.WorkingMode == WorkingMode.Plan
            ? pipelineOptions with
            {
                IsRunSealed = () => OneCodeAgentRunContext.PlanSubmissionSeal?.IsSealed == true,
                RunSealedMessage = PlanSealedMessage,
            }
            : pipelineOptions;
    }

    /// <summary>
    /// Builds security context for the Main agent path.
    /// </summary>
    /// <param name="cwd">工作目录。</param>
    /// <param name="options">主代理运行选项。</param>
    /// <param name="transaction">本轮编辑事务。</param>
    /// <param name="modelId">模型标识。</param>
    /// <param name="providerId">提供方标识。</param>
    /// <param name="budgetBaselineTokens">
    /// 显式预算基线覆盖；null 表示就地快照 ledger。流式零输出重试必须传首轮基线——
    /// 零输出 attempt 烧掉的 token 是真实消耗，重试若就地重快照会让这部分逃出 BUILD 预算。
    /// </param>
    public PipelineSecurityContext BuildMainSecurityContext(
        string cwd,
        MainAgentRunOptions options,
        EditTransaction transaction,
        string? modelId,
        string? providerId,
        long? budgetBaselineTokens = null)
    {
        return PipelineSecurityContextBuilder.Create(
            workingDirectory: cwd,
            permissionMode: modeProvider.CurrentMode,
            hook: hookExecutionService,
            permissionChecker: permissionChecker,
            tokenLedger: tokenLedger,
            rulesBySource: options.PermissionRules,
            additionalWorkingDirectories: BuildAdditionalWorkingDirectories(),
            verificationProvider: verificationProvider,
            enableVerification: PermissionProfiles.GetProfile(modeProvider.CurrentMode).EnableVerification,
            orchestrationEventSink: options.OrchestrationEventSink,
            fileChangeCallback: options.FileChangeCallback,
            modelId: modelId,
            providerId: providerId,
            editTransaction: transaction,
            conversationId: options.ConversationId,
            maxBudgetTokens: options.MaxBudgetTokens,
            // BUILD 模式按 run 独立计量：run 启动时对 ledger 快照作为预算基线，
            // Plan 阶段的调研消耗不占用计划执行的额度（maxBudgetTokens 语义：BUILD=单次
            // 计划执行的预算）。PLAN/GOAL 维持会话级累计（基线 0）。
            budgetBaselineTokens: budgetBaselineTokens
                ?? (options.WorkingMode == WorkingMode.Build
                    ? tokenLedger.GetTotalTokens()
                    : 0),
            onBudgetExceeded: options.OnBudgetExceeded,
            onBudgetWarning: options.OnBudgetWarning);
    }

    /// <summary>
    /// Builds role-level overrides for the Main agent path.
    /// </summary>
    public PipelineRoleOverrides BuildMainRoleOverrides(MainAgentRunOptions options)
    {
        return new PipelineRoleOverrides(
            MaxToolCalls: options.MaxTurns,
            // 可指导收尾：达到上限后模型应总结进度而非继续重试调用。
            ToolLimitMessage:
                $"Main agent tool call budget ({options.MaxTurns}) reached. " +
                "Stop calling tools and summarize completed work and remaining items.",
            IsToolAllowed: options.IsToolAllowed,
            // GoalAuto（GOAL 子目标路径）下 broker 为 null，但必须挂 MAF 审批中间件 +
            // 全放行规则（AutoApprovalRulesFactory 对 GoalAuto 自动放行全部工具），
            // 否则 LoopAgent 会因 pending tool approval 无人解析而直接停止。
            // SuppressToolApproval 保留受控 Build（broker=null + 显式禁用）的语义。
            //
            // 该布尔同时门控「工具是否带原生审批标记」：只有真正存在审批通道时才能标记，
            // 否则每个函数调用都会变成无人应答的审批请求。因此 SuppressToolApproval 时
            // 标记必须一并关闭——这与它“不挂交互 broker”的意图一致。
            EnableToolApproval: (options.ApprovalBroker is not null
                || modeProvider.CurrentMode == PermissionMode.GoalAuto)
                && !options.SuppressToolApproval);
    }

    private Dictionary<string, AdditionalWorkingDirectory>? BuildAdditionalWorkingDirectories()
    {
        var dirs = workingDirectoryAccessor.AdditionalDirectories;
        if (dirs is null || dirs.Count == 0)
            return null;

        var dict = new Dictionary<string, AdditionalWorkingDirectory>(StringComparer.Ordinal);
        for (var i = 0; i < dirs.Count; i++)
        {
            dict[$"dir-{i}"] = new AdditionalWorkingDirectory(dirs[i], WorkingDirectorySource.Config);
        }

        return dict;
    }
}
