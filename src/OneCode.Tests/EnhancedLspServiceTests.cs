using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Lsp;

namespace OneCode.Tests;

// EnhancedLspService — didChange 基线记录（P2：诊断新鲜度判据的数据源）
// 与目录删除的已打开文档清理（P4：前缀匹配的递归覆盖、兄弟目录边界、Windows 大小写）。

public sealed class EnhancedLspServiceTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "EnhancedLspTests_" + Guid.NewGuid().ToString("N")[..8]);

    public EnhancedLspServiceTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    private static EnhancedLspService CreateService() => new(
        new LspServerManager(
            NullLogger<LspServerManager>.Instance,
            new LspDiagnosticRegistry(),
            NullLoggerFactory.Instance),
        new LspDiagnosticRegistry(),
        NullLogger<EnhancedLspService>.Instance);

    private async Task<string> WriteAsync(string relativeName)
    {
        var path = Path.Combine(_tmpDir, relativeName);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, "class X { }");
        return path;
    }

    [Fact]
    public async Task NotifyFileUpdated_RecordsDidChangeBaseline()
    {
        await using var service = CreateService();
        var file = await WriteAsync("a.cs");

        service.GetLastDidChangeUtc(file).Should().BeNull("未经写通知路径的文件不应有基线");

        var before = DateTime.UtcNow;
        await service.NotifyFileUpdatedAsync(file);

        var baseline = service.GetLastDidChangeUtc(file);
        baseline.Should().NotBeNull("didChange 基线是诊断新鲜度判定的数据源");
        baseline!.Value.Should().BeOnOrAfter(before, "基线必须是本次通知的时间戳，不得早于通知发起时刻")
            .And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(5), "基线必须落在测试执行时间窗内");
    }

    [Fact]
    public async Task NotifyFileClosed_DropsDidChangeBaseline()
    {
        await using var service = CreateService();
        var file = await WriteAsync("b.cs");
        await service.NotifyFileUpdatedAsync(file);
        service.GetLastDidChangeUtc(file).Should().NotBeNull();

        await service.NotifyFileClosedAsync(file);

        service.GetLastDidChangeUtc(file).Should().BeNull("关闭文档后基线随之失效");
    }

    [Fact]
    public async Task NotifyDirectoryDeleted_ClosesDocumentsUnderDirectory_ButNotSiblingTrees()
    {
        await using var service = CreateService();
        var treeDir = Path.Combine(_tmpDir, "tree");
        var nestedDir = Path.Combine(treeDir, "sub");
        var siblingDir = Path.Combine(_tmpDir, "tree2");
        Directory.CreateDirectory(nestedDir);
        Directory.CreateDirectory(siblingDir);
        var inside = await WriteAsync(Path.Combine("tree", "in.cs"));
        var nested = await WriteAsync(Path.Combine("tree", "sub", "deep.cs"));
        var sibling = await WriteAsync(Path.Combine("tree2", "out.cs"));
        var outside = await WriteAsync("top.cs");
        foreach (var f in new[] { inside, nested, sibling, outside })
            await service.NotifyFileUpdatedAsync(f);

        await service.NotifyDirectoryDeletedAsync(treeDir);

        service.GetLastDidChangeUtc(inside).Should().BeNull("目录删除应关闭目录下已打开文档");
        service.GetLastDidChangeUtc(nested).Should().BeNull("前缀匹配必须递归覆盖子目录");
        service.GetLastDidChangeUtc(sibling).Should().NotBeNull("tree2 不得被 /tree 前缀误匹配（分隔符边界）");
        service.GetLastDidChangeUtc(outside).Should().NotBeNull();
    }

    [Fact]
    public async Task NotifyDirectoryDeleted_MatchesPathCaseInsensitivelyOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return; // 忽略大小写仅是 Windows 路径语义

        await using var service = CreateService();
        var dir = Path.Combine(_tmpDir, "casedir");
        Directory.CreateDirectory(dir);
        var file = await WriteAsync(Path.Combine("casedir", "f.cs"));
        await service.NotifyFileUpdatedAsync(file);

        await service.NotifyDirectoryDeletedAsync(dir.ToLowerInvariant());

        service.GetLastDidChangeUtc(file).Should().BeNull("Windows 路径前缀匹配必须忽略大小写");
    }

    [Fact]
    public void IsUnderDirectory_UnixComparison_IsCaseSensitive()
    {
        // 平台语义锚定：Unix 用 Ordinal——大小写敏感，且分隔符边界防 /a/bc 误匹配。
        const StringComparison unix = StringComparison.Ordinal;

        EnhancedLspService.IsUnderDirectory("/a/b/file.cs", "/a/b", unix).Should().BeTrue();
        EnhancedLspService.IsUnderDirectory("/a/B/file.cs", "/a/b", unix)
            .Should().BeFalse("Unix 路径大小写敏感，/a/B 不得被 /a/b 前缀误匹配");
        EnhancedLspService.IsUnderDirectory("/a/bc/file.cs", "/a/b", unix)
            .Should().BeFalse("分隔符边界：/a/bc 不得被 /a/b 误匹配");
    }

    [Fact]
    public void IsUnderDirectory_WindowsComparison_IgnoresCase()
    {
        // 平台语义锚定：Windows 用 OrdinalIgnoreCase，分隔符边界同样生效。
        const StringComparison windows = StringComparison.OrdinalIgnoreCase;

        EnhancedLspService.IsUnderDirectory(@"C:\p\B\f.cs", @"c:\p\b", windows).Should().BeTrue();
        EnhancedLspService.IsUnderDirectory(@"C:\p\bc\f.cs", @"c:\p\b", windows)
            .Should().BeFalse("分隔符边界对不区分大小写的比较同样生效");
    }
}
