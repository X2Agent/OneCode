using OneCode.App.Query;

namespace OneCode.Tests;

/// <summary>
/// UI 契约基线守卫：新增 <see cref="QueryEvent"/> 子类型时，必须同时更新本清单与 TUI / WebUI 两处渲染。
/// 清单与程序集内全部具体子类型做集合相等断言，任一侧多出都会失败。
/// </summary>
public sealed class QueryEventContractTests
{
    private static readonly string[] Baseline =
    [
        nameof(AgentMessageEvent),
        nameof(BuildRunCompletedEvent),
        nameof(BuildRunStateEvent),
        nameof(BudgetWarningEvent),
        nameof(CompactSuggestedEvent),
        nameof(DoneEvent),
        nameof(ErrorEvent),
        nameof(FileChangeEvent),
        nameof(GoalBudgetWarningEvent),
        nameof(GoalPlanEvent),
        nameof(GoalPlanUpdatedEvent),
        nameof(GoalResultEvent),
        nameof(ModeProgressEvent),
        nameof(NoticeEvent),
        nameof(SubagentProgressEvent),
        nameof(SuggestionsEvent),
        nameof(TextDeltaEvent),
        nameof(ThinkingDeltaEvent),
        nameof(ToolDoneEvent),
        nameof(ToolStartEvent),
        nameof(TurnCompletedEvent),
        nameof(TurnStartedEvent),
        nameof(WorkingDirectoryChangedEvent),
        nameof(WorkflowRunEndedEvent),
        nameof(WorkflowRunStartedEvent),
    ];

    [Fact]
    public void ConcreteSubtypes_MatchBaseline()
    {
        var actual = typeof(QueryEvent).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(QueryEvent)) && !type.IsAbstract)
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        actual.Should().Equal(
            [.. Baseline.Order(StringComparer.Ordinal)],
            "新增或删除 QueryEvent 子类型必须同步本基线清单，并补 TUI / WebUI 两处渲染");
    }
}
