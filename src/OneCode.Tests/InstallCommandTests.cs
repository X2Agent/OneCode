using OneCode.App.Commands;
using OneCode.Core.Commands;
using NSubstitute;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for InstallCommand covering: local directory install, -g/--global
/// scope flag, git URL detection, repo name extraction, and error paths.
/// Git-clone behavior is tested via a mocked IGitHelper (no network access).
/// </summary>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class InstallCommandTests : IDisposable
{
    private readonly string _tmpRoot = Path.Combine(
        Path.GetTempPath(), "InstallCmdTests_" + Guid.NewGuid().ToString("N")[..8]);

    // Simulate the "user home" for global installs and the "project dir" for local installs.
    private readonly string _fakeHome;
    private readonly string _fakeProject;
    private readonly string _originalCwd;

    public InstallCommandTests()
    {
        _fakeHome = Path.Combine(_tmpRoot, "home");
        _fakeProject = Path.Combine(_tmpRoot, "project");
        Directory.CreateDirectory(_fakeHome);
        Directory.CreateDirectory(_fakeProject);
        _originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _fakeProject;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _originalCwd;
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    // No-args / list

    [Fact]
    public async Task NoArgs_ReturnsListHint()
    {
        var sut = CreateSut();
        var result = await sut.ExecuteAsync([]);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("/skills list");
    }

    [Fact]
    public async Task ListArg_ReturnsListHint()
    {
        var sut = CreateSut();
        var result = await sut.ExecuteAsync(["list"]);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("/skills list");
    }

    // Local directory install

    [Fact]
    public async Task LocalDir_ProjectScope_InstallsToProjectOneCodeDir()
    {
        var skillSrc = CreateTempSkill("my-skill", "# my-skill\nA test skill.");
        var sut = CreateSut();

        var result = await sut.ExecuteAsync([skillSrc]);

        result.Should().BeOfType<CommandResult.TextResult>();
        var expectedDest = Path.Combine(_fakeProject, ".onecode", "skills", "my-skill", "SKILL.md");
        File.Exists(expectedDest).Should().BeTrue();
    }

    [Fact]
    public async Task LocalDir_GlobalFlag_InstallsToUserHomeSkillsDir()
    {
        // 用 GUID 保证 skill 名唯一，避免覆盖用户真实 skill
        var uniqueName = $"test-skill-{Guid.NewGuid():N}"[..16];
        var skillSrc = CreateTempSkill(uniqueName, $"# {uniqueName}\nGlobal install test.");
        var sut = CreateSut();
        var globalRoot = InstallCommand.ResolveSkillsRoot(global: true);
        var destFile = Path.Combine(globalRoot, uniqueName, "SKILL.md");

        try
        {
            var result = await sut.ExecuteAsync([skillSrc, "-g"]);

            result.Should().BeOfType<CommandResult.TextResult>();
            File.Exists(destFile).Should().BeTrue($"-g 标志应将 skill 安装到用户目录 {globalRoot}");
            (await File.ReadAllTextAsync(destFile)).Should().Contain(uniqueName);
        }
        finally
        {
            // 清理用户目录下的测试 skill
            var destDir = Path.Combine(globalRoot, uniqueName);
            if (Directory.Exists(destDir))
            {
                try { Directory.Delete(destDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public async Task LocalDir_GlobalLongFlag_InstallsToUserHomeSkillsDir()
    {
        // 验证 --global 长标志与 -g 短标志行为一致
        var uniqueName = $"test-skill-{Guid.NewGuid():N}"[..16];
        var skillSrc = CreateTempSkill(uniqueName, $"# {uniqueName}\nGlobal long-flag test.");
        var sut = CreateSut();
        var globalRoot = InstallCommand.ResolveSkillsRoot(global: true);
        var destFile = Path.Combine(globalRoot, uniqueName, "SKILL.md");

        try
        {
            var result = await sut.ExecuteAsync([skillSrc, "--global"]);

            result.Should().BeOfType<CommandResult.TextResult>();
            File.Exists(destFile).Should().BeTrue($"--global 标志应将 skill 安装到用户目录 {globalRoot}");
        }
        finally
        {
            var destDir = Path.Combine(globalRoot, uniqueName);
            if (Directory.Exists(destDir))
            {
                try { Directory.Delete(destDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public async Task LocalDir_Overwrites_ExistingSkill()
    {
        var skillSrc = CreateTempSkill("overwrite", "# v1\nOriginal content.");
        var sut = CreateSut();

        // First install
        await sut.ExecuteAsync([skillSrc]);
        var destFile = Path.Combine(_fakeProject, ".onecode", "skills", "overwrite", "SKILL.md");
        (await File.ReadAllTextAsync(destFile)).Should().Contain("Original content");

        // Modify source and re-install
        await File.WriteAllTextAsync(Path.Combine(skillSrc, "SKILL.md"), "# v2\nUpdated content.");
        await sut.ExecuteAsync([skillSrc]);

        (await File.ReadAllTextAsync(destFile)).Should().Contain("Updated content");
    }

    [Fact]
    public async Task LocalDir_NotExist_ReturnsError()
    {
        var sut = CreateSut();
        var result = await sut.ExecuteAsync(["/nonexistent/path/xyz"]);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("Source path not found");
    }

    // Unknown options

    [Fact]
    public async Task UnknownOption_ReturnsError()
    {
        var sut = CreateSut();
        var result = await sut.ExecuteAsync(["somepath", "--bogus"]);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("--bogus");
    }

    // Git clone via IGitHelper

    [Fact]
    public async Task GitUrl_ClonesAndCopiesSkillContent()
    {
        // Arrange: IGitHelper.RunAsync simulates a successful clone by creating the
        // skill content in the temp dir that the real git would have populated.
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();

        gitHelper.RunAsync(
            Arg.Is<string[]>(a => a.Length > 0 && a[0] == "clone"),
            ct)
            .Returns(callInfo =>
            {
                // callInfo[1] is the CancellationToken; the temp dir is the last arg.
                var args = callInfo.Arg<string[]>();
                var tempDir = args[^1];
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "SKILL.md"),
                    "# cloned-skill\nFrom git repo.");
                return new GitCommandResult(true, "", "");
            });

        var sut = new InstallCommand(gitHelper);

        // Act
        var result = await sut.ExecuteAsync(["https://github.com/user/cloned-skill.git"], ct);

        // Assert
        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("cloned-skill");
        var destFile = Path.Combine(_fakeProject, ".onecode", "skills", "cloned-skill", "SKILL.md");
        File.Exists(destFile).Should().BeTrue();
        (await File.ReadAllTextAsync(destFile)).Should().Contain("From git repo.");
    }

    [Fact]
    public async Task GitUrl_CloneFails_ReturnsErrorWithStderr()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(false, "", "fatal: repository not found"));

        var sut = new InstallCommand(gitHelper);
        var result = await sut.ExecuteAsync(["https://github.com/user/nonexistent.git"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("repository not found");
    }

    [Fact]
    public async Task GitUrl_CloneReturnsNull_ReturnsGitNotAvailableError()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns((GitCommandResult?)null);

        var sut = new InstallCommand(gitHelper);
        var result = await sut.ExecuteAsync(["https://github.com/user/repo.git"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("git is not available");
    }

    [Fact]
    public async Task GitUrl_NoSkillContent_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();

        gitHelper.RunAsync(
            Arg.Is<string[]>(a => a.Length > 0 && a[0] == "clone"),
            ct)
            .Returns(callInfo =>
            {
                var args = callInfo.Arg<string[]>();
                var tempDir = args[^1];
                Directory.CreateDirectory(tempDir);
                // No .md files — not a valid skill repo.
                File.WriteAllText(Path.Combine(tempDir, "README.txt"), "no skill here");
                return new GitCommandResult(true, "", "");
            });

        var sut = new InstallCommand(gitHelper);
        var result = await sut.ExecuteAsync(["https://github.com/user/empty-repo.git"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("no skill content found");
    }

    [Fact]
    public async Task GitUrl_FindsSkillInSkillsSubdirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();

        gitHelper.RunAsync(
            Arg.Is<string[]>(a => a.Length > 0 && a[0] == "clone"),
            ct)
            .Returns(callInfo =>
            {
                var args = callInfo.Arg<string[]>();
                var tempDir = args[^1];
                var skillsSub = Path.Combine(tempDir, "skills");
                Directory.CreateDirectory(skillsSub);
                File.WriteAllText(Path.Combine(skillsSub, "SKILL.md"), "# nested\nNested skill.");
                return new GitCommandResult(true, "", "");
            });

        var sut = new InstallCommand(gitHelper);
        var result = await sut.ExecuteAsync(["https://github.com/user/nested.git"], ct);

        result.Should().BeOfType<CommandResult.TextResult>();
        var destFile = Path.Combine(_fakeProject, ".onecode", "skills", "nested", "SKILL.md");
        File.Exists(destFile).Should().BeTrue();
        (await File.ReadAllTextAsync(destFile)).Should().Contain("Nested skill.");
    }

    // IsGitUrl detection

    [Theory]
    [InlineData("git://github.com/user/repo.git")]
    [InlineData("https://github.com/user/repo.git")]
    [InlineData("https://github.com/user/repo")]
    [InlineData("http://gitlab.com/user/repo")]
    [InlineData("git@github.com:user/repo.git")]
    [InlineData("https://example.com/skills/my-skill")]
    public void IsGitUrl_RecognizesGitUrls(string url)
    {
        InstallCommand.IsGitUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("./local/path")]
    [InlineData("/absolute/local/path")]
    [InlineData("relative/path/to/skill")]
    [InlineData("C:\\Users\\someone\\skill")]
    [InlineData("my-skill")]
    public void IsGitUrl_RejectsLocalPaths(string path)
    {
        InstallCommand.IsGitUrl(path).Should().BeFalse();
    }

    // ExtractRepoName

    [Theory]
    [InlineData("https://github.com/user/my-skill.git", "my-skill")]
    [InlineData("https://github.com/user/my-skill", "my-skill")]
    [InlineData("git://github.com/user/my-skill.git", "my-skill")]
    [InlineData("git@github.com:user/my-skill.git", "my-skill")]
    [InlineData("https://example.com/repo/awesome-skill", "awesome-skill")]
    [InlineData("https://github.com/user/repo.git?branch=main", "repo")]
    [InlineData("https://github.com/user/repo#develop", "repo")]
    public void ExtractRepoName_ParsesCorrectly(string url, string expected)
    {
        InstallCommand.ExtractRepoName(url).Should().Be(expected);
    }

    // ResolveSkillsRoot

    [Fact]
    public void ResolveSkillsRoot_Project_ReturnsCwdBasedPath()
    {
        var root = InstallCommand.ResolveSkillsRoot(global: false);
        root.Should().Be(Path.Combine(_fakeProject, ".onecode", "skills"));
    }

    [Fact]
    public void ResolveSkillsRoot_Global_ReturnsHomeBasedPath()
    {
        var root = InstallCommand.ResolveSkillsRoot(global: true);
        var expectedParent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        root.Should().Be(Path.Combine(expectedParent, ".onecode", "skills"));
    }

    // Helpers

    /// <summary>
    /// Creates an InstallCommand with a no-op IGitHelper. Suitable for local-directory
    /// tests that never trigger a git clone.
    /// </summary>
    private static InstallCommand CreateSut()
    {
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns((GitCommandResult?)null);
        return new InstallCommand(gitHelper);
    }

    private string CreateTempSkill(string name, string content)
    {
        var dir = Path.Combine(_tmpRoot, "sources", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
        return dir;
    }
}
