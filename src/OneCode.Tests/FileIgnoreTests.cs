using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Search;
using OneCode.App.Tools;
using OneCode.Core.IO;
using OneCode.Core.Tools;
using OneCode.Infrastructure;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="FileIgnore"/>
/// </summary>
public sealed class FileIgnoreTests
{
    [Fact]
    public async Task WorkspaceIgnore_UsesProjectRulesWithoutRestoringBuiltInIgnores()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".onecodeignore"), "cache/\n!cache/keep.txt\nnotes/*.md");
            var workingDirectory = Substitute.For<IWorkingDirectoryAccessor>();
            workingDirectory.WorkingDirectory.Returns(root);
            var fileSystem = Substitute.For<IFileSystem>();
            fileSystem.GetMtimeMs(Arg.Any<string>()).Returns(call =>
            {
                var path = call.Arg<string>();
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
            });
            fileSystem.ReadTextFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var path = call.Arg<string>();
                    return File.Exists(path)
                        ? Task.FromResult<string?>(File.ReadAllText(path))
                        : Task.FromResult<string?>(null);
                });

            var snapshot = await new WorkspaceIgnoreProvider(workingDirectory, fileSystem).GetSnapshotAsync();

            snapshot.IsIgnored("cache/data.txt").Should().BeTrue();
            // gitignore 语义：`cache/` 排除目录本身后，`!cache/keep.txt` 无法恢复其下文件
            // （父目录被剪枝），与 ripgrep `--ignore-file` 的遍历行为一致。
            snapshot.IsIgnored("cache/keep.txt").Should().BeTrue();
            snapshot.IsIgnored("notes/today.md").Should().BeTrue();
            snapshot.IsIgnored("bin/app.dll").Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceIgnore_DotfilesAndRootAnchoredRulesMatchLikeGitignore()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // `/generated/` 验证根锚定规则不影响子目录同名目录
            // （注意不能用 `build/`/`bin/` 等：它们命中 FileIgnore 内置段规则）。
            File.WriteAllText(Path.Combine(root, ".onecodeignore"), ".env.*\n!.env.example\n/generated/\nnotes/*.md");
            var workingDirectory = Substitute.For<IWorkingDirectoryAccessor>();
            workingDirectory.WorkingDirectory.Returns(root);
            var fileSystem = Substitute.For<IFileSystem>();
            fileSystem.GetMtimeMs(Arg.Any<string>()).Returns(call =>
            {
                var path = call.Arg<string>();
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
            });
            fileSystem.ReadTextFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var path = call.Arg<string>();
                    return File.Exists(path)
                        ? Task.FromResult<string?>(File.ReadAllText(path))
                        : Task.FromResult<string?>(null);
                });

            var snapshot = await new WorkspaceIgnoreProvider(workingDirectory, fileSystem).GetSnapshotAsync();

            snapshot.IsIgnored(".env.local").Should().BeTrue();
            snapshot.IsIgnored("src/.env.local").Should().BeTrue();
            snapshot.IsIgnored(".env.example").Should().BeFalse();           // `!` 恢复
            snapshot.IsIgnored("generated/output.js").Should().BeTrue();     // 根锚定命中
            snapshot.IsIgnored("src/generated/output.js").Should().BeFalse(); // 根锚定不匹配子目录
            snapshot.IsIgnored("notes/today.md").Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>加载 <paramref name="root"/> 下的 `.onecodeignore` 内容并返回快照。</summary>
    private static async Task<WorkspaceIgnoreSnapshot> LoadIgnoreAsync(string root, string ignoreContent)
    {
        File.WriteAllText(Path.Combine(root, ".onecodeignore"), ignoreContent);
        return await LoadIgnoreFromContentAsync(root, ignoreContent);
    }

    /// <summary>按给定内容构造 ignore 快照（不要求磁盘上真实存在 ignore 文件）。</summary>
    private static async Task<WorkspaceIgnoreSnapshot> LoadIgnoreFromContentAsync(string root, string ignoreContent)
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(root);
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.GetMtimeMs(Arg.Any<string>()).Returns(call =>
        {
            var path = call.Arg<string>();
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        });
        fileSystem.ReadTextFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(ignoreContent));
        return await new WorkspaceIgnoreProvider(wd, fileSystem).GetSnapshotAsync();
    }

    /// <summary>
    /// 规则翻译矩阵：4 类典型规则（`*.log` / `data/**` / `cache/` / `/foo`）× 根级与子目录目标，
    /// 逐格断言 gitignore 语义（锚定以“去掉首尾 `/` 后中间含 `/`”为准，无锚定规则在任意层级生效）。
    /// 这是 `WorkspaceIgnoreProvider` 翻译层的防回归矩阵（计划 §7.2）。
    /// </summary>
    [Theory]
    [InlineData("*.dump", "error.dump", true)]         // 无锚定文件规则命中根级
    [InlineData("*.dump", "src/error.dump", true)]     // ……及任意子目录
    [InlineData("data/**", "data/a.txt", true)]        // “目录之下”规则命中根级
    [InlineData("data/**", "src/data/a.txt", false)]   // ……且仅根级锚定（中间含 `/`）
    [InlineData("cache/", "cache/x.txt", true)]        // 无锚定目录规则命中根级
    [InlineData("cache/", "src/cache/x.txt", true)]    // ……及任意子目录
    [InlineData("cache/", "src/cache", false)]         // 目录规则不匹配同名文件（与 rg `--ignore-file` 一致）
    [InlineData("/foo", "foo", true)]                  // 前导 `/` 锚定根级
    [InlineData("/foo", "src/foo", false)]             // ……不影响子目录同名路径
    [InlineData("/foo", "foo/bar.txt", true)]          // 锚定规则的目录剪枝生效
    [InlineData("notes/*.md", "notes/today.md", true)] // 中间含 `/` → 根锚定
    [InlineData("notes/*.md", "src/notes/today.md", false)]
    public async Task WorkspaceIgnore_TranslationMatrix_FollowsGitignoreSemantics(
        string rule, string target, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = await LoadIgnoreAsync(root, rule + "\n");

            snapshot.IsIgnored(target).Should().Be(expected, $"rule '{rule}' vs '{target}'");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// `!` 否定与目录剪枝交互：gitignore 规定父目录被排除后 `!` 无法恢复其下文件
    /// （rg `--ignore-file` 同样剪枝，两条路径一致）；而 `data/**` 只排除 data 目录
    /// 之下而非目录本身，因此 `!data/keep.txt` 仍可恢复。
    /// </summary>
    [Fact]
    public async Task WorkspaceIgnore_Negation_LastMatchWinsPerLevel()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = await LoadIgnoreAsync(root, "cache/\n!cache/keep.txt\ndata/**\n!data/keep.txt");

            snapshot.IsIgnored("cache/keep.txt").Should().BeTrue();      // 父目录被排除：`!` 无法恢复（gitignore 语义）
            snapshot.IsIgnored("src/cache/keep.txt").Should().BeTrue();  // 无锚定目录规则在任意层级剪枝
            snapshot.IsIgnored("data/keep.txt").Should().BeFalse();      // `data/**` 未排除 data 目录本身
            snapshot.IsIgnored("data/inner/deep.bin").Should().BeTrue(); // `data/**` 排除目录之下
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 目录级否定不能恢复已被祖先排除的子树（gitignore 剪枝语义）：
    /// `a/` 排除整树后，`!a/b/` 不得把 `a/b` 之下恢复。
    /// </summary>
    [Fact]
    public async Task WorkspaceIgnore_DirectoryNegation_CannotRestorePrunedSubtree()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = await LoadIgnoreAsync(root, "a/\n!a/b/\n");

            snapshot.IsIgnored("a/b/c.txt").Should().BeTrue("祖先目录已被剪枝，深层目录否定不得恢复");
            snapshot.IsIgnored("a/other.txt").Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// rg/fallback 对等回归（计划 §5.4.1/§7.2）：对同一 `.onecodeignore`（`*`、目录、
    /// `/` 根锚定、`!` 否定），ripgrep `--no-ignore --ignore-file` 与 C# fallback 的
    /// files_with_matches 结果必须一致。环境缺少 rg 时跳过断言（生产路径本就走 fallback）。
    /// </summary>
    [Fact]
    public async Task WorkspaceIgnore_RipgrepAndFallback_AgreeOnSameIgnoreFile()
    {
        var ct = TestContext.Current.CancellationToken;

        var root = Path.Combine(Path.GetTempPath(), "onecode-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // 目录名与扩展名避开 FileIgnore 内置规则（段匹配/内置 glob），隔离用户规则语义。
            File.WriteAllText(Path.Combine(root, ".onecodeignore"), "*.dump\ncache/\n/generated/\n!keep.dump\n");
            foreach (var rel in new[]
                     {
                         "plain.txt", "root.dump", "keep.dump", "cache/nested.txt", "src/plain.txt",
                         "src/cache/nested.txt", "generated/out.txt", "src/generated/out.txt",
                     })
            {
                var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "needle");
            }

            var request = new TextSearchRequest(
                SearchPath: root, Pattern: "needle", OutputMode: "files_with_matches", WorkspaceRoot: root);
            var provider = new StubIgnoreProvider(root);
            var fs = Substitute.For<IFileSystem>();
            fs.FindFiles(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string[]?>())
                .Returns(call =>
                {
                    // FindFiles(string, string?, string[]?) 有两个 string 参数：用位置索引取参，
                    // 避免 NSubstitute 的 Arg<string>() 歧义异常。
                    var dir = call.ArgAt<string>(0);
                    var patterns = call.ArgAt<string?>(1);
                    var excludeDirs = call.ArgAt<string[]?>(2);
                    var wd = Substitute.For<IWorkingDirectoryAccessor>();
                    wd.WorkingDirectory.Returns(root);
                    IFileSystem realFs = new LocalAgentFileStore(wd);
                    return realFs.FindFiles(dir, patterns, excludeDirs);
                });

            // 1) 无 rg 环境也能验证的双向断言：C# fallback 遵循 gitignore 语义
            // （`/generated/` 不匹配 src/generated/，`!keep.dump` 恢复），且跨平台先归一化再比较。
            var fallback = await new TextSearchService(Substitute.For<IProcessRunner>(), fs,
                NullLogger<TextSearchService>.Instance, provider).SearchAsync(request, ct);
            var expected = new[] { "keep.dump", "plain.txt", "src/plain.txt", "src/generated/out.txt" };
            fallback.Select(p => p.Replace('\\', '/')).Should().BeEquivalentTo(expected,
                "C# fallback 遵循 gitignore 语义：`/generated/` 不匹配 src/generated/，`!keep.dump` 恢复");

            // 2) 有 rg 时的对等断言：ripgrep `--no-ignore --ignore-file` 与 fallback 对同一规则必须一致。
            // 环境缺少 rg 时跳过（生产路径本就走 fallback）。
            var runner = new ProcessRunner();
            if (!await runner.CommandExistsAsync("rg"))
                return;

            var rg = await new TextSearchService(runner, fs,
                NullLogger<TextSearchService>.Instance, provider).SearchAsync(request, ct);
            rg.Select(p => p.Replace('\\', '/')).Should().BeEquivalentTo(expected,
                "rg(--no-ignore --ignore-file) 遵循 gitignore 语义：`/generated/` 不匹配 src/generated/，`!keep.dump` 恢复");
            fallback.Select(p => p.Replace('\\', '/')).Should().BeEquivalentTo(rg.Select(p => p.Replace('\\', '/')),
                "两条搜索路径对同一 `.onecodeignore` 必须给出相同结果");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// 缺陷一回归：rg 在 Windows 下对子目录搜索输出反斜杠前缀（`src\...`）时，
    /// <c>TextSearchService</c> 仍须剥离为相对 searchPath 的路径，与 fallback 一致。
    /// 通过伪造 <see cref="IProcessRunner"/> 的 rg 输出验证，不依赖本机安装 rg。
    /// </summary>
    [Fact]
    public async Task TextSearchService_RipgrepWindowsBackslashPrefix_StripsToSearchRelative()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "onecode-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            var searchPath = Path.Combine(root, "src");
            var processRunner = Substitute.For<IProcessRunner>();
            processRunner.CommandExistsAsync("rg").Returns(true);
            // 模拟 rg 以工作区根为运行目录、对子目录搜索返回 Windows 风格 `src\...` 输出。
            processRunner.ExecuteAsync("rg", Arg.Any<string[]>(), root, ct: Arg.Any<CancellationToken>())
                .Returns(new ProcessResult(0, "src\\rg.cs:1:found", "", false));

            var request = new TextSearchRequest(
                SearchPath: searchPath, Pattern: "found", OutputMode: "content", WorkspaceRoot: root);
            var noIgnore = new WorkspaceIgnoreSnapshot(null, [], []);
            var ignoreProvider = Substitute.For<IWorkspaceIgnoreProvider>();
            ignoreProvider.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(noIgnore);

            var results = await new TextSearchService(processRunner, Substitute.For<IFileSystem>(),
                NullLogger<TextSearchService>.Instance, ignoreProvider).SearchAsync(request, ct);

            results.Should().BeEquivalentTo(["rg.cs:1:found"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>读取真实 `.onecodeignore` 并复用生产解析器构造快照的测试桩。</summary>
    private sealed class StubIgnoreProvider(string root) : IWorkspaceIgnoreProvider
    {
        public async Task<WorkspaceIgnoreSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            var content = await File.ReadAllTextAsync(Path.Combine(root, ".onecodeignore"), ct);
            return await LoadIgnoreFromContentAsync(root, content);
        }
    }

    [Theory]
    [InlineData("node_modules/package.json")]
    [InlineData("src/node_modules/package.json")]
    [InlineData(".git/config")]
    [InlineData("src/.git/config")]
    [InlineData("bin/Debug/app.exe")]
    [InlineData("obj/Debug/app.dll")]
    [InlineData(".vscode/settings.json")]
    [InlineData("__pycache__/module.pyc")]
    public void IsIgnored_BlockedFolders_ReturnsTrue(string path)
    {
        FileIgnore.IsIgnored(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("src/main.cs")]
    [InlineData("README.md")]
    [InlineData("package.json")]
    [InlineData("src/components/App.tsx")]
    public void IsIgnored_NormalFiles_ReturnsFalse(string path)
    {
        FileIgnore.IsIgnored(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("temp/swap.swp")]
    [InlineData("backup/file.swo")]
    [InlineData("src/__pycache__/module.pyc")]
    [InlineData(".DS_Store")]
    [InlineData("Thumbs.db")]
    [InlineData("logs/app.log")]
    [InlineData("tmp/tempfile.txt")]
    [InlineData("temp/data.json")]
    [InlineData("build/output.log")]
    [InlineData("coverage/index.html")]
    public void IsIgnored_BlockedFilePatterns_ReturnsTrue(string path)
    {
        FileIgnore.IsIgnored(path).Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_EmptyPath_ReturnsFalse()
    {
        FileIgnore.IsIgnored("").Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_NullPath_ReturnsFalse()
    {
        FileIgnore.IsIgnored(null!).Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_WhitelistOverridesIgnore_ReturnsFalse()
    {
        var path = "node_modules/important-package/index.js";
        var whitelist = new[] { "node_modules/important-package/**" };

        FileIgnore.IsIgnored(path, whitelist: whitelist).Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_ExtraPatterns_AddsToIgnoreList()
    {
        var path = "custom-cache/data.json";
        var extraPatterns = new[] { "**/custom-cache/**" };

        FileIgnore.IsIgnored(path, extraPatterns: extraPatterns).Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_CaseInsensitiveFolderMatch_ReturnsTrue()
    {
        FileIgnore.IsIgnored("NODE_MODULES/package.json").Should().BeTrue();
        FileIgnore.IsIgnored("Node_Modules/package.json").Should().BeTrue();
    }

    [Fact]
    public void ApplyExcludes_AddsAllRulesToMatcher()
    {
        // Matcher 只有在添加了 include 规则后，Match 才会返回 HasMatches=true。
        // exclude 规则的作用是从匹配结果中移除文件。
        // 因此必须先添加 include("**/*")，再验证被排除的文件不匹配、普通文件匹配。
        var matcher = new Matcher();
        matcher.AddInclude("**/*");

        FileIgnore.ApplyExcludes(matcher);

        // 被排除的目录和文件模式不应出现在匹配结果中
        matcher.Match("node_modules/package.json").HasMatches.Should().BeFalse("node_modules 应被 exclude 排除");
        matcher.Match(".git/config").HasMatches.Should().BeFalse(".git 应被 exclude 排除");
        matcher.Match("bin/Debug/app.exe").HasMatches.Should().BeFalse("bin 应被 exclude 排除");
        matcher.Match("obj/Debug/app.dll").HasMatches.Should().BeFalse("obj 应被 exclude 排除");
        matcher.Match("logs/app.log").HasMatches.Should().BeFalse("*.log 应被 exclude 排除");
        matcher.Match("temp/swap.swp").HasMatches.Should().BeFalse("*.swp 应被 exclude 排除");
        matcher.Match(".DS_Store").HasMatches.Should().BeFalse(".DS_Store 应被 exclude 排除");

        // 普通源码文件应通过匹配（未被排除）
        matcher.Match("src/main.cs").HasMatches.Should().BeTrue("普通源码文件应通过匹配");
        matcher.Match("README.md").HasMatches.Should().BeTrue("普通文档应通过匹配");
    }

}
