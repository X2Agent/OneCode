using OneCode.Core.IO;
using OneCode.Core.Tools;

namespace OneCode.Core.Permissions;

public static class PermissionCheckHelpers
{
    // 只读工具白名单与文件写入工具集合统一引用 ToolNames（单一事实源）。
    // - 只读工具：ToolNames.ReadOnlyTools（Read/Glob/Grep/LS/WebFetch 等）
    // - 文件写入：ToolNames.FileWriteTools（Write/Edit/ApplyWorkspaceEdit）

    public const string BashTool = "Bash";
    public const string PowerShellTool = "PowerShell";

    public static bool IsReadOnlyTool(string toolName, JsonElement toolInput)
    {
        if (ToolNames.ReadOnlyTools.Contains(toolName) || IsReadOnlyShell(toolName, toolInput))
            return true;

        // The consolidated Task tool supports read-only actions (list, get, output)
        // and mutating actions (create, update, stop). Check the action parameter.
        if (string.Equals(toolName, "Task", StringComparison.OrdinalIgnoreCase))
        {
            var action = ExtractField(toolInput, "action");
            return action?.ToLowerInvariant() is "list" or "get" or "output";
        }

        return false;
    }

    public static bool IsReadOnlyShell(string toolName, JsonElement toolInput)
    {
        var command = ExtractInputString(toolName, toolInput);
        if (string.Equals(toolName, BashTool, StringComparison.OrdinalIgnoreCase))
            return BashCommandClassifier.IsReadOnly(command);

        if (string.Equals(toolName, PowerShellTool, StringComparison.OrdinalIgnoreCase))
            return PowerShellCommandClassifier.IsReadOnly(command);

        return false;
    }

    /// <summary>
    /// 判断 Shell 工具调用是否为破坏性命令（rm -rf / git push --force 等）。
    /// 用于权限策略区分"危险命令需确认"与"常规开发命令自动放行"。
    /// </summary>
    public static bool IsDestructiveShell(string toolName, JsonElement toolInput)
    {
        var command = ExtractInputString(toolName, toolInput);
        if (string.Equals(toolName, BashTool, StringComparison.OrdinalIgnoreCase))
            return BashCommandClassifier.IsDestructive(command);

        if (string.Equals(toolName, PowerShellTool, StringComparison.OrdinalIgnoreCase))
            return PowerShellCommandClassifier.IsDestructive(command);

        return false;
    }

    /// <summary>
    /// "只读工具 + 文件写入"短路流程——<see cref="AutoAllowFileWriteAndShell"/> 与
    /// <see cref="PermissionChecker.CheckAutoModeWithRulesAsync"/> 共用的前置快路径。
    ///
    /// 决策表：
    ///   只读工具 / 只读 Shell   → Allow
    ///   文件写入工具           → ValidatePath 后 Allow（路径越界 Deny）
    ///   其他工具               → 返回 null（由调用方决定后续处理）
    /// </summary>
    /// <returns>非 null 表示已确定决策；null 表示非只读且非文件写入，由调用方处理。</returns>
    internal static PermissionCheckResult? CheckReadOnlyAndFileWrite(
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        // 1. 只读工具 / 只读 Shell → Allow
        if (IsReadOnlyTool(toolName, toolInput))
            return PermissionCheckResult.Allow;

        // 2. 文件写入工具 → 路径校验后 Allow
        if (ToolNames.FileWriteTools.Contains(toolName))
        {
            var pathResult = ValidatePath(toolName, toolInput, context);
            if (pathResult.Decision == PermissionDecision.Deny)
                return pathResult;
            return PermissionCheckResult.Allow;
        }

        return null;
    }

    /// <summary>
    /// "自动放行文件写入与 Shell"公共流程——适用于 GoalAuto/AcceptEdits/Team 策略。
    ///
    /// 决策表：
    ///   只读工具 / 只读 Shell   → Allow
    ///   文件写入工具           → ValidatePath 后 Allow（路径越界 Deny）
    ///   危险 Shell             → 返回 <paramref name="destructiveShellResult"/>（null 表示未处理，由调用方决定）
    ///   常规 Shell             → ValidatePath 后 Allow（路径越界 Deny）
    ///   未知工具               → 返回 null（由调用方决定，如 EvaluateRules 或 Team 工具白名单）
    ///
    /// 不适用此方法的策略：
    ///   - Default/Auto 模式：文件写入不自动 Allow，需走 EvaluateRules（用 <see cref="CheckReadOnlyAndEvaluate"/> 代替）
    ///   - Plan 模式：白名单制，非白名单工具 Deny
    /// </summary>
    /// <param name="toolName">工具名称（如 "Read"、"Write"、"Bash" 等）。</param>
    /// <param name="toolInput">工具输入，含 command/file_path 等字段。</param>
    /// <param name="context">权限检查上下文，提供工作目录与用户规则。</param>
    /// <param name="destructiveShellResult">
    /// 危险 Shell 命令的预设结果。传 null 表示由策略自行处理（如走 EvaluateRules）。
    /// </param>
    /// <returns>
    /// 返回非 null 表示已确定决策；返回 null 表示工具未被本方法识别（如未知工具），
    /// 调用方应自行决定后续处理（如 EvaluateRules 或 Team 工具白名单）。
    /// </returns>
    internal static PermissionCheckResult? AutoAllowFileWriteAndShell(
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context,
        PermissionCheckResult? destructiveShellResult)
    {
        // 前置快路径：只读工具 + 文件写入（与 Auto 模式 YOLO 路径共用）
        var shortcut = CheckReadOnlyAndFileWrite(toolName, toolInput, context);
        if (shortcut != null)
            return shortcut;

        // 3. Shell 工具：区分危险 vs 常规
        var isShell = string.Equals(toolName, BashTool, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(toolName, PowerShellTool, StringComparison.OrdinalIgnoreCase);
        if (isShell)
        {
            if (IsDestructiveShell(toolName, toolInput))
            {
                // 危险 Shell：返回策略预设结果，或 null 让策略自行处理（如走 EvaluateRules）
                return destructiveShellResult;
            }

            // 常规 Shell（dotnet build / npm install 等）→ 路径校验后 Allow
            var shellPathResult = ValidatePath(toolName, toolInput, context);
            if (shellPathResult.Decision == PermissionDecision.Deny)
                return shellPathResult;
            return PermissionCheckResult.Allow;
        }

        // 未识别工具 → 让策略自行处理
        return null;
    }

    /// <summary>
    /// "只读放行 + 路径校验"公共流程——适用于 PlanMode 等白名单策略。
    ///
    /// 决策表：
    ///   只读工具 / 只读 Shell   → Allow（路径越界 Deny）
    ///   其他工具               → 返回 null（由调用方决定，如白名单检查或 Deny）
    ///
    /// 与 <see cref="CheckReadOnlyAndEvaluate"/> 的区别：
    ///   - 不走 EvaluateRules，只处理只读工具
    ///   - 返回 null 让调用方自行决定非只读工具的处理方式
    /// </summary>
    /// <returns>非 null 表示已确定决策；null 表示非只读工具，由调用方处理。</returns>
    internal static PermissionCheckResult? CheckReadOnlyWithPath(
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        if (!IsReadOnlyTool(toolName, toolInput))
            return null;

        // 只读工具仍需路径校验，防止读取工作目录外的文件（信息泄露风险）。
        // ValidatePath 对无路径的只读工具（如 WebSearch/Skill 等）返回 Allow，无副作用。
        var pathResult = ValidatePath(toolName, toolInput, context);
        if (pathResult.Decision == PermissionDecision.Deny)
            return pathResult;
        return PermissionCheckResult.Allow;
    }

    /// <summary>
    /// "只读放行 + 路径校验 + 规则评估"公共流程——适用于 Default/AutoMode 策略。
    ///
    /// 决策表：
    ///   只读工具 / 只读 Shell   → Allow
    ///   文件写入 / Shell       → ValidatePath（路径越界 Deny）→ EvaluateRules
    ///   其他工具               → EvaluateRules（AlwaysDeny → Deny, AlwaysAllow → Allow, 无匹配 → Ask）
    ///
    /// 与 <see cref="AutoAllowFileWriteAndShell"/> 的关键区别：
    ///   - 文件写入**不**自动 Allow，走 EvaluateRules（Default 模式期望 Write 弹窗确认）
    ///   - Shell**不**区分危险/常规，统一走 EvaluateRules
    ///   - 仍保留 ValidatePath 路径校验（越界直接 Deny，不进入 EvaluateRules）
    /// </summary>
    internal static PermissionCheckResult CheckReadOnlyAndEvaluate(
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context)
    {
        var readOnlyResult = CheckReadOnlyWithPath(toolName, toolInput, context);
        if (readOnlyResult != null)
            return readOnlyResult;

        // 路径校验（文件写入/Shell 工具）：越界直接 Deny
        var pathResult = ValidatePath(toolName, toolInput, context);
        if (pathResult.Decision == PermissionDecision.Deny)
            return pathResult;

        return EvaluateRules(toolName, toolInput, context);
    }

    /// <summary>
    /// 为 Bubble/DontAsk 变体包装 EvaluateRules 结果。
    /// - DenyAsk：Ask → Deny
    /// - Bubble：Ask → Ask + BubbleRequest 原因
    /// - Standard：原样返回
    /// </summary>
    internal static PermissionCheckResult ApplyAskPolicy(
        PermissionCheckResult result,
        AskDecisionPolicy askPolicy,
        string toolName,
        JsonElement toolInput)
    {
        if (result.Decision != PermissionDecision.Ask)
            return result;

        if (askPolicy == AskDecisionPolicy.DenyAsk)
            return PermissionCheckResult.Deny(
                $"Tool '{toolName}' is not in the allow list (don't-ask mode).");

        if (askPolicy == AskDecisionPolicy.Bubble)
            return new PermissionCheckResult
            {
                Decision = PermissionDecision.Ask,
                DecisionReason = new PermissionDecisionReason.BubbleRequest(
                    toolName,
                    ExtractInputString(toolName, toolInput)),
            };

        return result;
    }

    internal static PermissionCheckResult EvaluateRules(
        string toolName, JsonElement toolInput, ToolPermissionContext context)
    {
        var inputStr = ExtractInputString(toolName, toolInput);

        foreach (var (_, group) in context.RulesBySource)
        {
            if (group.AlwaysDeny != null)
            {
                foreach (var rule in group.AlwaysDeny)
                {
                    if (RuleMatches(rule, toolName, inputStr))
                        return PermissionCheckResult.Deny($"Denied by rule: {rule.ToolName}({rule.InputPattern ?? "*"})");
                }
            }

            if (group.AlwaysAllow != null)
            {
                foreach (var rule in group.AlwaysAllow)
                {
                    if (RuleMatches(rule, toolName, inputStr))
                        return PermissionCheckResult.Allow;
                }
            }
        }

        return new PermissionCheckResult { Decision = PermissionDecision.Ask };
    }

    internal static PermissionCheckResult ValidatePath(
        string toolName, JsonElement toolInput, ToolPermissionContext context)
    {
        if (!ToolNames.FileWriteTools.Contains(toolName) && !ToolNames.ReadOnlyTools.Contains(toolName)
            && !string.Equals(toolName, BashTool, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(toolName, PowerShellTool, StringComparison.OrdinalIgnoreCase))
            return PermissionCheckResult.Allow;

        string[] pathValues;
        if (string.Equals(toolName, BashTool, StringComparison.OrdinalIgnoreCase))
        {
            pathValues = BashCommandClassifier.ExtractReferencedPaths(ExtractInputString(toolName, toolInput)).ToArray();
        }
        else if (string.Equals(toolName, PowerShellTool, StringComparison.OrdinalIgnoreCase))
        {
            pathValues = PowerShellCommandClassifier.ExtractReferencedPaths(ExtractInputString(toolName, toolInput)).ToArray();
        }
        else
        {
            pathValues = ExtractInputString(toolName, toolInput) is { } singlePath ? [singlePath] : Array.Empty<string>();
        }

        var workingDir = string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? Environment.CurrentDirectory
            : context.WorkingDirectory;

        foreach (var pathStr in pathValues)
        {
            if (string.IsNullOrWhiteSpace(pathStr))
                continue;

            try
            {
                var fullPath = Path.GetFullPath(pathStr, workingDir);
                var inBase = PathBoundary.IsWithinDirectory(fullPath, workingDir);

                if (!inBase && context.AdditionalWorkingDirectories.Count > 0)
                {
                    inBase = context.AdditionalWorkingDirectories.Values.Any(awd =>
                        PathBoundary.IsWithinDirectory(fullPath, awd.Path));
                }

                if (!inBase)
                    return PermissionCheckResult.Deny(
                        $"Path '{pathStr}' is outside the working directory '{workingDir}'. " +
                        "Use a path inside that directory (for example '.'), or /add-dir to grant access to additional roots.");
            }
            catch (ArgumentException)
            {
                return PermissionCheckResult.Deny($"Invalid path: '{pathStr}'");
            }
            catch (NotSupportedException)
            {
                return PermissionCheckResult.Deny($"Invalid path: '{pathStr}'");
            }
        }

        return PermissionCheckResult.Allow;
    }

    internal static string? ExtractInputString(string toolName, JsonElement input)
    {
        return OneCode.Core.Tools.ToolArgumentExtractor.ExtractInputString(toolName, input);
    }

    private static string? ExtractField(JsonElement input, string fieldName)
    {
        if (input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty(fieldName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static bool RuleMatches(PermissionRule rule, string toolName, string? inputStr)
    {
        if (!string.Equals(rule.ToolName, toolName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.InputPattern == null) return true;
        if (inputStr == null) return false;

        return PermissionRuleParser.GlobMatch(rule.InputPattern, inputStr);
    }
}
