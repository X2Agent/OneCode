using System.Threading.Channels;
using OneCode.App.Tui;
using OneCode.Core.Goals;

namespace OneCode.App.Services.GoalMode;

/// <summary>
/// GOAL 运行期的 TUI 进度发射器：步骤开始/回执、预算快照文案、黄/橙预算预警。
/// TryWrite 非阻塞；发射点均在持久化之后（由调用方保证），不影响执行/事务语义。
/// 拥有预警级别去重状态（Fix-6：级别变化才推送）。
/// </summary>
internal sealed class GoalRuntimeProgress(ChannelWriter<TuiEvent> events, GoalBudget budget)
{
    private GoalBudgetWarningLevel? _lastWarningLevel;

    public void Progress(string message)
        => events.TryWrite(new TuiModeProgress(WorkingMode.Goal, message));

    public void PlanCreated(GoalStepSnapshot[] plan)
    {
        if (plan.Length == 0)
            return;
        events.TryWrite(new TuiGoalPlan(plan.Select(step => step.Description).ToArray()));
        Progress(plan.Length == 1
            ? "已分解为单个子目标，开始执行"
            : $"已分解为 {plan.Length} 个子目标，开始执行");
    }

    public void StepStarted(int index, int total, GoalStepSnapshot step, GoalBudgetUsage usage)
        => Progress($"子目标 {index + 1}/{total}: {TruncateEllipsis(step.Description, 40)}{FormatBudget(usage)}");

    public void StepReceipt(int index, int total, GoalStepExecutionEvidence evidence)
    {
        var (icon, label) = evidence.State switch
        {
            GoalStepState.Completed => ("✓", "完成"),
            GoalStepState.Failed => ("✗", "失败已回滚"),
            GoalStepState.Skipped => ("○", "跳过"),
            _ => ("·", evidence.State.ToString()),
        };
        Progress($"{icon} 子目标 {index + 1}/{total} {label} · {evidence.Attempts} 轮 · {evidence.ChangedFiles.Count} 个文件变更");
    }

    /// <summary>预算快照文案：仅显示启用了上限的维度（null = 不限制，自动省略）。</summary>
    private string FormatBudget(GoalBudgetUsage usage)
    {
        List<string> parts = [];
        if (budget.MaxSubGoalAttempts > 0)
            parts.Add($"{usage.TotalAttempts}/{budget.MaxSubGoalAttempts} 次");
        if (budget.MaxTotalTokens is { } maxTokens)
            parts.Add($"{FormatTokens(usage.TotalTokens)}/{FormatTokens(maxTokens)} tok");
        return parts.Count == 0 ? "" : $" · ⛽ {string.Join(" · ", parts)}";
    }

    private static string FormatTokens(long tokens)
        => tokens >= 1000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string TruncateEllipsis(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..Math.Max(1, maxLength - 1)] + "…";

    public void PublishBudgetWarning(GoalBudgetUsage usage)
    {
        var level = budget.EvaluateWarning(usage);
        if (level == _lastWarningLevel)
            return;
        _lastWarningLevel = level;
        if (level is null)
            return;
        events.TryWrite(new TuiGoalBudgetWarning(
            level.Value,
            usage.TotalAttempts,
            usage.TotalTokens,
            usage.Elapsed));
    }
}
