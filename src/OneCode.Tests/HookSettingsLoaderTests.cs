using System.Text;
using NSubstitute;
using OneCode.Core.Hooks;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Hooks;

namespace OneCode.Tests;

/// <summary>
/// HookSettingsLoader 单测——覆盖：
///   文件缺失（NotFound）→ 正常加载 → 未知事件名警告
///   → JSON 损坏（含行号）→ 根节点不是对象 → 全部经 HookConfigBootstrapper 落入诊断
/// </summary>
public sealed class HookSettingsLoaderTests : IDisposable
{
    private readonly string _configDir;

    public HookSettingsLoaderTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "onecode-hook-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "onecode-hook-loader-tests"), recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论 */ }
    }

    private HookSettingsLoader CreateSut() => new(NullLogger<HookSettingsLoader>.Instance);

    private void WriteHooksJson(string content)
    {
        File.WriteAllText(Path.Combine(_configDir, "hooks.json"), content, new UTF8Encoding(false));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNotFound()
    {
        var result = CreateSut().Load(_configDir);

        result.Status.Should().Be(HookFileLoadStatus.NotFound);
        result.Hooks.Should().BeNull();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Load_ValidConfiguration_ParsesEventGroups()
    {
        WriteHooksJson("""
            {
              "PreToolUse": [
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo hi" } ] }
              ]
            }
            """);

        var result = CreateSut().Load(_configDir);

        result.Status.Should().Be(HookFileLoadStatus.Loaded);
        result.Hooks.Should().NotBeNull();
        result.Hooks!.Should().ContainKey(HookEvent.PreToolUse);
        result.Hooks![HookEvent.PreToolUse].Should().ContainSingle();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Load_UnknownEventName_RecordsWarningAndSkipsOnlyThatEvent()
    {
        WriteHooksJson("""
            {
              "NotARealEvent": [
                { "matcher": "*", "hooks": [ { "type": "command", "command": "echo hi" } ] }
              ],
              "Stop": [
                { "matcher": "Completed", "hooks": [ { "type": "command", "command": "echo done" } ] }
              ]
            }
            """);

        var result = CreateSut().Load(_configDir);

        result.Status.Should().Be(HookFileLoadStatus.Loaded);
        result.Hooks!.Keys.Should().ContainSingle().Which.Should().Be(HookEvent.Stop);
        result.Errors.Should().ContainSingle();
        result.Errors[0].Should().Contain("NotARealEvent");
    }

    [Fact]
    public void Load_BrokenJson_FailsWithLineNumber()
    {
        WriteHooksJson("""
            {
              "Stop": [

            "matcher": broken
            """);

        var result = CreateSut().Load(_configDir);

        result.Status.Should().Be(HookFileLoadStatus.Failed);
        result.Hooks.Should().BeNull();
        result.Errors.Should().ContainSingle();
        result.Errors[0].Should().Contain("行");
    }

    [Fact]
    public void Load_NonObjectRoot_Fails()
    {
        WriteHooksJson("[]");

        var result = CreateSut().Load(_configDir);

        result.Status.Should().Be(HookFileLoadStatus.Failed);
        result.Errors[0].Should().Contain("JSON 对象");
    }

    [Fact]
    public void Bootstrap_RecordsPerFileDiagnostics()
    {
        WriteHooksJson("""
            {
              "MysteryEvent": [
                { "matcher": "*", "hooks": [ { "type": "command", "command": "echo hi" } ] }
              ],
              "Stop": [
                { "matcher": "Completed", "hooks": [ { "type": "command", "command": "echo done" } ] }
              ]
            }
            """);

        var diagnostics = new HookLoadDiagnostics();
        var providerRegistry = new NotificationProviderRegistry(
            [], Substitute.For<IHttpClientFactory>(), NullLoggerFactory.Instance);
        var bootstrapper = new HookConfigBootstrapper(
            CreateSut(),
            new HookRegistry(new GlobHookMatcher()),
            new NotificationProviderDefinitionLoader(NullLogger<NotificationProviderDefinitionLoader>.Instance),
            providerRegistry,
            diagnostics,
            NullLogger<HookConfigBootstrapper>.Instance);

        bootstrapper.Bootstrap(_configDir);

        diagnostics.Reports.Should().ContainSingle();
        var report = diagnostics.Reports[0];
        report.Status.Should().Be(HookFileLoadStatus.Loaded);
        report.HookCount.Should().Be(1);
        report.Errors.Should().ContainSingle(e => e.Contains("MysteryEvent"));
    }
}
