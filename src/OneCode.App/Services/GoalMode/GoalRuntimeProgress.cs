using System.Threading.Channels;
using OneCode.App.Query;
using OneCode.Core.Goals;

namespace OneCode.App.Services.GoalMode;

/// <summary>
/// GOAL 运行期的进度发射器：步骤开始/回执、预算快照文案、黄/橙预算预警。
/// TryWrite 非阻塞；发射点均在持久化之后（由调用方保证），不影响执行/事务语义。
/// 拥有预警级别去重状态：级别变化才推送。
/// </summary>
internal sealed class GoalRuntimeProgress(ChannelWriter<QueryEvent> events, GoalBudget budget)
{
    private GoalBudget _budget = budget;
    private GoalBudgetWarningLevel? _lastWarningLevel;

    public void UpdateBudget(GoalBudget updated)
    {
        _budget = updated;
        _lastWarningLevel = _budget.EvaluateWarning(new GoalBudgetUsage(0, 0, null));
    }

    public void Progress(string message)
        => events.TryWrite(new ModeProgressEvent(WorkingMode.Goal, message));

    public void PlanCreated(GoalStepSnapshot[] plan)
    {
        if (plan.Length == 0)
            return;
        events.TryWrite(new GoalPlanEvent(plan.Select(step => step.Description).ToArray()));
        events.TryWrite(new GoalPlanUpdatedEvent(plan, [], 0, new GoalBudgetUsage(0, 0, null), _budget));
        Progress(plan.Length == 1
            ? "已分解为单个子目标，开始执行"
            : $"已分解为 {plan.Length} 个子目标，开始执行");
    }

    public void StepStarted(
        int index,
        int total,
        GoalStepSnapshot step,
        GoalBudgetUsage usage,
        IReadOnlyList<GoalStepSnapshot>? plan = null,
        IReadOnlyList<GoalStepExecutionEvidence>? executions = null)
    {
        events.TryWrite(new TurnStartedEvent(index + 1));
        if (plan is { Count: > 0 })
        {
            var activePlan = plan.Select((s, i) => i == index ? s with { State = GoalStepState.InProgress } : s).ToArray();
            events.TryWrite(new GoalPlanUpdatedEvent(activePlan, executions ?? [], index, usage, _budget));
        }
        Progress($"子目标 {index + 1}/{total}: {TruncateEllipsis(step.Description, 40)}{FormatBudget(usage)}");
    }

    public void StepReceipt(
        int index,
        int total,
        GoalStepExecutionEvidence evidence,
        IReadOnlyList<GoalStepSnapshot>? plan = null,
        IReadOnlyList<GoalStepExecutionEvidence>? executions = null,
        GoalBudgetUsage? usage = null)
    {
        var (icon, label) = evidence.State switch
        {
            GoalStepState.Completed => ("✓", "完成"),
            GoalStepState.Failed => ("✗", "失败"),
            GoalStepState.Skipped => ("○", "跳过"),
            _ => ("·", evidence.State.ToString()),
        };
        if (plan is { Count: > 0 })
        {
            events.TryWrite(new GoalPlanUpdatedEvent(plan, executions ?? [], Math.Min(index + 1, plan.Count - 1), usage, _budget));
        }
        Progress($"{icon} 子目标 {index + 1}/{total} {label} · {evidence.Attempts} 轮 · {evidence.ChangedFiles.Count} 个文件变更");
    }

    public void PlanReplanned(
        IReadOnlyList<GoalStepSnapshot> plan,
        IReadOnlyList<GoalStepExecutionEvidence> executions,
        int currentIndex,
        GoalBudgetUsage usage)
    {
        if (plan.Count > 0)
        {
            events.TryWrite(new GoalPlanUpdatedEvent(
                plan,
                executions,
                Math.Clamp(currentIndex, 0, plan.Count - 1),
                usage,
                _budget));
        }
        Progress("步骤失败，已重新规划剩余步骤");
    }

    /// <summary>预算快照文案：仅显示启用了上限的维度（null = 不限制，自动省略）。</summary>
    private string FormatBudget(GoalBudgetUsage usage)
    {
        List<string> parts = [];
        if (_budget.MaxSubGoalAttempts > 0)
            parts.Add($"{usage.TotalAttempts}/{_budget.MaxSubGoalAttempts} 次");
        if (_budget.MaxTotalTokens is { } maxTokens)
            parts.Add($"{FormatTokens(usage.TotalTokens)}/{FormatTokens(maxTokens)} tok");
        return parts.Count == 0 ? "" : $" · ⛽ {string.Join(" · ", parts)}";
    }

    private static string FormatTokens(long tokens)
        => tokens >= 1000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string TruncateEllipsis(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..Math.Max(1, maxLength - 1)] + "…";

    public void PublishBudgetWarning(GoalBudgetUsage usage)
    {
        var level = _budget.EvaluateWarning(usage);
        if (level == _lastWarningLevel)
            return;
        _lastWarningLevel = level;
        if (level is null)
            return;
        events.TryWrite(new GoalBudgetWarningEvent(
            level.Value,
            usage.TotalAttempts,
            usage.TotalTokens,
            usage.Elapsed));
    }
}
