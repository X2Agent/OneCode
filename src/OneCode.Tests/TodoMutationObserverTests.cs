using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;

namespace OneCode.Tests;

/// <summary>
/// 待办变更观察：只有 add/complete/remove 的<b>结果</b>到达时才报告变更——这是宿主在工具边界
/// 重读待办快照的唯一触发信号，漏报会让侧边栏停滞，误报只会多读一次快照。
/// </summary>
public sealed class TodoMutationObserverTests
{
    private static AgentResponseUpdate Update(params AIContent[] contents) => new(ChatRole.Assistant, contents);

    [Theory]
    [InlineData("todos_add")]
    [InlineData("todos_complete")]
    [InlineData("todos_remove")]
    public void Observe_MutationResultAfterCall_ReportsMutation(string toolName)
    {
        var sut = new TodoMutationObserver();

        sut.Observe(Update(new FunctionCallContent("c1", toolName))).Should().BeFalse("a call alone is not yet a change");
        sut.Observe(Update(new FunctionResultContent("c1", "ok"))).Should().BeTrue();
    }

    [Theory]
    [InlineData("todos_get_all")]
    [InlineData("todos_get_remaining")]
    [InlineData("Read")]
    public void Observe_NonMutatingToolResult_ReportsNothing(string toolName)
    {
        var sut = new TodoMutationObserver();

        sut.Observe(Update(new FunctionCallContent("c1", toolName)));

        sut.Observe(Update(new FunctionResultContent("c1", "ok"))).Should().BeFalse();
    }

    [Fact]
    public void Observe_CallSeenOnlyInsideApprovalRequest_StillReportsMutationWhenResultArrives()
    {
        var sut = new TodoMutationObserver();
        var call = new FunctionCallContent("c1", "todos_complete");

        sut.Observe(Update(new ToolApprovalRequestContent("req1", call)));

        sut.Observe(Update(new FunctionResultContent("c1", "ok"))).Should().BeTrue(
            "after approval the call only reappears as a result in a later iteration of the stream");
    }

    [Fact]
    public void Observe_ResultReportedOnce_AndUnknownResultIgnored()
    {
        var sut = new TodoMutationObserver();
        sut.Observe(Update(new FunctionCallContent("c1", "todos_add")));

        sut.Observe(Update(new FunctionResultContent("never-seen", "ok"))).Should().BeFalse();
        sut.Observe(Update(new FunctionResultContent("c1", "ok"))).Should().BeTrue();
        sut.Observe(Update(new FunctionResultContent("c1", "ok"))).Should().BeFalse("one call yields one change");
    }
}
