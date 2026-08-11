using Microsoft.Extensions.FileSystemGlobbing;
using OneCode.App.Tools;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="FileIgnore"/>
/// </summary>
public sealed class FileIgnoreTests
{
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

    [Fact]
    public void ApplyExcludes_WithoutIncludeRules_HasNoMatches()
    {
        // 回归保护：若未添加任何 include 规则，Matcher.Match 永远返回 HasMatches=false。
        // 此测试明确记录该行为，避免未来误以为 exclude 单独生效。
        var matcher = new Matcher();

        FileIgnore.ApplyExcludes(matcher);

        matcher.Match("src/main.cs").HasMatches.Should().BeFalse("无 include 规则时任何路径都不匹配");
    }

}
