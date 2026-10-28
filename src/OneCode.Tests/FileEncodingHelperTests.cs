using System.Text;
using OneCode.Infrastructure.Text;

namespace OneCode.Tests;

public sealed class FileEncodingHelperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"onecode-encoding-{Guid.NewGuid():N}");

    [Fact]
    public async Task WriteWithEncodingAsync_ExistingFile_PreservesMetadata()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "sample.cs");
        await File.WriteAllTextAsync(path, "before", TestContext.Current.CancellationToken);
        var creationTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, creationTime);
        var attributes = File.GetAttributes(path) | FileAttributes.Hidden;
        File.SetAttributes(path, attributes);

        await FileEncodingHelper.WriteWithEncodingAsync(
            path, "after", new UTF8Encoding(false), TestContext.Current.CancellationToken);

        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("after");
        File.GetCreationTimeUtc(path).Should().Be(creationTime);
        File.GetAttributes(path).Should().Be(attributes);
    }

    [Fact]
    public async Task WriteWithEncodingAsync_StaleTemporaryFile_IsRemovedWithoutTouchingRecentFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "sample.cs");
        var stale = $"{path}.tmp-stale";
        var recent = $"{path}.tmp-recent";
        await File.WriteAllTextAsync(stale, "stale", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(recent, "recent", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        await FileEncodingHelper.WriteWithEncodingAsync(
            path, "content", new UTF8Encoding(false), TestContext.Current.CancellationToken);

        File.Exists(stale).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("content");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
