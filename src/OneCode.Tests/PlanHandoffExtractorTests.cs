using Microsoft.Extensions.AI;
using OneCode.App.Services.PlanMode;

namespace OneCode.Tests;

public sealed class PlanHandoffExtractorTests
{
    [Fact]
    public void Extract_UserRequestAndAskUserQuestionAnswer_FreezesBothForExecution()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "build a todo app in ./app"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "AskUserQuestion")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "Storage? -> sqlite")]),
            new(ChatRole.Assistant, "plan submitted"),
        ];

        var handoff = PlanHandoffExtractor.Extract(history);

        handoff.OriginalRequest.Should().Be("build a todo app in ./app");
        handoff.Clarifications.Should().Equal("Q&A: Storage? -> sqlite");
    }

    [Fact]
    public void Extract_FailedAskUserQuestionResult_IsNotTreatedAsAnswer()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "build it"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "AskUserQuestion")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "AskUserQuestion was cancelled by the user.")]),
        ];

        var handoff = PlanHandoffExtractor.Extract(history);

        handoff.Clarifications.Should().BeEmpty("工具失败/取消文案不是用户答案");
    }

    [Fact]
    public void Extract_ResultOfOtherTool_IsNotTreatedAsAnswer()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "build it"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "Read")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "file contents")]),
        ];

        PlanHandoffExtractor.Extract(history).Clarifications.Should().BeEmpty();
    }

    [Fact]
    public void Extract_LongConversation_KeepsOriginalRequestAndOnlyRecentFollowUps()
    {
        var history = Enumerable.Range(1, 8)
            .Select(i => new ChatMessage(ChatRole.User, $"message {i}"))
            .ToList();

        var handoff = PlanHandoffExtractor.Extract(history);

        handoff.OriginalRequest.Should().Be("message 1", "原话不得被后续往返挤出交接上下文");
        handoff.Clarifications.Should().Equal(
            "User follow-up: message 5",
            "User follow-up: message 6",
            "User follow-up: message 7",
            "User follow-up: message 8");
    }

    [Fact]
    public void Extract_WindowStart_ExcludesEarlierConversation()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "unrelated earlier chat"),
            new(ChatRole.Assistant, "ok"),
            new(ChatRole.User, "the real task"),
        ];

        PlanHandoffExtractor.Extract(history, windowStart: 2).OriginalRequest.Should().Be("the real task");
    }

    [Fact]
    public void Extract_ExecutionTriggerMessage_IsNotUserIntent()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "the real task"),
            new(ChatRole.User, "Execute approved plan p1 revision 1 now."),
        ];

        var handoff = PlanHandoffExtractor.Extract(history);

        handoff.OriginalRequest.Should().Be("the real task");
        handoff.Clarifications.Should().BeEmpty();
    }

    [Fact]
    public void Extract_HugePastedMessage_IsTruncatedBecauseItIsInjectedEveryTurn()
    {
        var history = new List<ChatMessage> { new(ChatRole.User, new string('x', 10_000)) };

        var handoff = PlanHandoffExtractor.Extract(history);

        handoff.OriginalRequest.Length.Should().BeInRange(2_000, 2_001, "2000 字符上限加一个省略号");
    }

    [Fact]
    public void Extract_NoUserMessages_ReturnsEmptyHandoff()
    {
        var handoff = PlanHandoffExtractor.Extract([new ChatMessage(ChatRole.Assistant, "hello")]);

        handoff.OriginalRequest.Should().BeEmpty();
        handoff.Clarifications.Should().BeEmpty();
    }

    [Fact]
    public void Extract_AfterPriorPlanExecutionTrigger_StartsFromNewPlanUserIntent()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "build feature A"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-old-q", "AskUserQuestion")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-old-q", "Old Q -> Old A")]),
            new(ChatRole.User, "Execute approved plan plan-old revision 1 now."),
            new(ChatRole.Assistant, [new FunctionCallContent("call-exec-q", "AskUserQuestion")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-exec-q", "Exec Q -> Exec A")]),
            new(ChatRole.Assistant, "feature A completed"),
            new(ChatRole.User, "now build feature B"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-new-q", "AskUserQuestion")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-new-q", "New Q -> New A")]),
        ];

        var handoff = PlanHandoffExtractor.Extract(history, windowStart: 0, currentPlanId: "plan-new");

        handoff.OriginalRequest.Should().Be("now build feature B",
            "prior plan execution trigger marks the boundary of the earlier generation");
        handoff.Clarifications.Should().Equal("Q&A: New Q -> New A");
    }

    [Fact]
    public void Extract_SamePlanReApproval_PreservesOriginalRequestAndExcludesExecutionTurnQa()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.User, "original task for plan-1"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-plan-q", "AskUserQuestion")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-plan-q", "DB? -> Postgres")]),
            new(ChatRole.User, "Execute approved plan plan-1 revision 1 now."),
            new(ChatRole.Assistant, [new FunctionCallContent("call-exec-q", "AskUserQuestion")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-exec-q", "Runtime Q -> Runtime A")]),
            new(ChatRole.User, "Also add retry logic before re-approving"),
        ];

        var handoff = PlanHandoffExtractor.Extract(history, windowStart: 0, currentPlanId: "plan-1");

        handoff.OriginalRequest.Should().Be("original task for plan-1",
            "re-approving a revised revision of the same plan must not discard its initial user request");
        handoff.Clarifications.Should().Equal(
            "Q&A: DB? -> Postgres",
            "User follow-up: Also add retry logic before re-approving");
    }
}
