using System.Diagnostics;
using OneCode.App.Commands;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Core.Tools;
using OneCode.App.Services.Observability;
using OneCode.App.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Session;
using NSubstitute;

namespace OneCode.Tests;

/// <summary>
/// End-to-end verification for session persistence, multi-session, resume, and shell integration.
/// </summary>
public sealed class SessionFeatureE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sessionsDir;
    private readonly SessionStore _store;
    private readonly SessionManager _sessionManager;
    private readonly ConversationShellExecutorManager _shellManager;

    public SessionFeatureE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SessionE2E_{Guid.NewGuid():N}");
        _sessionsDir = Path.Combine(_tempDir, "sessions");
        Directory.CreateDirectory(_sessionsDir);

        _store = new SessionStore(basePath: _sessionsDir, NullLogger<SessionStore>.Instance);
        _shellManager = new ConversationShellExecutorManager(NullLogger<ConversationShellExecutorManager>.Instance);
        _sessionManager = new SessionManager(
            _store,
            NullLogger<SessionManager>.Instance,
            _tempDir,
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            shellExecutorCleanup: _shellManager,
            tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
            sessionIdHolder: new SessionIdHolder(),
            sessionToolSetManager: Substitute.For<OneCode.App.Query.ISessionToolSetManager>());
    }

    [Fact]
    public async Task E2E_SessionPersistence_RoundTripsThroughStoreAndSessionSwitch()
    {
        var ct = TestContext.Current.CancellationToken;

        var conv = await _sessionManager.EnsureActiveSessionAsync(
            new ConversationOptions(_tempDir, "test-model"), ct);
        await _sessionManager.AppendUserMessageAsync("first question", ct);
        await _sessionManager.AppendAssistantMessageAsync("first answer", new TokenUsage(11, 7), ct);

        var sessionId = conv.Id.Value;
        _sessionManager.ForegroundConversation!.Id.Should().Be(conv.Id);

        // Simulate new process: reload from disk
        var store2 = new SessionStore(basePath: _sessionsDir, NullLogger<SessionStore>.Instance);
        var manager2 = new SessionManager(store2, NullLogger<SessionManager>.Instance, _tempDir,
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            shellExecutorCleanup: Substitute.For<IShellExecutorCleanup>(),
            tokenUsageTracker: Substitute.For<ITokenUsageTracker>(),
            sessionIdHolder: new SessionIdHolder(),
            sessionToolSetManager: Substitute.For<OneCode.App.Query.ISessionToolSetManager>());

        var sessionCmd = new SessionCommand(manager2);
        var listResult = await sessionCmd.ExecuteAsync(["list"], ct);
        listResult.Should().BeOfType<CommandResult.TextResult>();
        ((CommandResult.TextResult)listResult).Value.Should().Contain(sessionId[..8]);

        var switchResult = await sessionCmd.ExecuteAsync(["switch", sessionId], ct);
        switchResult.Should().BeOfType<CommandResult.TextResult>();
        manager2.ForegroundConversation!.Messages.Should().HaveCount(2);
        manager2.GetForegroundChatHistory().Should().HaveCount(2);
    }

    [Fact]
    public async Task E2E_MultiSession_NewSwitchAndClose()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await _sessionManager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);
        await _sessionManager.AppendUserMessageAsync("background task", ct);
        var firstId = first.Id.Value;

        var sessionCmd = new SessionCommand(_sessionManager);
        // /session new backgrounds the current one and creates a new foreground session
        var newResult = await sessionCmd.ExecuteAsync(["new"], ct);
        newResult.Should().BeOfType<CommandResult.TextResult>();
        _sessionManager.BackgroundSessionCount.Should().Be(1);
        _sessionManager.ForegroundConversation!.Id.Should().NotBe(first.Id);

        var switchResult = await sessionCmd.ExecuteAsync(["switch", firstId], ct);
        switchResult.Should().BeOfType<CommandResult.TextResult>();
        _sessionManager.ForegroundConversation!.Id.Should().Be(first.Id);
        _sessionManager.BackgroundSessionCount.Should().Be(1);

        var closeResult = await sessionCmd.ExecuteAsync(["close", _sessionManager.BackgroundSessions[0].Conversation.Id.Value], ct);
        closeResult.Should().BeOfType<CommandResult.TextResult>();
        _sessionManager.BackgroundSessionCount.Should().Be(0);
    }

    [Fact]
    public async Task E2E_BashTool_PersistentShell_PreservesWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var ct = TestContext.Current.CancellationToken;
        var subDir = Path.Combine(_tempDir, "shell-subdir");
        Directory.CreateDirectory(subDir);

        await _sessionManager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir), ct);

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_tempDir);
        var bash = new BashTool(
            wd,
            ssh: null!,
            shellExecutorManager: _shellManager,
            sessionManager: _sessionManager);

        var cdCommand = OperatingSystem.IsWindows()
            ? $"Set-Location -Path '{subDir.Replace("'", "''")}'"
            : $"cd '{subDir.Replace("'", "'\\''")}'";

        var pwdCommand = OperatingSystem.IsWindows() ? "(Get-Location).Path" : "pwd";

        var cdResult = await bash.ExecuteAsync(cdCommand, timeout: 30, ct: ct);
        cdResult.Content.Should().Contain("Exit code: 0");

        var pwdResult = await bash.ExecuteAsync(pwdCommand, timeout: 30, ct: ct);
        pwdResult.Content.Should().Contain("Exit code: 0");
        pwdResult.Content.Should().Contain(Path.GetFileName(subDir));
    }

    [Fact]
    public async Task E2E_SessionStore_ListAndLoad_MatchesCliPsExpectations()
    {
        var ct = TestContext.Current.CancellationToken;

        await _sessionManager.EnsureActiveSessionAsync(new ConversationOptions(_tempDir, "e2e-model"), ct);
        await _sessionManager.AppendUserMessageAsync("cli ps check", ct);
        await _sessionManager.SaveAsync(ct);

        var listed = await _store.ListAsync(ct);
        listed.Should().NotBeEmpty();

        var loaded = await _store.LoadAsync(_sessionManager.ForegroundConversation!.Id, ct);
        loaded.Should().NotBeNull();
        loaded!.Messages.OfType<UserMessage>()
            .Should().ContainSingle(um => um.Content == "cli ps check");
    }

    public void Dispose()
    {
        try { _shellManager.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { Debug.WriteLine($"SessionFeatureE2ETests shell dispose failed: {ex.Message}"); }

        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"SessionFeatureE2ETests cleanup failed: {ex.Message}"); }
    }
}
