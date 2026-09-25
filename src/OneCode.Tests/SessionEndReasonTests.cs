using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;

namespace OneCode.Tests;

/// <summary>
/// 会话退出覆盖：CloseAsync(reason) 仍按原因收尾，但 SessionEnd 已退出 Hook 协议
/// （产品会话生命周期不是 agent execution seam），此处只验证状态与持久化行为。
/// </summary>
public sealed class SessionEndReasonTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionManager _manager;

    public SessionEndReasonTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SessionEndReasonTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _manager = new SessionManager(
            new EventSourcedSessionStore(new FileSessionEventStore(_tempDir)),
            NullLogger<SessionManager>.Instance,
            _tempDir,
            shellExecutorCleanup: Substitute.For<IShellExecutorCleanup>(),
            tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
            sessionIdHolder: new SessionIdHolder(),
            sessionToolSetManager: Substitute.For<OneCode.App.Query.ISessionToolSetManager>());
    }

    [Fact]
    public async Task CloseAsync_WithoutReason_MarksConversationCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-a"), ct);

        await _manager.CloseAsync(ct);

        conversation.Status.Should().Be(ConversationStatus.Completed);
        _manager.CurrentSessionId.Should().BeNull();
    }

    [Fact]
    public async Task CloseAsync_WithPromptInputExit_MarksConversationCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-a"), ct);

        await _manager.CloseAsync(SessionEndReason.PromptInputExit, ct);

        conversation.Status.Should().Be(ConversationStatus.Completed);
        _manager.CurrentSessionId.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_MarksConversationCompletedAsFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-a"), ct);

        await _manager.DisposeAsync();

        conversation.Status.Should().Be(ConversationStatus.Completed);
        _manager.CurrentSessionId.Should().BeNull();
    }

    [Fact]
    public async Task CloseAsync_Twice_SecondIsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var conversation = await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-a"), ct);

        await _manager.CloseAsync(SessionEndReason.PromptInputExit, ct);
        await _manager.CloseAsync(SessionEndReason.Other, ct);

        conversation.Status.Should().Be(ConversationStatus.Completed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* 临时目录清理失败可忽略 */ }
    }
}
