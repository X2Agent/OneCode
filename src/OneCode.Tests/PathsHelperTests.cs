using OneCode.Infrastructure;

namespace OneCode.Tests;

public sealed class PathsHelperTests
{
    [Fact]
    public void SafeResolve_PathInsideWorkingDir_Succeeds()
    {
        var workDir = Path.GetTempPath();
        var result = PathsHelper.SafeResolve("subdir/file.txt", workDir);

        result.IsSuccess.Should().BeTrue();
        result.Value.StartsWith(workDir, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
    }

    [Fact]
    public void SafeResolve_PathOutsideWorkingDir_Fails()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "sandbox");
        var result = PathsHelper.SafeResolve("../../etc/passwd", workDir);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("../../etc/passwd", "错误文案必须回显被拒绝的路径");
        // 拒绝分支随平台而异：Windows 上解析结果落在 AppData 等受保护目录内
        // （"Access denied" / "protected system directory"），Linux 上 /etc 不在
        // 受保护列表内而走通用越界分支（"outside the working directory"）。
        // 两种均为正确拒绝，文案都必须显式说明原因（约定同 PathTraversalTests）。
        result.Error.Should().Match(error =>
                error.Contains("Access denied", StringComparison.Ordinal)
                || error.Contains("protected system directory", StringComparison.Ordinal)
                || error.Contains("outside the working directory", StringComparison.Ordinal),
            "拒绝原因必须显式可见");
    }

    [Fact]
    public void SafeResolve_AbsolutePathOutsideWorkingDir_Fails()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "sandbox");
        var outsidePath = Path.GetTempPath(); // parent of sandbox

        var result = PathsHelper.SafeResolve(outsidePath, workDir);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void SafeResolve_TildePathInsideHome_Succeeds()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = PathsHelper.SafeResolve("~/test.txt", home);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ExpandHome_Tilde_ExpandsToUserProfile()
    {
        var expanded = PathsHelper.ExpandHome("~/foo");
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "foo");

        expanded.Should().Be(expected);
    }

    // SafeResolve with additional directories (/add-dir)

    [Fact]
    public void SafeResolve_WithAdditionalDirs_PathInsideAdditionalDir_Succeeds()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"ph_add_{Guid.NewGuid():N}");
        var workDir = Path.Combine(sandbox, "project");
        var addDir = Path.Combine(sandbox, "external");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(addDir);
        try
        {
            var filePath = Path.Combine(addDir, "note.txt");
            File.WriteAllText(filePath, "x");

            var result = PathsHelper.SafeResolve(filePath, workDir, new[] { addDir });

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be(filePath);
        }
        finally { try { Directory.Delete(sandbox, recursive: true); } catch { } }
    }

    [Fact]
    public void SafeResolve_WithAdditionalDirs_PathOutsideAllRoots_Fails()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"ph_add_{Guid.NewGuid():N}");
        var workDir = Path.Combine(sandbox, "project");
        var addDir = Path.Combine(sandbox, "external");
        var outsideDir = Path.Combine(sandbox, "outside");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(addDir);
        Directory.CreateDirectory(outsideDir);
        try
        {
            var outsideFile = Path.Combine(outsideDir, "secret.txt");
            File.WriteAllText(outsideFile, "x");

            var result = PathsHelper.SafeResolve(outsideFile, workDir, new[] { addDir });

            result.IsSuccess.Should().BeFalse();
        }
        finally { try { Directory.Delete(sandbox, recursive: true); } catch { } }
    }

    [Fact]
    public void SafeResolve_WithNullAdditionalDirs_FallsBackToWorkingDirOnly()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"ph_null_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            // Path inside workDir — succeeds
            var inside = PathsHelper.SafeResolve("file.txt", workDir, additionalDirs: null);
            inside.IsSuccess.Should().BeTrue();

            // Path outside workDir — fails (null additionalDirs means no extra roots)
            var outside = PathsHelper.SafeResolve("../../etc/passwd", workDir, additionalDirs: null);
            outside.IsSuccess.Should().BeFalse();
        }
        finally { try { Directory.Delete(workDir, recursive: true); } catch { } }
    }

    [Fact]
    public void SafeResolve_TwoArgOverload_BehavesLikeNullAdditionalDirs()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"ph_twoarg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var outside = PathsHelper.SafeResolve("../../etc/passwd", workDir);
            var outsideExplicit = PathsHelper.SafeResolve("../../etc/passwd", workDir, additionalDirs: null);

            outside.IsSuccess.Should().BeFalse();
            outsideExplicit.IsSuccess.Should().BeFalse();
            outside.Error.Should().Be(outsideExplicit.Error);
        }
        finally { try { Directory.Delete(workDir, recursive: true); } catch { } }
    }
}
