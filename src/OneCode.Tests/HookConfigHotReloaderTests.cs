using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Hooks;
using OneCode.Core.Hooks;

namespace OneCode.Tests;

/// <summary>
/// HookConfigHotReloader 集成单测（真实 FileSystemWatcher + 短防抖窗口 + 轮询等待）——覆盖：
///   BootstrapAndStartWatching 同步完成初始注册
///   → hooks.json 变更 → 防抖后整体重建（旧配置被替换）
///   → JSON 损坏 → 保留 last-good（注册表不清空，诊断记录 Failed）
///   → 编程注册 hook（非 config: 前缀）在重建后保留
///   → hooks.json 删除 → config hook 清空（NotFound 也参与整体交换）
/// </summary>
public sealed class HookConfigHotReloaderTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly string _configDir;
    private readonly HookRegistry _registry;
    private readonly HookLoadDiagnostics _diagnostics;
    private readonly HookConfigHotReloader _reloader;

    public HookConfigHotReloaderTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "onecode-hook-reloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);

        _registry = new HookRegistry(new GlobHookMatcher());
        _diagnostics = new HookLoadDiagnostics();
        var providerRegistry = new NotificationProviderRegistry(
            [], Substitute.For<IHttpClientFactory>(), NullLoggerFactory.Instance);
        var bootstrapper = new HookConfigBootstrapper(
            new HookSettingsLoader(NullLogger<HookSettingsLoader>.Instance),
            _registry,
            new NotificationProviderDefinitionLoader(NullLogger<NotificationProviderDefinitionLoader>.Instance),
            providerRegistry,
            _diagnostics,
            NullLogger<HookConfigBootstrapper>.Instance);

        _reloader = new HookConfigHotReloader(
            bootstrapper, _registry, _diagnostics, providerRegistry,
            NullLogger<HookConfigHotReloader>.Instance)
        { DebounceMs = 100 };
    }

    public void Dispose()
    {
        _reloader.Dispose();
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "onecode-hook-reloader-tests"), recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    // 启动路径

    [Fact]
    public void BootstrapAndStartWatching_RegistersInitialHooksSynchronously()
    {
        WriteHooksJson("""
            {
              "PreToolUse": [
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo v1" } ] }
              ]
            }
            """);

        _reloader.BootstrapAndStartWatching(_configDir, projectConfigDir: null);

        _registry.GetAll().Should().ContainSingle("初始 Bootstrap 是同步的，返回后注册表已就绪");
        _diagnostics.Reports.Should().ContainSingle().Which.Status.Should().Be(HookFileLoadStatus.Loaded);
    }

    // 热重载：正常重建

    [Fact]
    public void HooksJsonChanged_RegistryRebuiltAfterDebounce()
    {
        WriteHooksJson("""
            {
              "PreToolUse": [
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo v1" } ] }
              ]
            }
            """);
        _reloader.BootstrapAndStartWatching(_configDir, projectConfigDir: null);

        WriteHooksJson("""
            {
              "PreToolUse": [
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo v1" } ] }
              ],
              "Stop": [
                { "matcher": "Completed", "hooks": [ { "type": "command", "command": "echo v2" } ] }
              ]
            }
            """);

        WaitUntil(() => _registry.GetAll().Count == 2, "热重载应在防抖窗口后重建出 2 个 hook");
        _registry.GetMatchesForEvent(HookEvent.Stop, "Completed").Should().ContainSingle();
    }

    // 热重载：last-good 保护

    [Fact]
    public void HooksJsonBroken_KeepsLastGoodConfigAndRecordsFailedDiagnostics()
    {
        WriteHooksJson("""
            {
              "PreToolUse": [
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo v1" } ] }
              ]
            }
            """);
        _reloader.BootstrapAndStartWatching(_configDir, projectConfigDir: null);

        WriteHooksJson("{ broken json");

        WaitUntil(
            () => _diagnostics.Reports.Any(r => r.Status == HookFileLoadStatus.Failed),
            "热重载应把解析失败写入诊断");
        _registry.GetAll().Should().ContainSingle("解析失败时必须保留上一次有效配置");
    }

    // 编程注册 hook 保留

    [Fact]
    public void Rebuild_PreservesProgrammaticallyRegisteredHooks()
    {
        WriteHooksJson("""
            {
              "PreToolUse": [
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo v1" } ] }
              ]
            }
            """);
        _reloader.BootstrapAndStartWatching(_configDir, projectConfigDir: null);
        _registry.Register(new HookRegistration
        {
            Name = "my-plugin:audit",
            Event = HookEvent.PreToolUse,
            Matcher = "Bash",
            ExecutorType = HookType.Command,
            TimeoutMs = 5000,
            Config = new HookConfig(),
        });

        WriteHooksJson("""
            {
              "PreToolUse": [
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo v1" } ] },
                { "matcher": "*", "hooks": [ { "type": "command", "command": "echo v2" } ] }
              ]
            }
            """);

        WaitUntil(() => _registry.GetAll().Count == 3, "重建后应为 1 个编程注册 hook + 2 个 config hook");
        _registry.GetAll().Should().Contain(h => h.Name == "my-plugin:audit", "编程注册 hook 不得被整体交换清掉");
    }

    // 热重载：文件删除

    [Fact]
    public void HooksJsonDeleted_ConfigHooksRemoved()
    {
        WriteHooksJson("""
            {
              "PreToolUse": [
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo v1" } ] }
              ]
            }
            """);
        _reloader.BootstrapAndStartWatching(_configDir, projectConfigDir: null);

        File.Delete(Path.Combine(_configDir, "hooks.json"));

        WaitUntil(() => _registry.GetAll().Count == 0, "删除 hooks.json 应清空 config hook（NotFound 参与整体交换）");
        _diagnostics.Reports.Should().ContainSingle().Which.Status.Should().Be(HookFileLoadStatus.NotFound);
    }

    // 辅助

    private void WriteHooksJson(string content) =>
        File.WriteAllText(Path.Combine(_configDir, "hooks.json"), content, new UTF8Encoding(false));

    private void WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            Thread.Sleep(50);
        }

        condition().Should().BeTrue($"等待超时：{because}");
    }
}