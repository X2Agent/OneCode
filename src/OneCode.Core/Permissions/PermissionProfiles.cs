using OneCode.Core.Tools;

namespace OneCode.Core.Permissions;

/// <summary>
/// Product knobs that are not part of the Allow/Deny/Ask check flow.
/// MAF auto-approval is derived from <see cref="PermissionProfiles.Check"/> (Allow ⇒ auto-approve),
/// not from parallel flags on this type.
/// </summary>
public sealed record PermissionProfile
{
    /// <summary>Post-edit verification (build/type-check) enabled for this mode.</summary>
    public bool EnableVerification { get; init; }
}

/// <summary>High-level CheckAsync flow for a permission mode.</summary>
internal enum PermissionCheckFlow
{
    AlwaysAllow,
    ReadOnlyAndEvaluate,
    AutoAllowFileWriteAndShell,
    PlanWhitelist,
}

/// <summary>How destructive shell commands are handled in AutoAllowFileWriteAndShell flow.</summary>
internal enum DestructiveShellPolicy
{
    /// <summary>Fall through to EvaluateRules (AcceptEdits).</summary>
    EvaluateRules,

    /// <summary>Deny immediately (GoalAuto).</summary>
    Deny,
}

/// <summary>How unknown (non-shell, non-write) tools are handled after AutoAllowFileWriteAndShell.</summary>
internal enum UnknownToolPolicy
{
    /// <summary>Fall through to user rules (AcceptEdits：有交互审批通道，无规则时 Ask)。</summary>
    EvaluateRules,

    /// <summary>路径校验后放行（GoalAuto：无审批通道，Ask 等价于 Deny）。</summary>
    AllowWithPathValidation,
}

/// <summary>How Ask decisions from EvaluateRules are post-processed (Default/DontAsk variants).</summary>
internal enum AskDecisionPolicy
{
    Standard,
    DenyAsk,
}

/// <summary>
/// Full declarative config for one <see cref="PermissionMode"/> — profile + CheckAsync behavior.
/// </summary>
internal sealed record PermissionModeConfig
{
    public required PermissionProfile Profile { get; init; }
    public required PermissionCheckFlow Flow { get; init; }
    public AskDecisionPolicy AskPolicy { get; init; } = AskDecisionPolicy.Standard;
    public bool CheckReadOnlyAndPath { get; init; } = true;
    public DestructiveShellPolicy DestructiveShell { get; init; } = DestructiveShellPolicy.EvaluateRules;
    public UnknownToolPolicy UnknownTools { get; init; } = UnknownToolPolicy.EvaluateRules;
    public bool IncludePlanAllowedTools { get; init; }
}

/// <summary>
/// Static registry of all permission mode definitions.
/// </summary>
public static class PermissionProfiles
{
    /// <summary>
    /// Tools allowed in Plan mode beyond read-only tools.
    /// Sourced from <see cref="ToolNames.PlanAllowedTools"/>.
    /// </summary>
    private static IReadOnlySet<string> PlanModeAllowedTools => ToolNames.PlanAllowedTools;

    private static readonly IReadOnlyDictionary<PermissionMode, PermissionModeConfig> Definitions =
        BuildDefinitions();

    /// <summary>Retrieve the product profile for a mode (e.g. verification). Unknown modes fall back to Default.</summary>
    public static PermissionProfile GetProfile(PermissionMode mode) =>
        GetConfig(mode).Profile;

    /// <summary>Execute CheckAsync logic for the given mode (deterministic; no YOLO).</summary>
    public static PermissionCheckResult Check(
        PermissionMode mode,
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        var config = GetConfig(mode);
        return CheckWithConfig(config, toolName, toolInput, context);
    }

    internal static PermissionModeConfig GetConfig(PermissionMode mode) =>
        Definitions.TryGetValue(mode, out var config)
            ? config
            : Definitions[PermissionMode.Default];

    private static PermissionCheckResult CheckWithConfig(
        PermissionModeConfig config,
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        return config.Flow switch
        {
            PermissionCheckFlow.AlwaysAllow => PermissionCheckResult.Allow,
            PermissionCheckFlow.ReadOnlyAndEvaluate => CheckReadOnlyAndEvaluateFlow(config, toolName, toolInput, context),
            PermissionCheckFlow.AutoAllowFileWriteAndShell => CheckAutoAllowFlow(config, toolName, toolInput, context),
            PermissionCheckFlow.PlanWhitelist => CheckPlanWhitelistFlow(config, toolName, toolInput, context),
            _ => PermissionCheckResult.Ask("Unknown permission check flow."),
        };
    }

    private static PermissionCheckResult CheckReadOnlyAndEvaluateFlow(
        PermissionModeConfig config,
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        PermissionCheckResult result = config.CheckReadOnlyAndPath
            ? PermissionCheckHelpers.CheckReadOnlyAndEvaluate(toolName, toolInput, context)
            : PermissionCheckHelpers.EvaluateRules(toolName, toolInput, context);

        return PermissionCheckHelpers.ApplyAskPolicy(result, config.AskPolicy, toolName);
    }

    private static PermissionCheckResult CheckAutoAllowFlow(
        PermissionModeConfig config,
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        PermissionCheckResult? destructiveShellResult = config.DestructiveShell switch
        {
            DestructiveShellPolicy.Deny => PermissionCheckResult.Deny(
                "Destructive shell command denied in GOAL mode. Use a safer alternative."),
            DestructiveShellPolicy.EvaluateRules => null,
            _ => null,
        };

        var result = PermissionCheckHelpers.AutoAllowFileWriteAndShell(
            toolName, toolInput, context, destructiveShellResult);
        if (result is not null)
            return result;

        if (config.IncludePlanAllowedTools && PlanModeAllowedTools.Contains(toolName))
            return PermissionCheckResult.Allow;

        return config.UnknownTools switch
        {
            UnknownToolPolicy.AllowWithPathValidation =>
                AllowWithPathValidation(toolName, toolInput, context),
            _ => PermissionCheckHelpers.EvaluateRules(toolName, toolInput, context),
        };
    }

    /// <summary>
    /// 用户规则优先，无规则时路径校验后放行：<see cref="PermissionCheckHelpers.EvaluateRules"/>
    /// 给出 Deny/Allow 即采纳（AlwaysDeny 必须生效）；只有无人表态（Ask）时才回落到
    /// <see cref="PermissionCheckHelpers.ValidatePath"/>——越界路径 Deny，其余 Allow。
    /// 注意 ValidatePath 对「既非文件读写、也非 Shell」的工具直接返回 Allow，因此本分支
    /// 对真正未知的工具等价于放行——GOAL 的实际授权面由工具面白名单
    /// （<c>ToolCapabilities.AllowedToolNames</c>）与 Layer 0 安全不变量承担，勿把这里当安全闸。
    /// 采用本策略的原因是 GoalAuto 无审批 broker：<c>Ask</c> 在
    /// <c>PermissionAndLimitMiddleware</c> 里会被 fail-safe 拒绝，直接 EvaluateRules
    /// 会把运行时登记的工具（MCP <c>mcp__*</c>、扩展工具）全部打死。
    /// </summary>
    private static PermissionCheckResult AllowWithPathValidation(
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        var ruleResult = PermissionCheckHelpers.EvaluateRules(toolName, toolInput, context);
        if (ruleResult.Decision != PermissionDecision.Ask)
            return ruleResult;

        var pathResult = PermissionCheckHelpers.ValidatePath(toolName, toolInput, context);
        return pathResult.Decision == PermissionDecision.Deny ? pathResult : PermissionCheckResult.Allow;
    }

    private static PermissionCheckResult CheckPlanWhitelistFlow(
        PermissionModeConfig config,
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        var readOnlyResult = PermissionCheckHelpers.CheckReadOnlyWithPath(toolName, toolInput, context);
        if (readOnlyResult is not null)
            return readOnlyResult;

        // Plan 白名单实时查询注册表：Harness provider 工具（todos_* / file_memory_*）
        // 在装配 agent 时登记（AgentPipelineBuilder），可能晚于 Definitions 的首次构建。
        if (config.IncludePlanAllowedTools && PlanModeAllowedTools.Contains(toolName))
            return PermissionCheckResult.Allow;

        return PermissionCheckResult.Deny(
            $"Tool '{toolName}' is not permitted in plan mode. Only read-only tools, SubmitPlan, task management, sub-agent tools (Agent/ParallelAgents), and AskUserQuestion are allowed.");
    }

    private static IReadOnlyDictionary<PermissionMode, PermissionModeConfig> BuildDefinitions()
    {
        var noVerification = new PermissionProfile { EnableVerification = false };
        var withVerification = new PermissionProfile { EnableVerification = true };

        return new Dictionary<PermissionMode, PermissionModeConfig>
        {
            [PermissionMode.Default] = new()
            {
                Profile = noVerification,
                Flow = PermissionCheckFlow.ReadOnlyAndEvaluate,
                AskPolicy = AskDecisionPolicy.Standard,
            },
            [PermissionMode.DontAsk] = new()
            {
                Profile = noVerification,
                Flow = PermissionCheckFlow.ReadOnlyAndEvaluate,
                AskPolicy = AskDecisionPolicy.DenyAsk,
                CheckReadOnlyAndPath = false,
            },
            [PermissionMode.Auto] = new()
            {
                Profile = withVerification,
                Flow = PermissionCheckFlow.ReadOnlyAndEvaluate,
                AskPolicy = AskDecisionPolicy.Standard,
            },
            [PermissionMode.BypassPermissions] = new()
            {
                Profile = noVerification,
                Flow = PermissionCheckFlow.AlwaysAllow,
            },
            [PermissionMode.Plan] = new()
            {
                Profile = noVerification,
                Flow = PermissionCheckFlow.PlanWhitelist,
                IncludePlanAllowedTools = true,
            },
            [PermissionMode.AcceptEdits] = new()
            {
                Profile = withVerification,
                Flow = PermissionCheckFlow.AutoAllowFileWriteAndShell,
                DestructiveShell = DestructiveShellPolicy.EvaluateRules,
                UnknownTools = UnknownToolPolicy.EvaluateRules,
                IncludePlanAllowedTools = true,
            },
            [PermissionMode.GoalAuto] = new()
            {
                Profile = withVerification,
                Flow = PermissionCheckFlow.AutoAllowFileWriteAndShell,
                DestructiveShell = DestructiveShellPolicy.Deny,
                // 无人值守模式无审批通道（GoalAuto 子目标 EnableToolApproval=false，Ask 会被
                // fail-safe 拒绝），因此未知工具走「路径校验后放行」：越界路径 Deny 不变，
                // 运行时登记的 MCP/扩展工具保持可用。授权面由工具面白名单 + Layer 0 不变量承担。
                UnknownTools = UnknownToolPolicy.AllowWithPathValidation,
            },
        };
    }
}
