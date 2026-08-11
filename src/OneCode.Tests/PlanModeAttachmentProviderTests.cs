using System.Reflection;
using Microsoft.Agents.AI;
using NSubstitute;
using OneCode.App.Services.PlanMode;

namespace OneCode.Tests;

public sealed class PlanModeAttachmentProviderTests
{
    private readonly IPlanModeService _planMode;
    private readonly PlanModeAttachmentProvider _sut;

    public PlanModeAttachmentProviderTests()
    {
        _planMode = Substitute.For<IPlanModeService>();
        _sut = new PlanModeAttachmentProvider(_planMode);
    }

    [Fact]
    public async Task ProvideAIContextAsync_NotInPlanMode_ReturnsEmptyContext()
    {
        _planMode.IsInPlanMode.Returns(false);

        var result = await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);

        result.Messages.Should().BeNull();
    }

    [Fact]
    public async Task ProvideAIContextAsync_Turn1_ReturnsEmptyContext()
    {
        // Turn 1 由 AgentModeProvider 注入完整指令，
        // AttachmentProvider 不再重复注入。
        _planMode.IsInPlanMode.Returns(true);
        _planMode.GetWorkflowInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns("FULL WORKFLOW: Phase 1-5 with sub-agent limits");

        var result = await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);

        result.Messages.Should().BeNull();
        // Turn 1 不应调用任何 attachment 方法
        // 使用弃元丢弃未 await 的 Task，避免 CS4014（项目将 CS4014 视为错误）
        _ = _planMode.DidNotReceive().GetWorkflowInstructionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvideAIContextAsync_Turn2Onwards_UsesIncrementalReminder()
    {
        _planMode.IsInPlanMode.Returns(true);
        _planMode.GetWorkflowInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns("full-instructions");

        // Turn 1 由 AgentModeProvider 注入，返回空 context
        await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);
        // Turn 2 使用内联 sparse reminder
        var r2 = await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);

        r2.Messages!.Single().Text.Should().Contain("Turn 2");
        r2.Messages!.Single().Text.Should().Contain("Continue planning");
    }

    [Fact]
    public async Task ProvideAIContextAsync_Turn1WithNullInstructions_ReturnsEmptyContext()
    {
        _planMode.IsInPlanMode.Returns(true);
        _planMode.GetWorkflowInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);

        result.Messages.Should().BeNull();
    }

    [Fact]
    public async Task ProvideAIContextAsync_RepeatedCalls_IncrementTurnCount()
    {
        _planMode.IsInPlanMode.Returns(true);
        _planMode.GetWorkflowInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns("full-instructions");

        var r1 = await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);
        var r2 = await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);
        var r3 = await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);

        // Turn 1 返回空 context（由 AgentModeProvider 注入）
        r1.Messages.Should().BeNull();
        r2.Messages!.Single().Text.Should().Contain("Turn 2");
        r3.Messages!.Single().Text.Should().Contain("Turn 3");
    }

    [Fact]
    public async Task ResetTurnCount_AfterIncrement_NextCallUsesTurnOne()
    {
        _planMode.IsInPlanMode.Returns(true);
        _planMode.GetWorkflowInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns("full-instructions");

        await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);
        await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);

        _sut.ResetTurnCount();

        var result = await InvokeProvideAIContextAsync(_sut, TestContext.Current.CancellationToken);
        // 重置后再次进入 Turn 1，由 AgentModeProvider 注入，返回空 context
        result.Messages.Should().BeNull();
    }

    /// <summary>
    /// Invokes the protected <c>PlanModeAttachmentProvider.ProvideAIContextAsync</c>
    /// via reflection. The <c>InvokingContext</c> argument is passed as <c>null</c> because
    /// the SUT never reads it — it only consults <see cref="IPlanModeService"/>.
    /// </summary>
    private static async Task<AIContext> InvokeProvideAIContextAsync(
        PlanModeAttachmentProvider provider, CancellationToken ct)
    {
        var method = typeof(PlanModeAttachmentProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (ValueTask<AIContext>)method.Invoke(provider, new object?[] { null, ct })!;
        return await task;
    }
}
