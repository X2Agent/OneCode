using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="TranscriptEventPresenter"/> — 覆盖 Agent 工具结果的
/// 发言块提取契约（后台信封反面场景是守卫重点：无 result 文本时不得渲染发言块）。
/// TryPresent 的完整路由依赖 Terminal.Gui 视图，归集成路径。
/// </summary>
public sealed class TranscriptEventPresenterTests
{
    [Fact]
    public void TryExtractAgentSpeech_SyncSuccessEnvelope_ExtractsAgentNameAndResult()
    {
        const string raw = """{"agent":"Explore","conversationId":"abc","result":"findings line 1\nline 2","turnsCompleted":3,"maxTurnsReached":false,"toolCalls":12}""";

        var ok = TranscriptEventPresenter.TryExtractAgentSpeech(raw, out var agentName, out var speech);

        ok.Should().BeTrue();
        agentName.Should().Be("Explore");
        speech.Should().Be("findings line 1\nline 2");
    }

    [Fact]
    public void TryExtractAgentSpeech_BackgroundEnvelope_ReturnsFalse()
    {
        // 守卫：后台模式信封 {background:true, taskId, ...} 无 result 文本——
        // 渲染发言块会把空内容平铺到对话流，必须跳过。
        const string raw = """{"background":true,"taskId":"t-1","agent":"Explore","status":"started"}""";

        TranscriptEventPresenter.TryExtractAgentSpeech(raw, out _, out _)
            .Should().BeFalse("background forks have no result text yet — no speech block may be rendered");
    }

    [Fact]
    public void TryExtractAgentSpeech_NullOrEmptyResultText_ReturnsFalse()
    {
        const string raw = """{"agent":"Explore","conversationId":"abc","result":null,"turnsCompleted":1,"maxTurnsReached":false}""";

        TranscriptEventPresenter.TryExtractAgentSpeech(raw, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain error text, not json")]
    [InlineData("{\"not valid json")]
    public void TryExtractAgentSpeech_NonEnvelopeInput_ReturnsFalse(string? raw)
    {
        TranscriptEventPresenter.TryExtractAgentSpeech(raw, out _, out _).Should().BeFalse();
    }
}
