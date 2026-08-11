using NSubstitute;
using OneCode.App.Commands;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;

namespace OneCode.Tests;

public sealed class GitHelperTests
{
    private readonly IProcessRunner _runner = Substitute.For<IProcessRunner>();
    private readonly GitHelper _sut;

    public GitHelperTests()
    {
        _sut = new GitHelper(_runner);
        // Default: rev-parse succeeds so ResolveGitRootAsync uses a stable root.
        _runner.ExecuteWithArgumentListAsync(
                "git",
                Arg.Is<IEnumerable<string>>(a => ArgsContain(a, "rev-parse")),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "C:/repo\n", ""));
    }

    [Theory]
    [InlineData(
        "src/{OneCode.Infrastructure => OneCode.Automation}/Cron/File.cs",
        "src/OneCode.Automation/Cron/File.cs")]
    [InlineData("{old_name => new_name}.cs", "new_name.cs")]
    [InlineData("src/file.{old => new}", "src/file.new")]
    [InlineData("src/OneCode.App/Commands/GitHelper.cs", "src/OneCode.App/Commands/GitHelper.cs")]
    [InlineData("\"src/{a => b}/x.cs\"", "src/b/x.cs")]
    public void NormalizeDiffPath_ResolvesRenameSyntax(string input, string expected)
    {
        GitHelper.NormalizeDiffPath(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("src/foo.cs", ":(top)src/foo.cs")]
    [InlineData(":(top)src/foo.cs", ":(top)src/foo.cs")]
    [InlineData(@"src\foo.cs", ":(top)src/foo.cs")]
    public void ToTopLevelPathspec_AnchorsRepoRelativePaths(string input, string expected)
    {
        GitHelper.ToTopLevelPathspec(input).Should().Be(expected);
    }

    [Fact]
    public async Task GetPendingDiffStatAsync_ResolvesRenamePath_SoFileDiffLookupWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        const string renameNumstat =
            "3\t1\tsrc/{OneCode.Infrastructure => OneCode.Automation}/Cron/CronosCronParser.cs\n";
        const string normalNumstat = "2\t0\tsrc/OneCode.App/Tui/TuiTheme.cs\n";

        _runner.ExecuteWithArgumentListAsync(
                "git",
                Arg.Is<IEnumerable<string>>(a => ArgsContain(a, "--numstat")),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, renameNumstat + normalNumstat, ""));

        var entries = await _sut.GetPendingDiffStatAsync(ct);

        entries.Should().HaveCount(2);
        entries[0].Path.Should().Be("src/OneCode.Automation/Cron/CronosCronParser.cs");
        entries[0].Added.Should().Be(3);
        entries[0].Removed.Should().Be(1);
        entries[1].Path.Should().Be("src/OneCode.App/Tui/TuiTheme.cs");
    }

    [Fact]
    public async Task GetFileDiffAgainstHeadAsync_RawRenamePath_NormalizesAndUsesTopPathspec()
    {
        var ct = TestContext.Current.CancellationToken;
        const string rawRename = "src/{OneCode.Infrastructure => OneCode.Automation}/Cron/File.cs";
        const string expectedTop = ":(top)src/OneCode.Automation/Cron/File.cs";
        const string diffBody = "diff --git a/src/OneCode.Automation/Cron/File.cs b/src/OneCode.Automation/Cron/File.cs\n+hello\n";

        _runner.ExecuteWithArgumentListAsync(
                "git",
                Arg.Is<IEnumerable<string>>(a =>
                    ArgsContain(a, "diff")
                    && ArgsContain(a, "HEAD")
                    && ArgsContain(a, expectedTop)
                    && !ArgsContain(a, rawRename)),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, diffBody, ""));

        var diff = await _sut.GetFileDiffAgainstHeadAsync(rawRename, ct);

        diff.Should().Be(diffBody);
    }

    [Fact]
    public async Task GetFileDiffAgainstHeadAsync_EmptyHeadDiff_FallsBackToCached()
    {
        var ct = TestContext.Current.CancellationToken;
        const string path = "src/foo.cs";
        const string topPath = ":(top)src/foo.cs";
        const string cachedDiff = "diff --git a/src/foo.cs b/src/foo.cs\n+staged\n";

        _runner.ExecuteWithArgumentListAsync(
                "git",
                Arg.Is<IEnumerable<string>>(a => ArgsContain(a, "HEAD") && ArgsContain(a, topPath)),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "", ""));

        _runner.ExecuteWithArgumentListAsync(
                "git",
                Arg.Is<IEnumerable<string>>(a => ArgsContain(a, "--cached") && ArgsContain(a, topPath)),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, cachedDiff, ""));

        var diff = await _sut.GetFileDiffAgainstHeadAsync(path, ct);

        diff.Should().Be(cachedDiff);
    }

    private static bool ArgsContain(IEnumerable<string>? args, string value)
        => args is not null && args.Contains(value);
}
