using OneCode.App.Tools;
using OneCode.Core.Permissions;

namespace OneCode.Tests;

/// <summary>
/// <see cref="ShellExecutionHelper.ValidateReferencedPaths"/> 的边界行为锁：
/// 展开风险操作数（$ / 反引号）必须拒绝（此前 continue 跳过属 fail-open，
/// 命令替换可把写入解析到工作区外任意位置）；纯通配操作数按静态前缀校验，
/// 保留工作区内 glob 用法、拦截越界前缀。
/// </summary>
public sealed class ShellExecutionHelperTests : IDisposable
{
    private readonly string _workspace;

    public ShellExecutionHelperTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"shell-path-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ValidateReferencedPaths_MidTokenExpansionOperand_IsRejected()
    {
        // 中段展开（src/$VAR、path/$(cmd)）会被提取为路径候选但无法静态校验——
        // 此前被 continue 跳过属 fail-open，展开可把读写解析到工作区外任意位置。
        var error = ShellExecutionHelper.ValidateReferencedPaths(
            "cat src/$SECRET_PATH/config",
            _workspace,
            BashCommandClassifier.ExtractReferencedPaths);

        error.Should().NotBeNull("中段含变量展开的操作数不得放行");
        error.Should().Contain("expansion");
    }

    [Fact]
    public void ValidateReferencedPaths_MidTokenBacktickOperand_IsRejected()
    {
        var error = ShellExecutionHelper.ValidateReferencedPaths(
            "cat src/`pwd`/config",
            _workspace,
            BashCommandClassifier.ExtractReferencedPaths);

        error.Should().NotBeNull("中段含命令替换的操作数不得放行");
    }

    [Theory]
    [InlineData("cat $OUTSIDE", false)]
    [InlineData("cd $OUTSIDE", false)]
    [InlineData("echo data > $OUT", false)]
    [InlineData("Get-Content $env:OUTSIDE", true)]
    [InlineData("Set-Location $env:OUTSIDE", true)]
    [InlineData("Set-Content -Path $env:OUT -Value data", true)]
    [InlineData("Set-Location $PWD", true)]
    [InlineData("Get-Content -Path:$p", true)]
    [InlineData("Get-Content $(Resolve-Path .)", true)]
    public void ValidateReferencedPaths_LeadingExpansionOperand_IsRejected(string command, bool powerShell)
    {
        var error = ShellExecutionHelper.ValidateReferencedPaths(
            command,
            _workspace,
            powerShell
                ? PowerShellCommandClassifier.ExtractReferencedPaths
                : BashCommandClassifier.ExtractReferencedPaths);

        error.Should().NotBeNull().And.Contain("expansion");
    }

    [Fact]
    public void ValidateReferencedPaths_LiteralPowerShellPathInsideWorkspace_IsAllowed()
    {
        var error = ShellExecutionHelper.ValidateReferencedPaths(
            "Get-Content src/config.json",
            _workspace,
            PowerShellCommandClassifier.ExtractReferencedPaths);

        error.Should().BeNull("字面工作区路径可静态证明边界，不应被展开风险策略误伤");
    }

    [Fact]
    public void ValidateReferencedPaths_InsideWorkspaceWildcard_PrefixCheckAllows()
    {
        var error = ShellExecutionHelper.ValidateReferencedPaths(
            "cat src/*.cs",
            _workspace,
            BashCommandClassifier.ExtractReferencedPaths);

        error.Should().BeNull("工作区内 glob 是合法用法，静态前缀 src 在边界内");
    }

    [Fact]
    public void ValidateReferencedPaths_OutsideWorkspaceWildcard_PrefixCheckRejects()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_workspace)!, "elsewhere");
        var command = $"cat ../elsewhere/*.cs";

        var error = ShellExecutionHelper.ValidateReferencedPaths(
            command,
            _workspace,
            BashCommandClassifier.ExtractReferencedPaths);

        error.Should().NotBeNull("越界静态前缀（..）不得因通配符被跳过");
        error.Should().Contain("outside the working directory");
    }

    [Fact]
    public void ValidateReferencedPaths_PlainLiteralPathOutside_IsRejected()
    {
        var error = ShellExecutionHelper.ValidateReferencedPaths(
            $"cat {Path.Combine(Path.GetTempPath(), "elsewhere", "file.txt")}",
            _workspace,
            BashCommandClassifier.ExtractReferencedPaths);

        error.Should().NotBeNull();
        error.Should().Contain("outside the working directory");
    }
}
