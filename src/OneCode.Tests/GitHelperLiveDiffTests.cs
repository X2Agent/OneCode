using OneCode.App.Commands;
using OneCode.Core.Commands;
using OneCode.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace OneCode.Tests;

/// <summary>
/// Optional live-repository smoke tests. Deterministic pathspec behavior is covered by
/// <see cref="GitHelperTests"/>; these tests skip explicitly (Assert.Skip) when the
/// repository has no suitable pending change.
/// </summary>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class GitHelperLiveDiffTests
{
    [Fact]
    public async Task GetFileDiff_FromSubdirectoryCwd_StillReturnsContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = FindRepoRoot();
        var subdir = Path.Combine(repo, "src", "OneCode.Cli");
        Directory.Exists(subdir).Should().BeTrue();

        // Reproduce the TUI failure mode: process cwd is a repo subdirectory,
        // while numstat paths are repo-root relative.
        Directory.SetCurrentDirectory(subdir);

        var git = new GitHelper(new ProcessRunner(NullLogger<ProcessRunner>.Instance));
        var entries = await git.GetPendingDiffStatAsync(ct);
        var diff = await FindFirstTextDiffAsync(git, entries, ct);
        if (diff is null)
            Assert.Skip("工作区没有待处理的文本变更，无法执行本冒烟测试");

        diff.Should().Contain("diff --git",
            "file detail must work when cwd is src/OneCode.Cli (uses repo root + :(top) pathspec)");
    }

    [Fact]
    public async Task GetFileDiff_FromRepoRoot_ReturnsContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = FindRepoRoot();
        Directory.SetCurrentDirectory(repo);
        var git = new GitHelper(new ProcessRunner(NullLogger<ProcessRunner>.Instance));
        var entries = await git.GetPendingDiffStatAsync(ct);
        var diff = await FindFirstTextDiffAsync(git, entries, ct);
        if (diff is null)
            Assert.Skip("工作区没有待处理的文本变更，无法执行本冒烟测试");

        diff.Should().Contain("diff --git");
    }

    private static async Task<string?> FindFirstTextDiffAsync(
        GitHelper git,
        IEnumerable<ReviewFileEntry> entries,
        CancellationToken ct)
    {
        foreach (var entry in entries.Where(item => item.Added > 0 || item.Removed > 0))
        {
            var diff = await git.GetFileDiffAgainstHeadAsync(entry.Path, ct);
            if (!string.IsNullOrWhiteSpace(diff))
                return diff;
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            if (File.Exists(Path.Combine(dir.FullName, "src", "OneCode.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return @"C:\Users\mayue\Desktop\ClaudeCode";
    }
}
