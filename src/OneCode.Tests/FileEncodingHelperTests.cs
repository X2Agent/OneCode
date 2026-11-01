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
        // Hidden 语义按平台不同：Unix 没有文件隐藏位，SetAttributes(Hidden) 实际不生效，
        // 断言「写回后属性不变」在 Linux 上会永远失败。只在属性真正可设置时才纳入断言。
        var requestedAttributes = File.GetAttributes(path) | FileAttributes.Hidden;
        File.SetAttributes(path, requestedAttributes);
        var effectiveAttributes = File.GetAttributes(path);

        await FileEncodingHelper.WriteWithEncodingAsync(
            path, "after", new UTF8Encoding(false), TestContext.Current.CancellationToken);

        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Be("after");
        File.GetAttributes(path).Should().Be(effectiveAttributes, "写回必须保留平台实际生效的文件属性");
        if (!OperatingSystem.IsWindows())
            return;

        // 创建时间只有 Windows 才有稳定语义：Unix 的 birth time 由文件系统决定且不可任意改写。
        File.GetCreationTimeUtc(path).Should().Be(creationTime);
        effectiveAttributes.Should().HaveFlag(FileAttributes.Hidden, "Windows 上 Hidden 位必须可设置");
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
