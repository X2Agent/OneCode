using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Hooks;

namespace OneCode.Tests;

/// <summary>
/// HookConfigBootstrapper.Build 快照语义单测——覆盖：
///   Build 纯读取（不触碰 HookRegistry）→ 注册名带 config: 前缀（热重载 preserved 过滤依据）
/// </summary>
public sealed class HookConfigBootstrapperTests : IDisposable
{
    private readonly string _configDir;
    private readonly HookRegistry _registry;
    private readonly HookConfigBootstrapper _bootstrapper;

    public HookConfigBootstrapperTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "onecode-hook-bootstrapper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);

        _registry = new HookRegistry(new GlobHookMatcher());
        _bootstrapper = new HookConfigBootstrapper(
            new HookSettingsLoader(NullLogger<HookSettingsLoader>.Instance),
            _registry,
            new NotificationProviderDefinitionLoader(NullLogger<NotificationProviderDefinitionLoader>.Instance),
            new NotificationProviderRegistry(
                [], Substitute.For<IHttpClientFactory>(), NullLoggerFactory.Instance),
            new HookLoadDiagnostics(),
            NullLogger<HookConfigBootstrapper>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "onecode-hook-bootstrapper-tests"), recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    [Fact]
    public void Build_ReturnsRegistrationsWithoutTouchingRegistry()
    {
        File.WriteAllText(Path.Combine(_configDir, "hooks.json"), """
            {
              "pre_tool_call": [
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo hi" } ] }
              ]
            }
            """, new UTF8Encoding(false));

        var snapshot = _bootstrapper.Build(_configDir);

        snapshot.Reports.Should().ContainSingle().Which.Status.Should().Be(HookFileLoadStatus.Loaded);
        snapshot.Registrations.Should().ContainSingle();
        snapshot.Registrations[0].Name.Should().StartWith(
            HookConfigBootstrapper.ConfigNamePrefix,
            "热重载按 config: 前缀区分配置 hook 与编程注册 hook");
        _registry.GetAll().Should().BeEmpty("Build 是纯读取，不得修改注册表");
    }
}