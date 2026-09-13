

using OneCode.Core.IO;
namespace OneCode.Tests;

/// <summary>
/// Containment contract for <see cref="PathBoundary.IsWithinDirectory(string, string)"/>:
/// the base directory itself is in-scope, descendants are in-scope,
/// parents and prefix-spoofed siblings are not.
/// </summary>
public sealed class PathBoundaryTests
{
    [Fact]
    public void IsWithinDirectory_BaseDirectoryItself_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pb_self_{Guid.NewGuid():N}");

        PathBoundary.IsWithinDirectory(dir, dir).Should().BeTrue();
    }

    [Fact]
    public void IsWithinDirectory_TrailingSeparatorOnBaseOrPath_StillReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pb_trail_{Guid.NewGuid():N}");
        var withSep = dir + Path.DirectorySeparatorChar;

        PathBoundary.IsWithinDirectory(withSep, dir).Should().BeTrue();
        PathBoundary.IsWithinDirectory(dir, withSep).Should().BeTrue();
    }

    [Fact]
    public void IsWithinDirectory_ChildPath_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pb_child_{Guid.NewGuid():N}");
        var child = Path.Combine(dir, "sub", "file.txt");

        PathBoundary.IsWithinDirectory(child, dir).Should().BeTrue();
    }

    [Fact]
    public void IsWithinDirectory_ParentPath_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sandbox", $"pb_parent_{Guid.NewGuid():N}");

        PathBoundary.IsWithinDirectory(Path.GetTempPath(), dir).Should().BeFalse();
    }

    [Fact]
    public void IsWithinDirectory_PrefixSpoofSibling_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "App");
        var spoof = Path.Combine(Path.GetTempPath(), "Application", "file.txt");

        PathBoundary.IsWithinDirectory(spoof, dir).Should().BeFalse();
    }

    /// <summary>
    /// Regression guard: the default comparison must follow the filesystem's case
    /// semantics. An unconditional <c>OrdinalIgnoreCase</c> made containment checks
    /// too permissive on case-sensitive filesystems, where <c>/work</c> and
    /// <c>/WORK</c> are distinct directories.
    /// </summary>
    [Fact]
    public void DefaultComparison_IsCaseInsensitiveOnlyOnWindows()
    {
        var expected = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        PathBoundary.DefaultComparison.Should().Be(expected);
    }

    [Fact]
    public void IsWithinDirectory_Default_UsesPlatformCaseSemantics()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "pb_case_root");
        var caseVariant = Path.Combine(Path.GetTempPath(), "PB_CASE_ROOT", "child");

        var result = PathBoundary.IsWithinDirectory(caseVariant, baseDir);

        if (OperatingSystem.IsWindows())
            result.Should().BeTrue();
        else
            result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinDirectory_ExplicitOrdinal_RejectsCaseVariant()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "pb_case_root");
        var caseVariant = Path.Combine(Path.GetTempPath(), "PB_CASE_ROOT", "child");

        PathBoundary.IsWithinDirectory(caseVariant, baseDir, StringComparison.Ordinal)
            .Should().BeFalse();
    }
}
