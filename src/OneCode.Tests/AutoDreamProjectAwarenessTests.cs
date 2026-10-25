using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.AutoDream;
using OneCode.App.Session;
using OneCode.Core.Config;
using OneCode.Core.Memory;
using OneCode.Core.Tools;
using OneCode.Core.Models;
using OneCode.Core.Prompt;

namespace OneCode.Tests;

/// <summary>
/// Tests for AutoDream project-awareness: state file isolation, current in-memory conversation
/// turn extraction filtered by working directory, and consolidation timestamp retry semantics.
/// </summary>
public sealed class AutoDreamProjectAwarenessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _globalConfigDir;
    private readonly string _projectA;
    private readonly string _projectB;
    private readonly IWorkingDirectoryAccessor _wdAccessor;
    private readonly ISessionManager _sessionManager;
    private readonly IChatClient _chatClient;
    private readonly IModelManager _modelManager;
    private readonly PromptManager _promptManager;
    private readonly AutoDreamService _service;

    public AutoDreamProjectAwarenessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AutoDream_{Guid.NewGuid():N}");
        _globalConfigDir = Path.Combine(_tempDir, "home", ".onecode");
        _projectA = Path.Combine(_tempDir, "projectA");
        _projectB = Path.Combine(_tempDir, "projectB");
        Directory.CreateDirectory(_projectA);
        Directory.CreateDirectory(_projectB);

        _wdAccessor = Substitute.For<IWorkingDirectoryAccessor>();
        _wdAccessor.WorkingDirectory.Returns(_projectA);

        _sessionManager = Substitute.For<ISessionManager>();
        _chatClient = Substitute.For<IChatClient>();
        _modelManager = Substitute.For<IModelManager>();
        _modelManager.GetFastModel().Returns(new ModelInfo("fast-model", "openai", "fast-model", 4096, null, 128_000));

        _promptManager = new PromptManager();
        _promptManager.RegisterTemplate(new PromptTemplate(
            "system/autodream-consolidation",
            "Project={{project_root}}\nWMRoot={{working_memory_root}}\nWM={{working_memory_section}}\nConv={{current_conversation_section}}"));

        var configManager = Substitute.For<IConfigManager>();
        configManager.GetSetting("autodream.enabled", true).Returns(true);

        _service = new AutoDreamService(
            NullLogger<AutoDreamService>.Instance,
            NullLoggerFactory.Instance,
            agent: new AutoDreamAgentDependencies(_chatClient, _modelManager, _promptManager),
            storage: new AutoDreamStorageDependencies(
                Substitute.For<IMemoryEntryStore>(),
                configManager,
                _wdAccessor,
                _sessionManager),
            globalConfigDirOverride: _globalConfigDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"Cleanup failed: {ex.Message}"); }
    }

    // State file location

    [Fact]
    public void GetProjectStateDir_ReturnsProjectMemorySubdir()
    {
        var stateDir = _service.GetProjectStateDir();

        stateDir.Should().Be(Path.Combine(_projectA, ".onecode", "memory"));
    }

    [Fact]
    public void GetStateFilePath_StoredInProjectDirectory()
    {
        var lockPath = _service.GetStateFilePath("autodream.lock");

        lockPath.Should().Be(Path.Combine(_projectA, ".onecode", "memory", "autodream.lock"));
        lockPath.Should().NotStartWith(_globalConfigDir, "state files must NOT be in the global config dir");
    }

    [Fact]
    public void GetStateFilePath_FallsBackToGlobal_WhenWorkingDirIsEmpty()
    {
        _wdAccessor.WorkingDirectory.Returns("");

        var statePath = _service.GetStateFilePath("autodream.lock");

        statePath.Should().StartWith(_globalConfigDir, "empty working dir should fall back to global");
    }

    [Fact]
    public void LockFiles_AreIsolatedBetweenProjects()
    {
        var store = new AutoDreamStateStore(
            NullLogger.Instance,
            _service.GetProjectStateDir);

        _wdAccessor.WorkingDirectory.Returns(_projectA);
        using (var lockA = store.TryAcquireConsolidationLock())
        {
            lockA.Should().NotBeNull("first lock acquisition in projectA should succeed");
            File.Exists(Path.Combine(_projectA, ".onecode", "memory", "autodream.lock")).Should().BeTrue();
            store.TryAcquireConsolidationLock().Should().BeNull("concurrent acquisition in the same projectA must be blocked");

            _wdAccessor.WorkingDirectory.Returns(_projectB);
            using var lockB = store.TryAcquireConsolidationLock();
            lockB.Should().NotBeNull("projectA lock must not block consolidation in projectB");
            File.Exists(Path.Combine(_projectB, ".onecode", "memory", "autodream.lock")).Should().BeTrue();
        }
    }

    // Current conversation turn extraction & working-directory isolation

    [Fact]
    public void HasCurrentConversationTurns_FiltersByProjectWorkingDirectory()
    {
        var now = DateTimeOffset.UtcNow;
        var convA = CreateConversation(_projectA, now, "user question", "assistant answer");
        _sessionManager.ForegroundConversation.Returns(convA);

        _wdAccessor.WorkingDirectory.Returns(_projectA);
        _service.HasCurrentConversationTurns().Should().BeTrue(
            "active conversation for projectA has user/assistant turns");

        _wdAccessor.WorkingDirectory.Returns(_projectB);
        _service.HasCurrentConversationTurns().Should().BeFalse(
            "conversation belonging to projectA must not trigger consolidation for projectB");
    }

    [Fact]
    public void CollectCurrentConversationTurns_PreservesCrossTurnContext_AndHandlesTrailingSlash()
    {
        var turn1Time = DateTimeOffset.UtcNow.AddMinutes(-30);
        var conv = CreateConversation(
            _projectA,
            turn1Time,
            userText: "run the build",
            assistantText: "I ran dotnet build without solution path and it failed");
        conv.Messages.Add(new UserMessage(
            Guid.NewGuid().ToString("N"),
            "No, always pass src/OneCode.slnx",
            DateTimeOffset.UtcNow));
        _sessionManager.ForegroundConversation.Returns(conv);

        _wdAccessor.WorkingDirectory.Returns(_projectA + Path.DirectorySeparatorChar);
        var turns = _service.CollectCurrentConversationTurns();

        turns.Should().Equal(
            "User: run the build",
            "Assistant: I ran dotnet build without solution path and it failed",
            "User: No, always pass src/OneCode.slnx");
    }

    [Fact]
    public async Task BuildPromptAsync_WhenWorkingDirectoryIsEmpty_DoesNotScanLiteralUnknownDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        _wdAccessor.WorkingDirectory.Returns("");
        var conv = CreateConversation("", DateTimeOffset.UtcNow, "hello", "world");
        _sessionManager.ForegroundConversation.Returns(conv);

        string? capturedPrompt = null;
        _chatClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedPrompt = msgs.LastOrDefault()?.Text),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        var succeeded = await _service.TryConsolidateAsync(force: true, ct);

        succeeded.Should().BeTrue();
        capturedPrompt.Should().Contain("Project=(unknown)");
        capturedPrompt.Should().Contain("WMRoot=(none)",
            "empty working directory must not resolve '(unknown)' as a relative path on disk");
    }

    [Fact]
    public async Task TryConsolidateAsync_WhenOutputIsNotValidJsonArray_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = CreateConversation(_projectA, DateTimeOffset.UtcNow, "fix build", "done");
        _sessionManager.ForegroundConversation.Returns(conv);

        _chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I could not find any JSON array to output.")));

        var succeeded = await _service.TryConsolidateAsync(force: false, ct);

        succeeded.Should().BeFalse("invalid non-JSON output must be treated as a failed consolidation");
    }

    [Fact]
    public async Task TryConsolidateAsync_WhenOutputIsValidEmptyJsonArray_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = CreateConversation(_projectA, DateTimeOffset.UtcNow, "hello", "world");
        _sessionManager.ForegroundConversation.Returns(conv);

        _chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        var succeeded = await _service.TryConsolidateAsync(force: false, ct);

        succeeded.Should().BeTrue("a valid empty JSON array [] means consolidation succeeded with zero changes");
    }

    private static Conversation CreateConversation(
        string workingDirectory,
        DateTimeOffset messageTime,
        string userText,
        string assistantText)
    {
        var conversation = new Conversation
        {
            Id = SessionId.NewId(),
            Name = "test-session",
            WorkingDirectory = workingDirectory,
            Model = "test-model",
            CreatedAt = messageTime,
            LastActivityAt = messageTime,
        };
        conversation.Messages.Add(new UserMessage(Guid.NewGuid().ToString("N"), userText, messageTime));
        conversation.Messages.Add(new AssistantMessage(
            Guid.NewGuid().ToString("N"),
            [new TextBlock(assistantText)],
            messageTime));
        return conversation;
    }
}
