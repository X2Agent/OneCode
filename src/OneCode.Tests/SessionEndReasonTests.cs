using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Observability;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;

namespace OneCode.Tests;

/// <summary>
/// A7：SessionEnd 退出覆盖——CloseAsync(reason) 将 reason 作为 matcher 值触发 SessionEnd。
/// </summary>
public sealed class SessionEndReasonTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionManager _manager;
    private readonly IHookExecutionService _hooks;

    public SessionEndReasonTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SessionEndReasonTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _hooks = Substitute.For<IHookExecutionService>();
        _manager = new SessionManager(
            new EventSourcedSessionStore(new FileSessionEventStore(_tempDir)),
            NullLogger<SessionManager>.Instance,
            _tempDir,
            hookExecutionService: _hooks,
            shellExecutorCleanup: Substitute.For<IShellExecutorCleanup>(),
            tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
            sessionIdHolder: new SessionIdHolder(),
            sessionToolSetManager: Substitute.For<OneCode.App.Query.ISessionToolSetManager>());
    }

    [Fact]
    public async Task CloseAsync_WithoutReason_FiresSessionEndWithClose()
    {
        var ct = TestContext.Current.CancellationToken;
        await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-a"), ct);

        await _manager.CloseAsync(ct);

        await _hooks.Received(1).FireAsync(
            Arg.Is<HookPayload>(p => p.Event == HookEvent.SessionEnd),
            Arg.Is<string?>(m => m == SessionEndReason.Close),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseAsync_WithPromptInputExit_FiresSessionEndWithReason()
    {
        var ct = TestContext.Current.CancellationToken;
        await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-a"), ct);

        await _manager.CloseAsync(SessionEndReason.PromptInputExit, ct);

        await _hooks.Received(1).FireAsync(
            Arg.Is<HookPayload>(p => p.Event == HookEvent.SessionEnd),
            Arg.Is<string?>(m => m == SessionEndReason.PromptInputExit),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_FiresSessionEndWithOtherAsFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-a"), ct);

        await _manager.DisposeAsync();

        await _hooks.Received(1).FireAsync(
            Arg.Is<HookPayload>(p => p.Event == HookEvent.SessionEnd),
            Arg.Is<string?>(m => m == SessionEndReason.Other),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloseAsync_Twice_SecondIsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await _manager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "model-a"), ct);

        await _manager.CloseAsync(SessionEndReason.PromptInputExit, ct);
        await _manager.CloseAsync(SessionEndReason.Other, ct);

        await _hooks.Received(1).FireAsync(
            Arg.Is<HookPayload>(p => p.Event == HookEvent.SessionEnd),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* 临时目录清理失败可忽略 */ }
    }
}
