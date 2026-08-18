using OneCode.Core.IO;

namespace OneCode.Tests;

/// <summary>
/// Containment contract for <see cref="PathBoundary.IsWithinDirectory"/>:
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
}
