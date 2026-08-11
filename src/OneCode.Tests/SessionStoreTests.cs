using System.Diagnostics;
using OneCode.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Session;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="SessionStore"/> using a temporary directory as disk backend.
/// </summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionStore _sut;

    public SessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SessionStoreTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new SessionStore(basePath: _tempDir, logger: NullLogger<SessionStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { Debug.WriteLine($"SessionStoreTests Dispose best-effort cleanup failed for {_tempDir}: {ex.Message}"); }
    }

    // Save / Load roundtrip

    [Fact]
    public async Task SaveAndLoad_RoundTrip_PreservesConversationFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = CreateConversation();
        conv.Messages.Add(new UserMessage("m1", "Hello!", DateTimeOffset.UtcNow));
        conv.Messages.Add(new AssistantMessage("m2", [new TextBlock("Hi there!")], DateTimeOffset.UtcNow));

        await _sut.SaveAsync(conv, ct);
        var loaded = await _sut.LoadAsync(conv.Id, ct);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(conv.Id);
        loaded.Name.Should().Be(conv.Name);
        loaded.Model.Should().Be(conv.Model);
        loaded.Messages.Should().HaveCount(2);
        loaded.Messages[0].Should().BeOfType<UserMessage>()
            .Which.Content.Should().Be("Hello!");
        loaded.Messages[1].Should().BeOfType<AssistantMessage>();
    }

    [Fact]
    public async Task SaveAsync_OverwriteExisting_UpdatesFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = CreateConversation();
        conv.Messages.Add(new UserMessage("m1", "First", DateTimeOffset.UtcNow));
        await _sut.SaveAsync(conv, ct);

        conv.Messages.Add(new UserMessage("m2", "Second", DateTimeOffset.UtcNow));
        await _sut.SaveAsync(conv, ct);

        var loaded = await _sut.LoadAsync(conv.Id, ct);
        loaded!.Messages.Should().HaveCount(2);
    }

    // ListAsync

    [Fact]
    public async Task ListAsync_MultipleConversations_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv1 = CreateConversation("Session 1");
        var conv2 = CreateConversation("Session 2");
        await _sut.SaveAsync(conv1, ct);
        await _sut.SaveAsync(conv2, ct);

        var sessions = await _sut.ListAsync(ct);

        sessions.Should().HaveCount(2);
        sessions.Select(s => s.Name).Should().Contain("Session 1").And.Contain("Session 2");
    }

    [Fact]
    public async Task ListAsync_EmptyDirectory_ReturnsEmptyList()
    {
        var sessions = await _sut.ListAsync(TestContext.Current.CancellationToken);
        sessions.Should().BeEmpty();
    }

    // LoadAsync — non-existent

    [Fact]
    public async Task LoadAsync_NonExistentId_ReturnsNull()
    {
        var result = await _sut.LoadAsync(SessionId.NewId(), TestContext.Current.CancellationToken);
        result.Should().BeNull();
    }

    // LoadForResumeAsync

    [Fact]
    public async Task LoadForResumeAsync_ExistingSession_ReturnsResume()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = CreateConversation();
        conv.Messages.Add(new UserMessage("m1", "What is 2+2?", DateTimeOffset.UtcNow));
        await _sut.SaveAsync(conv, ct);

        var resume = await _sut.LoadForResumeAsync(conv.Id, ct);

        resume.Should().NotBeNull();
        resume!.SessionId.Should().Be(conv.Id);
        resume.Messages.Should().HaveCount(1);
    }

    [Fact]
    public async Task LoadForResumeAsync_NonExistentId_ReturnsNull()
    {
        var result = await _sut.LoadForResumeAsync(SessionId.NewId(), TestContext.Current.CancellationToken);
        result.Should().BeNull();
    }

    // Delete

    [Fact]
    public async Task Delete_ExistingSession_RemovesFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = CreateConversation();
        await _sut.SaveAsync(conv, ct);

        _sut.Delete(conv.Id);

        var loaded = await _sut.LoadAsync(conv.Id, ct);
        loaded.Should().BeNull();
    }

    // ListSessionsAsync

    [Fact]
    public async Task ListSessionsAsync_WithLimit_RespectsLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 5; i++)
        {
            var conv = CreateConversation($"Conv {i}");
            conv.Messages.Add(new UserMessage($"m{i}", $"User message {i}", DateTimeOffset.UtcNow));
            await _sut.SaveAsync(conv, ct);
        }

        var sessions = await _sut.ListSessionsAsync(limit: 3, ct: ct);

        sessions.Count.Should().BeLessThanOrEqualTo(3);
    }

    // Helpers

    private static Conversation CreateConversation(string name = "Test Conversation") => new()
    {
        Id = SessionId.NewId(),
        Name = name,
        WorkingDirectory = Directory.GetCurrentDirectory(),
        Model = "claude-3-5-sonnet-20241022",
        Status = ConversationStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        LastActivityAt = DateTimeOffset.UtcNow,
    };
}
