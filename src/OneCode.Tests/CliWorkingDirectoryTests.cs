using OneCode.Cli;

namespace OneCode.Tests;

/// <remarks>
/// TryApply 用例依赖 Directory.SetCurrentDirectory，按项目约定加入串行集合，
/// 避免与其他并行集合的工作目录竞争。
/// </remarks>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class CliWorkingDirectoryTests
{
    [Fact]
    public void Parse_NoOption_ReturnsNullPathAndOriginalArgs()
    {
        var result = CliWorkingDirectory.Parse(["--version"]);

        result.Path.Should().BeNull();
        result.Error.Should().BeNull();
        result.Remaining.Should().Equal("--version");
    }

    [Theory]
    [InlineData(new[] { "--cwd", "E:\\ws" }, "E:\\ws")]
    [InlineData(new[] { "--cwd=E:\\ws" }, "E:\\ws")]
    [InlineData(new[] { "-C", "E:\\ws" }, "E:\\ws")]
    public void Parse_SupportedForms_ExtractsPath(string[] args, string expected)
    {
        var result = CliWorkingDirectory.Parse(args);

        result.Error.Should().BeNull();
        result.Path.Should().Be(expected);
        result.Remaining.Should().BeEmpty();
    }

    [Fact]
    public void Parse_OptionAmongOthers_StripsOnlyOptionPair()
    {
        var result = CliWorkingDirectory.Parse(["--foo", "--cwd", "E:\\ws", "--version"]);

        result.Path.Should().Be("E:\\ws");
        result.Remaining.Should().Equal("--foo", "--version");
    }

    [Fact]
    public void Parse_MultipleOccurrences_LastOneWins()
    {
        var result = CliWorkingDirectory.Parse(["--cwd", "E:\\first", "-C", "E:\\second"]);

        result.Path.Should().Be("E:\\second");
        result.Remaining.Should().BeEmpty();
    }

    // 单实参 string[] 传给 params object[] 需显式 object[] 包裹，避免 CS0182（隐式类型数组的 normal-form 特性绑定）
    [Theory]
    [InlineData(new object[] { new[] { "--cwd" } })]
    [InlineData(new object[] { new[] { "a", "-C" } })]
    [InlineData(new object[] { new[] { "--cwd=" } })]
    public void Parse_MissingValue_ReturnsUsageError(string[] args)
    {
        var result = CliWorkingDirectory.Parse(args);

        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryApply_NonexistentDirectory_FailsWithFullPathInError()
    {
        var missing = Path.Combine(Path.GetTempPath(), "onecode-missing-" + Guid.NewGuid().ToString("N"));

        var ok = CliWorkingDirectory.TryApply(missing, out var error);

        ok.Should().BeFalse();
        error.Should().Contain(missing);
    }

    [Fact]
    public void TryApply_ExistingDirectory_SwitchesCurrentDirectory()
    {
        var original = Environment.CurrentDirectory;
        var target = Directory.CreateTempSubdirectory("onecode-cwd-");
        try
        {
            var ok = CliWorkingDirectory.TryApply(target.FullName, out var error);

            ok.Should().BeTrue();
            error.Should().BeNull();
            Environment.CurrentDirectory.Should().Be(target.FullName);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            target.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryApply_RelativePath_ResolvesAgainstCurrentDirectory()
    {
        var original = Environment.CurrentDirectory;
        var parent = Directory.CreateTempSubdirectory("onecode-cwd-");
        var child = Directory.CreateDirectory(Path.Combine(parent.FullName, "child"));
        try
        {
            Environment.CurrentDirectory = parent.FullName;

            var ok = CliWorkingDirectory.TryApply("child", out var error);

            ok.Should().BeTrue();
            error.Should().BeNull();
            Environment.CurrentDirectory.Should().Be(child.FullName);
        }
        finally
        {
            Environment.CurrentDirectory = original;
            parent.Delete(recursive: true);
        }
    }
}
