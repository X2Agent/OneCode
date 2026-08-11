using OneCode.App.Services.Compact;
using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// MafSessionInvalidator epoch 计数器与 session 失效协同的单测。
/// </summary>
public sealed class MafSessionInvalidatorTests
{
    private const string MafSessionKey = "mafSession";

    [Fact]
    public void Invalidate_RemovesMafSessionKey()
    {
        var conv = new Conversation();
        conv.Metadata[MafSessionKey] = "{\"dummy\":true}";

        MafSessionInvalidator.Invalidate(conv, "test.clear");

        conv.Metadata.ContainsKey(MafSessionKey).Should().BeFalse();
    }

    [Fact]
    public void Invalidate_IncrementsHistoryEpoch()
    {
        var conv = new Conversation();

        MafSessionInvalidator.Invalidate(conv, "first");
        var epoch1 = MafSessionInvalidator.GetHistoryEpoch(conv);

        MafSessionInvalidator.Invalidate(conv, "second");
        var epoch2 = MafSessionInvalidator.GetHistoryEpoch(conv);

        epoch2.Should().Be(epoch1 + 1);
    }

    [Fact]
    public void GetHistoryEpoch_ReturnsZero_WhenNotInitialized()
    {
        var conv = new Conversation();
        MafSessionInvalidator.GetHistoryEpoch(conv).Should().Be(0);
    }

    [Fact]
    public void Invalidate_RecordsAuditMetadata()
    {
        var conv = new Conversation();
        var before = DateTimeOffset.UtcNow;

        MafSessionInvalidator.Invalidate(conv, "compact.full");

        conv.Metadata["lastMafSessionInvalidationSource"].Should().Be("compact.full");
        var ts = conv.Metadata["lastMafSessionInvalidatedAt"].ToString();
        ts.Should().NotBeNullOrEmpty();
        DateTimeOffset.TryParse(ts, out var parsed).Should().BeTrue();
        parsed.Should().BeOnOrAfter(before);
    }
}
