using System.Text.Json;
using OneCode.Core.Config;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// 信任门的持久化契约：只有用户在浮层上真正确认过，目录才可以进入
/// <c>trustedDirectories</c>。预 TUI 阶段的会话级放行不得落盘，否则 TUI 就绪后的
/// 补弹会被「已信任」判定静默吃掉，用户永远看不到确认框。
/// </summary>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class TrustServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _userDir;
    private readonly string _workspaceDir;
    private readonly string _originalCwd;

    public TrustServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"TrustServiceTests_{Guid.NewGuid():N}");
        _userDir = Path.Combine(_root, "user");
        _workspaceDir = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_userDir);
        Directory.CreateDirectory(_workspaceDir);

        _originalCwd = Environment.CurrentDirectory;
        Directory.SetCurrentDirectory(_workspaceDir);
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_originalCwd); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string UserSettingsPath => Path.Combine(_userDir, "settings.json");

    private ConfigManager CreateConfigManager() => new(_userDir);

    [Fact]
    public async Task EnsureTrustAsync_BeforeOverlayHostWired_DoesNotPersistTrust()
    {
        // 回归锚点：预 TUI 阶段（浮层宿主未接线）只允许会话放行。曾经这里无条件
        // AddTrustedDirectoryAsync，导致目录在用户从未确认的情况下被写入配置，
        // 随后 TUI 就绪的补弹因 IsDirectoryTrusted 命中而被跳过。
        using var config = CreateConfigManager();
        var sut = new TrustService(config) { IsInteractive = () => true };

        var accepted = await sut.EnsureTrustAsync(TestContext.Current.CancellationToken);

        accepted.Should().BeTrue();
        sut.NeedsPostTuiConfirmation.Should().BeTrue("TUI 就绪后必须补弹确认框");
        File.Exists(UserSettingsPath).Should().BeFalse("用户尚未确认，不得写入任何配置");
    }

    [Fact]
    public async Task EnsureTrustAsync_BeforeOverlayHostWired_StillPromptsAfterResetForPostTuiCheck()
    {
        using var config = CreateConfigManager();
        var sut = new TrustService(config) { IsInteractive = () => true };
        await sut.EnsureTrustAsync(TestContext.Current.CancellationToken);

        sut.ResetSessionTrustForPostTuiCheck();

        sut.ShouldShowTrustPrompt().Should().BeTrue(
            "预 TUI 放行没有落盘，补弹必须重新要求确认；若这里为 false，说明信任已被静默写入");
    }

    [Fact]
    public async Task EnsureTrustAsync_NonInteractiveConsole_DoesNotPersistTrust()
    {
        // 重定向 stdin / 非交互宿主同样只是本次会话放行：持久化会让下一次真实交互
        // 启动直接跳过确认框。
        using var config = CreateConfigManager();
        var sut = new TrustService(config) { IsInteractive = () => false };

        var accepted = await sut.EnsureTrustAsync(TestContext.Current.CancellationToken);

        accepted.Should().BeTrue();
        File.Exists(UserSettingsPath).Should().BeFalse();
        sut.ResetSessionTrustForPostTuiCheck();
        sut.ShouldShowTrustPrompt().Should().BeTrue();
    }

    [Fact]
    public async Task EnsureTrustAsync_UserConfirmsInOverlay_PersistsWorkspaceDirectory()
    {
        using var config = CreateConfigManager();
        var sut = new TrustService(config) { IsInteractive = () => true };
        var host = new OverlayHost(() => { });
        sut.SetOverlayDelegates(host.Push, () => host.Pop());

        var pending = sut.EnsureTrustAsync(TestContext.Current.CancellationToken);
        var overlay = host.Top.Should().BeOfType<TrustOverlay>().Subject;
        await AcceptAsync(overlay);

        (await pending).Should().BeTrue();
        ReadTrustedDirectories().Should().Contain(
            Path.GetFullPath(_workspaceDir),
            "用户在浮层上确认后必须记住该目录");
    }

    [Fact]
    public async Task EnsureTrustAsync_UserDeclinesInOverlay_AbortsWithoutPersisting()
    {
        using var config = CreateConfigManager();
        var sut = new TrustService(config) { IsInteractive = () => true };
        var host = new OverlayHost(() => { });
        sut.SetOverlayDelegates(host.Push, () => host.Pop());

        var pending = sut.EnsureTrustAsync(TestContext.Current.CancellationToken);
        var overlay = host.Top.Should().BeOfType<TrustOverlay>().Subject;
        overlay.NewKeyDownEvent(Terminal.Gui.Input.Key.Esc).Should().BeTrue();

        (await pending).Should().BeFalse();
        host.Depth.Should().Be(0);
        File.Exists(UserSettingsPath).Should().BeFalse("拒绝信任不得写入任何信任记录");
    }

    [Fact]
    public async Task EnsureTrustAsync_AlreadyTrustedParent_SkipsPromptWithoutWriting()
    {
        // 子目录隐式继承父目录信任；已受信任时不得重复写配置。
        using (var seed = CreateConfigManager())
        {
            await seed.ApplyAsync(
                ConfigPatch.Set(ConfigScope.User, "trustedDirectories", new[] { Path.GetFullPath(_root) }),
                TestContext.Current.CancellationToken);
        }

        using var config = CreateConfigManager();
        var sut = new TrustService(config) { IsInteractive = () => true };
        var pushed = false;
        sut.SetOverlayDelegates(_ => pushed = true, () => { });

        sut.ShouldShowTrustPrompt().Should().BeFalse();
        (await sut.EnsureTrustAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        pushed.Should().BeFalse("已受信任的目录不得再弹确认框");
    }

    [Fact]
    public async Task EnsureTrustAsync_HomeDirWrittenInDifferentCase_IsStillTreatedAsHome()
    {
        // Windows 上 `c:\users\x` 与 `C:\Users\x` 是同一目录。若主目录判定用序数 ==，
        // 小写盘符启动会把用户主目录当成普通工作区写进 trustedDirectories——
        // 那等于 hasTrustAccepted，全局信任被一次误判永久打开。
        var home = PathsHelper.UserHome;
        if (!OperatingSystem.IsWindows() || !IsAsciiCaseOnlyDifferent(home))
            return;

        Directory.SetCurrentDirectory(home.ToLowerInvariant());

        using var config = CreateConfigManager();
        var sut = new TrustService(config) { IsInteractive = () => true };
        var host = new OverlayHost(() => { });
        sut.SetOverlayDelegates(host.Push, () => host.Pop());

        var pending = sut.EnsureTrustAsync(TestContext.Current.CancellationToken);
        var overlay = host.Top.Should().BeOfType<TrustOverlay>().Subject;
        overlay.Should().NotBeNull();
        await AcceptAsync(overlay);

        (await pending).Should().BeTrue();
        File.Exists(UserSettingsPath).Should().BeFalse(
            "用户主目录永不落盘，否则等价于打开全局信任");
    }

    /// <summary>路径含字母才可能有大小写差异；全数字/符号的主目录无法构造该用例。</summary>
    private static bool IsAsciiCaseOnlyDifferent(string path) =>
        path.Any(char.IsAsciiLetter)
        && !string.Equals(path, path.ToLowerInvariant(), StringComparison.Ordinal);

    private static async Task AcceptAsync(TrustOverlay overlay)
    {
        overlay.YesButton.InvokeCommand(Terminal.Gui.Input.Command.Accept);
        await Task.Yield();
    }

    private List<string> ReadTrustedDirectories()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(UserSettingsPath));
        return document.RootElement.TryGetProperty("trustedDirectories", out var list)
            ? list.EnumerateArray().Select(item => item.GetString()!).ToList()
            : [];
    }
}
