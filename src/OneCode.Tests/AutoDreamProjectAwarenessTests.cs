using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.AutoDream;
using OneCode.App.Session;
using OneCode.Core.Config;
using OneCode.Core.Domain;
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
            "Project={{project_root}}\nSince={{since}}\nWM={{working_memory_section}}\nConv={{current_conversation_section}}"));

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

        var statePath = _service.GetStateFilePath("last_consolidated_at");

        statePath.Should().StartWith(_globalConfigDir, "empty working dir should fall back to global");
    }

    [Fact]
    public void SetLastConsolidatedAt_WritesToProjectDirectory()
    {
        var time = DateTimeOffset.UtcNow;

        _service.SetLastConsolidatedAt(time);

        var stateFile = Path.Combine(_projectA, ".onecode", "memory", "last_consolidated_at");
        File.Exists(stateFile).Should().BeTrue("state file should be written to project directory");
        var readBack = _service.GetLastConsolidatedAt();
        readBack.Should().BeCloseTo(time, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void StateFiles_AreIsolatedBetweenProjects()
    {
        _wdAccessor.WorkingDirectory.Returns(_projectA);
        var timeA = DateTimeOffset.UtcNow;
        _service.SetLastConsolidatedAt(timeA);

        _wdAccessor.WorkingDirectory.Returns(_projectB);
        var timeB = timeA.AddHours(12);
        _service.SetLastConsolidatedAt(timeB);

        _wdAccessor.WorkingDirectory.Returns(_projectA);
        var readA = _service.GetLastConsolidatedAt();
        readA.Should().BeCloseTo(timeA, TimeSpan.FromSeconds(1), "projectA state should be isolated from projectB");

        _wdAccessor.WorkingDirectory.Returns(_projectB);
        var readB = _service.GetLastConsolidatedAt();
        readB.Should().BeCloseTo(timeB, TimeSpan.FromSeconds(1), "projectB should have its own state");
    }

    // Current conversation delta & turn extraction

    [Fact]
    public void HasCurrentConversationDeltaSince_FiltersByProjectWorkingDirectory()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-10);
        var convA = CreateConversation(_projectA, since.AddMinutes(2), "user question", "assistant answer");
        _sessionManager.ForegroundConversation.Returns(convA);

        _wdAccessor.WorkingDirectory.Returns(_projectA);
        _service.HasCurrentConversationDeltaSince(since).Should().BeTrue(
            "active conversation for projectA has turns newer than since");

        _wdAccessor.WorkingDirectory.Returns(_projectB);
        _service.HasCurrentConversationDeltaSince(since).Should().BeFalse(
            "conversation belonging to projectA must not trigger consolidation for projectB");
    }

    [Fact]
    public void HasCurrentConversationDeltaSince_OnlyDetectsMessagesAfterSince()
    {
        var oldTime = DateTimeOffset.UtcNow.AddHours(-2);
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var conv = CreateConversation(_projectA, oldTime, "old question", "old answer");
        _sessionManager.ForegroundConversation.Returns(conv);

        _service.HasCurrentConversationDeltaSince(since).Should().BeFalse(
            "messages older than since must not trigger incremental consolidation");

        conv.Messages.Add(new UserMessage("u-new", "new question", DateTimeOffset.UtcNow));
        _service.HasCurrentConversationDeltaSince(since).Should().BeTrue(
            "new user message after since must trigger incremental consolidation");
    }

    [Fact]
    public void CollectCurrentConversationTurnsSince_ExtractsBothUserAndAssistantTurns_AndHandlesTrailingSlash()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-10);
        var conv = CreateConversation(
            _projectA,
            since.AddMinutes(1),
            userText: "wrong build command was used",
            assistantText: "Use dotnet build src/OneCode.slnx instead");
        _sessionManager.ForegroundConversation.Returns(conv);

        _wdAccessor.WorkingDirectory.Returns(_projectA + Path.DirectorySeparatorChar);
        var turns = _service.CollectCurrentConversationTurnsSince(since);

        turns.Should().Equal(
            "User: wrong build command was used",
            "Assistant: Use dotnet build src/OneCode.slnx instead");
    }

    [Fact]
    public async Task TryConsolidateAsync_WhenOutputIsNotValidJsonArray_DoesNotUpdateLastConsolidatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var initialSince = DateTimeOffset.UtcNow.AddHours(-1);
        _service.SetLastConsolidatedAt(initialSince);

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
        _service.GetLastConsolidatedAt().Should().BeCloseTo(
            initialSince,
            TimeSpan.FromSeconds(1),
            "last_consolidated_at must not advance when output parsing fails so the next task completion retries");
    }

    [Fact]
    public async Task TryConsolidateAsync_WhenOutputIsValidEmptyJsonArray_UpdatesLastConsolidatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var initialSince = DateTimeOffset.UtcNow.AddHours(-1);
        _service.SetLastConsolidatedAt(initialSince);

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
        _service.GetLastConsolidatedAt().Should().BeAfter(
            initialSince.AddMinutes(30),
            "last_consolidated_at must advance after a valid [] consolidation response");
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
