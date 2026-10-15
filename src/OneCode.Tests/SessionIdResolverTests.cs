using OneCode.App.Session;
using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// SessionIdResolver 纯函数行为：完整 id 直通、前缀唯一命中/歧义/零命中。
/// </summary>
public sealed class SessionIdResolverTests
{
    private const string FullId = "4f8a90c1e0d244ef8dba35b2588c0424";
    private const string OtherId = "4f8a90c1ffffffffffffffffffffffff";
    private const string UnrelatedId = "1a2b3c4d5e6f7a889900112233445566";

    private static ConversationSummary Summary(string fullId) =>
        new(new SessionId(fullId), null, "gpt-5", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void Resolve_FullId_ParsesDirectly()
    {
        var candidates = new[] { Summary(FullId) };

        var resolution = SessionIdResolver.Resolve(FullId, candidates);

        resolution.Id.Should().NotBeNull();
        resolution.Id!.Value.Value.Should().Be(FullId);
        resolution.Error.Should().BeNull();
        resolution.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_UniquePrefix_ReturnsFullId()
    {
        var candidates = new[] { Summary(FullId), Summary(UnrelatedId) };

        var resolution = SessionIdResolver.Resolve("4f8a90c1e0", candidates);

        resolution.Id!.Value.Value.Should().Be(FullId);
        resolution.Error.Should().BeNull();
    }

    [Fact]
    public void Resolve_AmbiguousPrefix_ReturnsAllCandidates()
    {
        var candidates = new[] { Summary(FullId), Summary(OtherId), Summary(UnrelatedId) };

        var resolution = SessionIdResolver.Resolve("4f8a", candidates);

        resolution.Id.Should().BeNull();
        resolution.Error.Should().BeNull();
        resolution.Candidates.Should().HaveCount(2);
        resolution.Candidates.Select(c => c.Id.Value)
            .Should().BeEquivalentTo([FullId, OtherId]);
    }

    [Fact]
    public void Resolve_ZeroMatch_ReturnsNotFound()
    {
        var candidates = new[] { Summary(FullId) };

        var resolution = SessionIdResolver.Resolve("deadbeef", candidates);

        resolution.Id.Should().BeNull();
        resolution.Error.Should().Contain("not found");
    }
}
