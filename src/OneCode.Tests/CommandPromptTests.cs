using OneCode.App.Commands;
using OneCode.App.Services;
using OneCode.App.Services.Lsp;
using OneCode.Core.Commands;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace OneCode.Tests;

/// <summary>
/// Tests for file-based prompt loading in CommitCommand, ReviewCommand
/// and InitCommand. Each test registers a PromptManager with
/// an in-memory template to verify template loading and variable rendering.
/// </summary>
/// <remarks>
/// 标记为 Collection 以串行化执行：InitCommand 测试依赖 Directory.SetCurrentDirectory
/// 和真实文件系统，并行执行会导致工作目录竞争。
/// </remarks>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class CommandPromptTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public CommandPromptTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cmd-prompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalCwd = Environment.CurrentDirectory;
        Directory.SetCurrentDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_originalCwd); } catch { }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static IConfigManager CreateConfigManager(AppSettings settings)
    {
        var cm = Substitute.For<IConfigManager>();
        cm.Current.Returns(ConfigSnapshot.FromEffective(settings));
        return cm;
    }

    // CommitCommand

    [Fact]
    public async Task CommitCommand_RendersPromptWithGitContext()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = CreateGitHelperReturning("status output", "diff output", "main", "abc123 commit");

        var sut = new CommitCommand(gitHelper, CreatePromptManager("system/commit",
            "Commit\nStatus: {{gitStatus}}\nDiff: {{gitDiff}}"));
        var result = await sut.ExecuteAsync([], ct);

        var prompt = result.Should().BeOfType<CommandResult.PromptResult>().Subject;
        prompt.Content.Should().Contain("status output");
        prompt.Content.Should().Contain("diff output");
        prompt.AllowedTools.Should().Contain("Bash(git commit:*)");
    }

    [Fact]
    public async Task CommitCommand_PromptNotFound_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = CreateGitHelperReturning("s", "d", "b", "l");

        var sut = new CommitCommand(gitHelper, new PromptManager());
        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("system/commit");
    }

    [Fact]
    public async Task CommitCommand_CustomTemplate_LoadsFromPromptManager()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = CreateGitHelperReturning("s", "d", "b", "l");

        var pm = CreatePromptManager("system/commit",
            "COMMIT PROMPT\nBranch: {{gitBranch}}");

        var sut = new CommitCommand(gitHelper, pm);
        var result = await sut.ExecuteAsync([], ct);

        var prompt = result.As<CommandResult.PromptResult>();
        prompt.Content.Should().Contain("COMMIT PROMPT");
        prompt.Content.Should().Contain("Branch: b");
    }

    // InitCommand

    [Fact]
    public async Task InitCommand_NoApiKey_WritesStaticTemplate()
    {
        var ct = TestContext.Current.CancellationToken;
        var fs = new TestFileSystem();
        var settings = new AppSettings();
        // 不走 prompt 路径，空 PromptManager 即可
        var sut = new InitCommand(fs, CreateConfigManager(settings), new PromptManager());

        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("static template, no API key");
        var md = File.ReadAllText(Path.Combine(_tempDir, "AGENTS.md"));
        md.Should().Contain("# AGENTS.md");
        md.Should().Contain("## Project Overview");
    }

    [Fact]
    public async Task InitCommand_NoLlmFlag_FallsBackEvenWithApiKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var fs = new TestFileSystem();
        var settings = new AppSettings { ApiKey = "sk-test" };
        var sut = new InitCommand(fs, CreateConfigManager(settings), new PromptManager());

        var result = await sut.ExecuteAsync(["--no-llm"], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("--no-llm");
    }

    [Fact]
    public async Task InitCommand_WithApiKey_ReturnsPromptResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var fs = new TestFileSystem();
        var settings = new AppSettings { ApiKey = "sk-test" };
        var pm = CreatePromptManager("system/init",
            "## Task: Initialize AGENTS.md\nType: {{projectType}}");

        var sut = new InitCommand(fs, CreateConfigManager(settings), pm);
        var result = await sut.ExecuteAsync([], ct);

        var prompt = result.Should().BeOfType<CommandResult.PromptResult>().Subject;
        prompt.Content.Should().Contain("## Task: Initialize AGENTS.md");
        prompt.AllowedTools.Should().Contain("Write(AGENTS.md)");
    }

    [Fact]
    public async Task InitCommand_AlreadyExistsWithoutForce_ReturnsHint()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "AGENTS.md"), "# existing", ct);
        var fs = new TestFileSystem();
        var settings = new AppSettings();
        var sut = new InitCommand(fs, CreateConfigManager(settings), new PromptManager());

        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("already exists");
    }

    [Fact]
    public async Task InitCommand_ForceFlag_OverwritesExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        var existing = Path.Combine(_tempDir, "AGENTS.md");
        await File.WriteAllTextAsync(existing, "# old content", ct);
        var fs = new TestFileSystem();
        var settings = new AppSettings();
        var sut = new InitCommand(fs, CreateConfigManager(settings), new PromptManager());

        var result = await sut.ExecuteAsync(["--force"], ct);

        result.Should().BeOfType<CommandResult.TextResult>();
        var md = File.ReadAllText(existing);
        md.Should().Contain("# AGENTS.md");
        md.Should().NotContain("# old content");
    }

    [Fact]
    public async Task InitCommand_WithCsproj_PromptContainsDotnetCommands()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "TestProj.csproj"), "<Project></Project>", ct);
        var fs = new TestFileSystem();
        var settings = new AppSettings { ApiKey = "sk-test" };
        var pm = CreatePromptManager("system/init",
            "Type: {{projectType}}\nBuild: {{buildCommand}}");

        var sut = new InitCommand(fs, CreateConfigManager(settings), pm);
        var result = await sut.ExecuteAsync([], ct);

        var prompt = result.As<CommandResult.PromptResult>();
        prompt.Content.Should().Contain("dotnet");
        prompt.Content.Should().Contain("dotnet build");
    }

    [Fact]
    public async Task InitCommand_WithReadme_PromptContainsReadmeContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "README.md"), "# My Project\nA test project.", ct);
        var fs = new TestFileSystem();
        var settings = new AppSettings { ApiKey = "sk-test" };
        var pm = CreatePromptManager("system/init",
            "README: {{readmeContent}}");

        var sut = new InitCommand(fs, CreateConfigManager(settings), pm);
        var result = await sut.ExecuteAsync([], ct);

        var prompt = result.As<CommandResult.PromptResult>();
        prompt.Content.Should().Contain("My Project");
        prompt.Content.Should().Contain("test project");
    }

    [Fact]
    public async Task InitCommand_PromptNotFound_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var fs = new TestFileSystem();
        var settings = new AppSettings { ApiKey = "sk-test" };
        // 空 PromptManager，没有注册模板
        var sut = new InitCommand(fs, CreateConfigManager(settings), new PromptManager());

        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("system/init");
    }

    [Fact]
    public async Task InitCommand_ApiKeyFromEffectiveSnapshot_TriggersLlmPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var fs = new TestFileSystem();
        var settings = new AppSettings { ApiKey = "resolved-environment-key" };
        var pm = CreatePromptManager("system/init", "Type: {{projectType}}");

        var sut = new InitCommand(fs, CreateConfigManager(settings), pm);
        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.PromptResult>();
    }

    // ReviewCommand

    [Fact]
    public async Task ReviewCommand_RendersPromptWithDiff()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "+ Added line", ""));
        gitHelper.ReadAsync(Arg.Any<string[]>(), ct)
            .Returns("main", "abc123 commit", "hash1\nhash2");

        var pm = CreatePromptManager("system/review",
            "Review\nScope: {{scopeDescription}}\nDiff: {{diff}}");
        var sut = new ReviewCommand(gitHelper, pm, new LspDiagnosticRegistry(), new ReviewCacheService(NullLogger<ReviewCacheService>.Instance));
        var result = await sut.ExecuteAsync([], ct);

        var prompt = result.Should().BeOfType<CommandResult.PromptResult>().Subject;
        prompt.Content.Should().Contain("+ Added line");
        prompt.Content.Should().Contain("unstaged changes");
    }

    [Fact]
    public async Task ReviewCommand_EmptyDiff_ReturnsTextResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "", ""));

        // 不走 prompt 路径，空 PromptManager 即可
        var sut = new ReviewCommand(gitHelper, new PromptManager(), new LspDiagnosticRegistry(), new ReviewCacheService(NullLogger<ReviewCacheService>.Instance));
        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("No changes to review");
    }

    [Fact]
    public async Task ReviewCommand_NoEditFlag_RestrictsAllowedTools()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "diff content", ""));
        gitHelper.ReadAsync(Arg.Any<string[]>(), ct)
            .Returns("main", "log", "hash1");

        var pm = CreatePromptManager("system/review",
            "Review\nScope: {{scopeDescription}}\nDiff: {{diff}}");
        var sut = new ReviewCommand(gitHelper, pm, new LspDiagnosticRegistry(), new ReviewCacheService(NullLogger<ReviewCacheService>.Instance));
        var result = await sut.ExecuteAsync(["--no-edit"], ct);

        var prompt = result.As<CommandResult.PromptResult>();
        prompt.AllowedTools.Should().NotContain("Edit(*)");
        prompt.AllowedTools.Should().NotContain("Write(*)");
    }

    [Fact]
    public async Task ReviewCommand_PromptNotFound_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "diff content", ""));
        gitHelper.ReadAsync(Arg.Any<string[]>(), ct)
            .Returns("main", "log", "hash1");

        // 空 PromptManager，没有注册模板
        var sut = new ReviewCommand(gitHelper, new PromptManager(), new LspDiagnosticRegistry(), new ReviewCacheService(NullLogger<ReviewCacheService>.Instance));
        var result = await sut.ExecuteAsync([], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("system/review");
    }

    [Fact]
    public async Task ReviewCommand_CustomTemplate_LoadsFromPromptManager()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "diff content", ""));
        gitHelper.ReadAsync(Arg.Any<string[]>(), ct)
            .Returns("main", "log", "hash1");

        var pm = CreatePromptManager("system/review",
            "CUSTOM REVIEW\nScope: {{scopeDescription}}\nDiff: {{diff}}");

        var sut = new ReviewCommand(gitHelper, pm, new LspDiagnosticRegistry(), new ReviewCacheService(NullLogger<ReviewCacheService>.Instance));
        var result = await sut.ExecuteAsync([], ct);

        var prompt = result.As<CommandResult.PromptResult>();
        prompt.Content.Should().Contain("CUSTOM REVIEW");
        prompt.Content.Should().Contain("Scope: unstaged changes");
        prompt.Content.Should().Contain("Diff: diff content");
    }

    [Theory]
    [InlineData("security")]
    [InlineData("crashes")]
    [InlineData("performance")]
    [InlineData("style")]
    public async Task ReviewCommand_Focus_LoadsSpecializedPrompt(string focus)
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "diff content", ""));
        gitHelper.ReadAsync(Arg.Any<string[]>(), ct)
            .Returns("main", "log", "hash1");

        var promptName = $"system/review-{focus}";
        // 用拼接而非内插字符串，避免 {{ }} 被转义成单大括号
        var pm = CreatePromptManager(promptName,
            "FOCUS=" + focus + "\nScope: {{scopeDescription}}\nDiff: {{diff}}");

        var sut = new ReviewCommand(gitHelper, pm, new LspDiagnosticRegistry(), new ReviewCacheService(NullLogger<ReviewCacheService>.Instance));
        var result = await sut.ExecuteAsync(["--focus", focus], ct);

        var prompt = result.As<CommandResult.PromptResult>();
        prompt.Content.Should().Contain($"FOCUS={focus}");
        prompt.Content.Should().Contain("Diff: diff content");
    }

    [Fact]
    public async Task ReviewCommand_FocusSecurity_LeavesDefaultPromptUnused()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "diff content", ""));
        gitHelper.ReadAsync(Arg.Any<string[]>(), ct)
            .Returns("main", "log", "hash1");

        // 只注册 system/review-security，不注册 system/review
        var pm = CreatePromptManager("system/review-security",
            "SECURITY ONLY\nDiff: {{diff}}");

        var sut = new ReviewCommand(gitHelper, pm, new LspDiagnosticRegistry(), new ReviewCacheService(NullLogger<ReviewCacheService>.Instance));
        var result = await sut.ExecuteAsync(["--focus", "security"], ct);

        var prompt = result.As<CommandResult.PromptResult>();
        prompt.Content.Should().Contain("SECURITY ONLY");
    }

    [Fact]
    public async Task ReviewCommand_UnknownFocus_ReturnsErrorBeforeGitWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var gitHelper = Substitute.For<IGitHelper>();
        // gitHelper should never be invoked for an invalid focus value
        gitHelper.RunAsync(Arg.Any<string[]>(), ct)
            .Returns(new GitCommandResult(true, "should-not-reach", ""));

        var sut = new ReviewCommand(gitHelper, new PromptManager(), new LspDiagnosticRegistry(), new ReviewCacheService(NullLogger<ReviewCacheService>.Instance));
        var result = await sut.ExecuteAsync(["--focus", "nonexistent"], ct);

        result.Should().BeOfType<CommandResult.ErrorResult>()
            .Which.Message.Should().Contain("Unknown --focus value 'nonexistent'");
        await gitHelper.DidNotReceive().RunAsync(Arg.Any<string[]>(), ct);
    }

    // Helpers

    private static PromptManager CreatePromptManager(string name, string template)
    {
        var pm = new PromptManager();
        pm.RegisterTemplate(new PromptTemplate(name, template));
        return pm;
    }

    private static IGitHelper CreateGitHelperReturning(
        string status, string diff, string branch, string log)
    {
        var helper = Substitute.For<IGitHelper>();
        // GitContextSnapshot.ReadAsync 调用 ReadAsync 三次分别获取 status/diff/log，
        // 再加一次 branch。NSubstitute 的 Returns(params object[]) 按顺序返回。
        helper.ReadAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(status, diff, branch, log);
        return helper;
    }

    private sealed class TestFileSystem : IFileSystem
    {
        public Task<string?> ReadTextFileAsync(string path, CancellationToken ct = default)
            => Task.Run<string?>(() => File.Exists(path) ? File.ReadAllText(path) : null, ct);

        public async Task WriteTextFileAsync(string path, string content, CancellationToken ct = default)
            => await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);

        public IReadOnlyList<string> FindFiles(string directory, string? patterns = null, string[]? excludeDirs = null)
            => Directory.GetFiles(directory, patterns ?? "*");

        public bool MatchesGlob(string filePath, string pattern) => true;

        public long GetMtimeMs(string path) => File.GetLastWriteTimeUtc(path).Ticks / TimeSpan.TicksPerMillisecond;
    }
}
