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

    [Theory]
    [InlineData("python --version 2>/dev/null || python3 --version 2>/dev/null")]
    [InlineData("dotnet build 2>&1")]
    [InlineData("ls -la 2>nul")]
    public void IsDestructive_NullAndStreamRedirection_ReturnsFalse(string command)
    {
        BashCommandClassifier.IsDestructive(command).Should().BeFalse();
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

    [Theory]
    [InlineData("cd /tmp", new[] { "/tmp" })]
    [InlineData("cd ..", new[] { ".." })]
    [InlineData("cd ../..", new[] { "../.." })]
    [InlineData("cd -L src", new[] { "src" })]
    [InlineData("pushd /var/log", new[] { "/var/log" })]
    // 无操作数（切到 $HOME）与 cd -（上一目录）无法静态解析 → "~" fail-closed 标记，
    // SafeResolve 展开主目录后因越界被拒
    [InlineData("cd", new[] { "~" })]
    [InlineData("cd -", new[] { "~" })]
    public void ExtractReferencedPaths_CdCommand_ExtractsTarget(string command, string[] expected)
    {
        BashCommandClassifier.ExtractReferencedPaths(command).Should().Equal(expected);
    }

    [Fact]
    public void ExtractReferencedPaths_CdWithVariableTarget_ReturnsOperandForFailClosedValidation()
    {
        BashCommandClassifier.ExtractReferencedPaths("cd $OUTSIDE").Should().Equal("$OUTSIDE");
    }

    [Theory]
    [InlineData("echo hi > out.txt", "out.txt")]
    [InlineData("cmd --flag >> logs/out.log", "logs/out.log")]
    [InlineData("build 1> build.log 2> err.log", "build.log")]
    [InlineData("cat src.cs > dst.cs", "dst.cs")]
    public void ExtractReferencedPaths_WriteRedirection_ExtractsFileTarget(string command, string expectedTarget)
    {
        BashCommandClassifier.ExtractReferencedPaths(command).Should().Contain(expectedTarget);
    }

    [Theory]
    [InlineData("python --version 2>/dev/null")]
    [InlineData("dotnet build 2>&1")]
    [InlineData("ls -la 2>nul")]
    [InlineData("echo ok >&2")]
    public void ExtractReferencedPaths_StreamDiscardTargets_AreNotExtracted(string command)
    {
        // 只允许真实文件目标进入校验：/dev/null、&1、&2、nul、fd 数字都不是越界信号
        var paths = BashCommandClassifier.ExtractReferencedPaths(command);
        paths.Should().NotContain("/dev/null").And.NotContain("nul")
             .And.NotContain("&1").And.NotContain("&2").And.NotContain("2");
    }
}
