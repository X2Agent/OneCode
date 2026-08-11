using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services;
using OneCode.Infrastructure;

namespace OneCode.Tests;

/// <summary>
/// Verifies deferred review-cache commit: hashes are persisted only after
/// <see cref="ReviewCacheService.CommitPending"/>, not after Schedule alone.
/// </summary>
public sealed class ReviewCacheServiceTests
{
    [Fact]
    public void CommitPending_AfterSchedule_PersistsHashes()
    {
        var baseRef = "test-" + Guid.NewGuid().ToString("N");
        var sut = new ReviewCacheService(NullLogger<ReviewCacheService>.Instance);
        try
        {
            sut.ScheduleCommit(baseRef, ["abc123", "def456"]);
            sut.CommitPending();

            var loaded = sut.Load(baseRef);
            loaded.IsReviewed("abc123").Should().BeTrue();
            loaded.IsReviewed("def456").Should().BeTrue();
            loaded.FilterNewCommits(["abc123", "zzz"]).Should().Equal("zzz");
        }
        finally
        {
            TryDeleteCacheFile(baseRef);
        }
    }

    [Fact]
    public void DiscardPending_DoesNotPersistHashes()
    {
        var baseRef = "test-" + Guid.NewGuid().ToString("N");
        var sut = new ReviewCacheService(NullLogger<ReviewCacheService>.Instance);
        try
        {
            sut.ScheduleCommit(baseRef, ["abc123"]);
            sut.DiscardPending();
            sut.CommitPending();

            var loaded = sut.Load(baseRef);
            loaded.IsReviewed("abc123").Should().BeFalse();
        }
        finally
        {
            TryDeleteCacheFile(baseRef);
        }
    }

    private static void TryDeleteCacheFile(string baseRef)
    {
        try
        {
            var path = Path.Combine(
                PathsHelper.GetUserConfigDir(),
                "review-cache",
                PathsHelper.SanitizeFileName(baseRef) + ".json");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup — test assertions already ran.
        }
    }
}
