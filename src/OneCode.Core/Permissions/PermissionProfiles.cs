using OneCode.Core.Tools;

namespace OneCode.Core.Permissions;

/// <summary>
/// Declarative description of a permission mode's behavior along orthogonal dimensions.
///
/// MAF auto-approval flags (AutoApproveAllTools, AutoApproveFileWrites, etc.) drive
/// Infrastructure AutoApprovalRulesFactory. Check-flow fields drive <see cref="PermissionChecker"/>.
///
/// Mutual exclusivity: AutoApproveAllTools implies AutoApproveFileWrites.
/// DenyAllNonReadOnly is incompatible with AutoApproveAllTools/AutoApproveFileWrites.
/// </summary>
public sealed record PermissionProfile
{
    /// <summary>All tools auto-approved at MAF layer (GoalAuto, BypassPermissions).</summary>
    public bool AutoApproveAllTools { get; init; }

    /// <summary>File write tools (Write/Edit) auto-approved at MAF layer (AcceptEdits/BUILD mode).</summary>
    public bool AutoApproveFileWrites { get; init; }

    /// <summary>Read-only shell commands auto-approved (most modes except DontAsk).</summary>
    public bool AutoApproveReadOnlyShell { get; init; } = true;

    /// <summary>Post-edit verification (build/type-check) enabled for this mode.</summary>
    public bool EnableVerification { get; init; }

    /// <summary>Strict mode — deny all non-read-only tools at MAF layer (Plan, DontAsk).</summary>
    public bool DenyAllNonReadOnly { get; init; }
}

/// <summary>High-level CheckAsync flow for a permission mode.</summary>
public enum PermissionCheckFlow
{
    AlwaysAllow,
    ReadOnlyAndEvaluate,
    AutoAllowFileWriteAndShell,
    PlanWhitelist,
}

/// <summary>How destructive shell commands are handled in AutoAllowFileWriteAndShell flow.</summary>
public enum DestructiveShellPolicy
{
    /// <summary>Fall through to EvaluateRules (AcceptEdits).</summary>
    EvaluateRules,

    /// <summary>Deny immediately (GoalAuto).</summary>
    Deny,

    /// <summary>Ask for approval (Team).</summary>
    Ask,
}

/// <summary>How unknown (non-shell, non-write) tools are handled after AutoAllowFileWriteAndShell.</summary>
public enum UnknownToolPolicy
{
    EvaluateRules,
    AllowWithPathValidation,
}

/// <summary>How Ask decisions from EvaluateRules are post-processed (Default/Bubble/DontAsk variants).</summary>
public enum AskDecisionPolicy
{
    Standard,
    Bubble,
    DenyAsk,
}

/// <summary>
/// Full declarative config for one <see cref="PermissionMode"/> — profile + CheckAsync behavior.
/// </summary>
public sealed record PermissionModeConfig
{
    public required PermissionProfile Profile { get; init; }
    public required PermissionCheckFlow Flow { get; init; }
    public AskDecisionPolicy AskPolicy { get; init; } = AskDecisionPolicy.Standard;
    public bool CheckReadOnlyAndPath { get; init; } = true;
    public DestructiveShellPolicy DestructiveShell { get; init; } = DestructiveShellPolicy.EvaluateRules;
    public UnknownToolPolicy UnknownTools { get; init; } = UnknownToolPolicy.EvaluateRules;
    public IReadOnlySet<string>? ExtraAllowedTools { get; init; }
}

/// <summary>
/// Static registry of all permission mode definitions.
/// Replaces <c>PermissionStrategyRouter</c> and the seven strategy classes.
/// </summary>
public static class PermissionProfiles
{
    /// <summary>
    /// Tools allowed in Plan mode beyond read-only tools.
    /// 元数据驱动——从 ToolNames.PlanAllowedTools 获取，工具在注册时声明 ToolCategory.PlanAllowed。
    /// </summary>
    private static IReadOnlySet<string> PlanModeAllowedTools => ToolNames.PlanAllowedTools;

    private static readonly IReadOnlyDictionary<PermissionMode, PermissionModeConfig> Definitions =
        BuildDefinitions();

    /// <summary>Retrieve the MAF-layer profile for a mode. Unknown modes fall back to Default.</summary>
    public static PermissionProfile GetProfile(PermissionMode mode) =>
        GetConfig(mode).Profile;

    /// <summary>Execute CheckAsync logic for the given mode.</summary>
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

        return PermissionCheckHelpers.ApplyAskPolicy(result, config.AskPolicy, toolName, toolInput);
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
            DestructiveShellPolicy.Ask => PermissionCheckResult.Ask(
                "Dangerous shell command in Team mode requires approval (no interactive channel available)."),
            DestructiveShellPolicy.EvaluateRules => null,
            _ => null,
        };

        var result = PermissionCheckHelpers.AutoAllowFileWriteAndShell(
            toolName, toolInput, context, destructiveShellResult);
        if (result != null)
            return result;

        if (config.ExtraAllowedTools?.Contains(toolName) == true)
            return PermissionCheckResult.Allow;

        return config.UnknownTools switch
        {
            UnknownToolPolicy.AllowWithPathValidation =>
                AllowWithPathValidation(toolName, toolInput, context),
            UnknownToolPolicy.EvaluateRules =>
                PermissionCheckHelpers.EvaluateRules(toolName, toolInput, context),
            _ => PermissionCheckHelpers.EvaluateRules(toolName, toolInput, context),
        };
    }

    private static PermissionCheckResult CheckPlanWhitelistFlow(
        PermissionModeConfig config,
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        var readOnlyResult = PermissionCheckHelpers.CheckReadOnlyWithPath(toolName, toolInput, context);
        if (readOnlyResult != null)
            return readOnlyResult;

        if (config.ExtraAllowedTools?.Contains(toolName) == true)
            return PermissionCheckResult.Allow;

        return PermissionCheckResult.Deny(
            $"Tool '{toolName}' is not permitted in plan mode. Only read-only tools, SubmitPlan, task management, sub-agent tools (Agent/ParallelAgents), AskUserQuestion, and AskUserQuestions are allowed.");
    }

    private static PermissionCheckResult AllowWithPathValidation(
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        var pathResult = PermissionCheckHelpers.ValidatePath(toolName, toolInput, context);
        return pathResult.Decision == PermissionDecision.Deny ? pathResult : PermissionCheckResult.Allow;
    }

    private static IReadOnlyDictionary<PermissionMode, PermissionModeConfig> BuildDefinitions()
    {
        var readOnlyEvaluateProfile = new PermissionProfile
        {
            AutoApproveReadOnlyShell = true,
            EnableVerification = false,
        };

        return new Dictionary<PermissionMode, PermissionModeConfig>
        {
            [PermissionMode.Default] = new()
            {
                Profile = readOnlyEvaluateProfile,
                Flow = PermissionCheckFlow.ReadOnlyAndEvaluate,
                AskPolicy = AskDecisionPolicy.Standard,
            },
            [PermissionMode.Bubble] = new()
            {
                Profile = readOnlyEvaluateProfile,
                Flow = PermissionCheckFlow.ReadOnlyAndEvaluate,
                AskPolicy = AskDecisionPolicy.Bubble,
            },
            [PermissionMode.DontAsk] = new()
            {
                Profile = new PermissionProfile
                {
                    AutoApproveReadOnlyShell = false,
                    DenyAllNonReadOnly = true,
                    EnableVerification = false,
                },
                Flow = PermissionCheckFlow.ReadOnlyAndEvaluate,
                AskPolicy = AskDecisionPolicy.DenyAsk,
                CheckReadOnlyAndPath = false,
            },
            [PermissionMode.Auto] = new()
            {
                Profile = new PermissionProfile
                {
                    AutoApproveReadOnlyShell = true,
                    EnableVerification = true,
                },
                Flow = PermissionCheckFlow.ReadOnlyAndEvaluate,
                AskPolicy = AskDecisionPolicy.Standard,
            },
            [PermissionMode.BypassPermissions] = new()
            {
                Profile = new PermissionProfile
                {
                    AutoApproveAllTools = true,
                    AutoApproveFileWrites = true,
                    EnableVerification = false,
                },
                Flow = PermissionCheckFlow.AlwaysAllow,
            },
            [PermissionMode.Plan] = new()
            {
                Profile = new PermissionProfile
                {
                    AutoApproveReadOnlyShell = true,
                    DenyAllNonReadOnly = true,
                    EnableVerification = false,
                },
                Flow = PermissionCheckFlow.PlanWhitelist,
                ExtraAllowedTools = PlanModeAllowedTools,
            },
            [PermissionMode.AcceptEdits] = new()
            {
                Profile = new PermissionProfile
                {
                    AutoApproveFileWrites = true,
                    AutoApproveReadOnlyShell = true,
                    EnableVerification = true,
                },
                Flow = PermissionCheckFlow.AutoAllowFileWriteAndShell,
                DestructiveShell = DestructiveShellPolicy.EvaluateRules,
                UnknownTools = UnknownToolPolicy.EvaluateRules,
            },
            [PermissionMode.GoalAuto] = new()
            {
                Profile = new PermissionProfile
                {
                    AutoApproveAllTools = true,
                    AutoApproveFileWrites = true,
                    EnableVerification = true,
                },
                Flow = PermissionCheckFlow.AutoAllowFileWriteAndShell,
                DestructiveShell = DestructiveShellPolicy.Deny,
                UnknownTools = UnknownToolPolicy.AllowWithPathValidation,
            },
            [PermissionMode.Team] = new()
            {
                Profile = new PermissionProfile
                {
                    AutoApproveFileWrites = true,
                    AutoApproveReadOnlyShell = true,
                    EnableVerification = false,
                },
                Flow = PermissionCheckFlow.AutoAllowFileWriteAndShell,
                DestructiveShell = DestructiveShellPolicy.Ask,
                UnknownTools = UnknownToolPolicy.EvaluateRules,
            },
        };
    }
}
