using OneCode.Core.Config;

namespace OneCode.App.Commands;

/// <summary>
/// <c>/loop</c> — 有界迭代循环。反复执行任务，每轮用确定性检查（命令退出码 / 验证提供者）
/// 判定是否通过，未过则把真实失败证据注入下一轮，直到通过或达到硬上限。
/// </summary>
/// <remarks>
/// 与 GOAL 模式的分工：GOAL 用 LLM judge 判定"语义上是否达成目标"；<c>/loop</c> 只认确定性
/// 判据（退出码 / 编译 / 测试），因此不需要规划、工作区隔离与预算机制。
/// 循环行为由 MAF <c>LoopAgent</c> 承担，上限为 <c>loop.maxIterations</c>，评估器无法突破。
/// </remarks>
public sealed class LoopCommand(
    IPermissionModeProvider permissionModes,
    IConfigManager configManager) : Command
{
    public override string Name => "loop";

    public override string Description => "反复执行任务直到确定性检查通过（有界迭代循环）";

    public override string? ArgumentHint => "<任务> [--check <命令>] [--max <次数>]";

    public override CommandCategory Category => CommandCategory.Builtin;

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return Task.FromResult(CommandResult.Error(
                "用法：/loop <任务> [--check \"<命令>\"] [--max <次数>]\n" +
                "示例：/loop 修复登录页样式回归 --check \"npm test -- --run login\"\n" +
                "提示：--check 的退出码 0 即为本轮通过；不加 --check 时退回构建/测试验证。"));

        var taskParts = new List<string>();
        string? checkCommand = null;
        var maxIterations = ModeBudgetSettings.FromSettings(configManager.Current.Effective).MaxLoopIterations;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                        return Task.FromResult(CommandResult.Error("/loop：--check 需要一个非空命令。"));
                    checkCommand = args[++i];
                    break;
                case "--max":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[i + 1], out var parsed)
                        || parsed < 1
                        || parsed > ModeBudgetSettings.MaxLoopIterationsUpperBound)
                        return Task.FromResult(CommandResult.Error(
                            $"/loop：--max 需要一个 1..{ModeBudgetSettings.MaxLoopIterationsUpperBound} 的整数。" +
                            "每轮都是一次完整自主 agent run，需要更多轮次请改用 GOAL 模式。"));
                    maxIterations = parsed;
                    i++;
                    break;
                default:
                    taskParts.Add(args[i]);
                    break;
            }
        }

        var task = string.Join(' ', taskParts).Trim();
        if (task.Length == 0)
            return Task.FromResult(CommandResult.Error("/loop：缺少任务描述。用法：/loop <任务> [--check <命令>] [--max <次数>]"));

        // 循环是自主执行体：在交互审批模式下运行会与"逐次询问用户"的权限语义冲突，
        // 因此 fail-closed 拒绝，而不是静默放权。
        if (!AutonomousPermissionModes.Contains(permissionModes.CurrentMode))
        {
            return Task.FromResult(CommandResult.Error(
                $"/loop 只能运行在自主权限模式下（当前：{permissionModes.CurrentMode}）。" +
                "先 /permissions dontAsk（或切入 GOAL 模式）再重试——循环不会替你做审批决定。"));
        }

        return Task.FromResult(CommandResult.Loop(task, checkCommand, maxIterations));
    }

    /// <summary>允许无人值守执行工具的权限模式。</summary>
    private static readonly HashSet<PermissionMode> AutonomousPermissionModes =
    [
        PermissionMode.GoalAuto,
        PermissionMode.DontAsk,
        PermissionMode.Auto,
        PermissionMode.BypassPermissions,
    ];
}
