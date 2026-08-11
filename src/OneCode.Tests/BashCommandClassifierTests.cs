using OneCode.Core.Permissions;

namespace OneCode.Tests;

public sealed class BashCommandClassifierTests
{
    [Theory]
    [InlineData("ls -la", true)]
    [InlineData("cat file.txt", true)]
    [InlineData("grep pattern file", true)]
    [InlineData("git status", true)]
    [InlineData("git diff HEAD", true)]
    [InlineData("git log --oneline", true)]
    [InlineData("echo hello", true)]
    public void IsReadOnly_ReadOnlyCommands_ReturnsTrue(string command, bool expected)
    {
        BashCommandClassifier.IsReadOnly(command).Should().Be(expected);
    }

    [Theory]
    [InlineData("rm -rf /", false)]
    [InlineData("git push origin main", false)]
    [InlineData("git commit -m msg", false)]
    [InlineData("cat file.txt > output.txt", false)]
    [InlineData("sed -i 's/a/b/' file.txt", false)]
    public void IsReadOnly_WritingCommands_ReturnsFalse(string command, bool expected)
    {
        BashCommandClassifier.IsReadOnly(command).Should().Be(expected);
    }

    [Theory]
    [InlineData("git fetch", false)]
    public void IsReadOnly_GitFetch_IsNotReadOnly(string command, bool expected)
    {
        // git fetch modifies local remote-tracking refs — not read-only
        BashCommandClassifier.IsReadOnly(command).Should().Be(expected);
    }

    [Theory]
    [InlineData("rm -rf folder", true)]
    [InlineData("git push --force", true)]
    [InlineData("git reset --hard HEAD~1", true)]
    [InlineData("git clean -fd", true)]
    [InlineData("DROP TABLE users", true)]
    public void IsDestructive_DangerousCommands_ReturnsTrue(string command, bool expected)
    {
        BashCommandClassifier.IsDestructive(command).Should().Be(expected);
    }

    [Fact]
    public void IsDestructive_SafeCommand_ReturnsFalse()
    {
        BashCommandClassifier.IsDestructive("ls -la").Should().BeFalse();
    }

    [Fact]
    public void ExtractReferencedPaths_RmCommand_ExtractsPaths()
    {
        var paths = BashCommandClassifier.ExtractReferencedPaths("rm -rf /tmp/dir");
        paths.Should().Contain("/tmp/dir");
    }

    [Fact]
    public void ExtractReferencedPaths_EmptyCommand_ReturnsEmpty()
    {
        var paths = BashCommandClassifier.ExtractReferencedPaths("");
        paths.Should().BeEmpty();
    }
}
