using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Hooks;
using OneCode.App.Services.Hooks.Notifications;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.Tests;

/// <summary>
/// NotificationProviderDefinitionLoader 单测——覆盖：
/// 文件缺失 → 正常加载 + preset 展开 → 非 https 拒绝 → 未知 preset 跳过 → 缺 body 跳过
/// → JSON 损坏（含行号）→ 大小写不敏感。
/// 三层整条替换合并见文件尾 Bootstrap 用例。
/// </summary>
public sealed class NotificationProviderDefinitionLoaderTests : IDisposable
{
    private readonly string _configDir;

    public NotificationProviderDefinitionLoaderTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"onecode-npdl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { /* ignore */ }
    }

    private void WriteFile(string json)
    {
        File.WriteAllText(Path.Combine(_configDir, NotificationProviderDefinitionLoader.FileName), json);
    }

    private static NotificationProviderDefinitionLoader CreateSut() =>
        new(NullLogger<NotificationProviderDefinitionLoader>.Instance);

    // ---------- 基础路径 ----------

    [Fact]
    public void Load_MissingFile_ReturnsNotFound()
    {
        var result = CreateSut().Load(_configDir);

        result.Status.Should().Be(HookFileLoadStatus.NotFound);
        result.Providers.Should().BeNull();
    }

    [Fact]
    public void Load_ValidDefinition_LoadsAndExpandsPreset()
    {
        WriteFile("""
            {
              "dingtalk": {
                "displayName": "钉钉机器人",
                "method": "POST",
                "url": "https://oapi.dingtalk.com/robot/send",
                "body": { "msgtype": "text", "text": { "content": "{{Text}}" } },
                "signing": { "preset": "dingtalk" },
                "success": { "field": "errcode", "equals": 0 }
              }
            }
            """);

        var result = CreateSut().Load(_configDir);

        result.Status.Should().Be(HookFileLoadStatus.Loaded);
        result.Providers.Should().ContainKey("dingtalk");
        var def = result.Providers!["dingtalk"];
        def.DisplayName.Should().Be("钉钉机器人");
        def.Signing.Should().NotBeNull();
        def.Signing!.Preset.Should().BeNull("preset 应在加载期展开");
        def.Signing.KeyTemplate.Should().Be("{TimestampMs}\n{Secret}");
        def.Signing.MessageTemplate.Should().BeEmpty("钉钉官方算法 message 为空");
        def.Signing.Encoding.Should().Be(ProviderSignatureEncoding.Base64UrlEncoded);
        def.Signing.TimestampInMilliseconds.Should().BeTrue();
        def.Success!.Field.Should().Be("errcode");
        def.Success.Expected.GetInt32().Should().Be(0);
    }

    [Fact]
    public void Load_ProviderNameCaseInsensitive_DictionaryUsesIgnoreCase()
    {
        WriteFile("""
            { "DingTalk": { "url": "https://example.com", "body": { "content": "{{Text}}" } } }
            """);

        var result = CreateSut().Load(_configDir);

        result.Providers.Should().ContainKey("dingtalk");
    }

    // ---------- 校验与降级 ----------

    [Fact]
    public void Load_NonHttpsUrl_SkipsWithWarning()
    {
        WriteFile("""
            {
              "bad": { "url": "http://example.com", "body": { "content": "{{Text}}" } },
              "good": { "url": "https://example.com", "body": { "content": "{{Text}}" } }
            }
            """);

        var result = CreateSut().Load(_configDir);

        result.Providers.Should().ContainKey("good");
        result.Providers.Should().NotContainKey("bad");
        result.Errors.Should().ContainSingle(e => e.Contains("https"));
    }

    [Fact]
    public void Load_UnknownPreset_SkipsWithWarning()
    {
        WriteFile("""
            {
              "mystery": {
                "url": "https://example.com",
                "body": { "content": "{{Text}}" },
                "signing": { "preset": "corp_custom" }
              }
            }
            """);

        var result = CreateSut().Load(_configDir);

        result.Providers.Should().BeNull();
        result.Errors.Should().ContainSingle(e => e.Contains("corp_custom"));
    }

    [Fact]
    public void Load_MissingBody_SkipsWithWarning()
    {
        WriteFile("""{ "nobody": { "url": "https://example.com" } }""");

        var result = CreateSut().Load(_configDir);

        result.Providers.Should().BeNull();
        result.Errors.Should().ContainSingle(e => e.Contains("body"));
    }

    [Fact]
    public void Load_BrokenJson_FailsWithLineNumber()
    {
        WriteFile("{ \"dingtalk\": ");

        var result = CreateSut().Load(_configDir);

        result.Status.Should().Be(HookFileLoadStatus.Failed);
        result.Errors.Should().ContainSingle();
        result.Errors[0].Should().Contain("行");
    }

    [Fact]
    public void Load_EnumStrings_AreReadCaseInsensitively()
    {
        WriteFile("""
            {
              "x": {
                "url": "https://example.com",
                "body": { "content": "{{Text}}" },
                "signing": {
                  "keyTemplate": "{Secret}",
                  "messageTemplate": "{Timestamp}",
                  "encoding": "hex",
                  "placement": "header"
                }
              }
            }
            """);

        var def = CreateSut().Load(_configDir).Providers!["x"];

        def.Signing!.Encoding.Should().Be(ProviderSignatureEncoding.Hex);
        def.Signing.Placement.Should().Be(ProviderSignaturePlacement.Header);
    }

    // ---------- Bootstrap 三层合并（内置 < 用户 < 项目，整条替换） ----------

    [Fact]
    public void Bootstrap_ThreeLayers_DefinitionWholeReplacedHigherLayerWins()
    {
        var builtinDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"onecode-npdl-b{Guid.NewGuid():N}")).FullName;
        var userDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"onecode-npdl-u{Guid.NewGuid():N}")).FullName;
        var projectDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"onecode-npdl-p{Guid.NewGuid():N}")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(builtinDir, NotificationProviderDefinitionLoader.FileName), """
                {
                  "a": { "url": "https://a.builtin", "body": { "c": "{{Text}}" } },
                  "b": { "url": "https://b.builtin", "body": { "c": "{{Text}}" } }
                }
                """);
            File.WriteAllText(Path.Combine(userDir, NotificationProviderDefinitionLoader.FileName), """
                { "b": { "url": "https://b.user", "body": { "c": "{{Text}}" } } }
                """);
            File.WriteAllText(Path.Combine(projectDir, NotificationProviderDefinitionLoader.FileName), """
                { "c": { "url": "https://c.project", "body": { "c": "{{Text}}" } } }
                """);

            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
            var registry = new NotificationProviderRegistry(
                [], factory, NullLoggerFactory.Instance);
            var bootstrapper = new HookConfigBootstrapper(
                new HookSettingsLoader(NullLogger<HookSettingsLoader>.Instance),
                new HookRegistry(new GlobHookMatcher()),
                new NotificationProviderDefinitionLoader(NullLogger<NotificationProviderDefinitionLoader>.Instance),
                registry,
                new HookLoadDiagnostics(),
                NullLogger<HookConfigBootstrapper>.Instance);

            bootstrapper.Bootstrap(userDir, projectDir, builtinDir);

            // 内置层 a 保留；用户层覆盖内置层 b；项目层 c 新增
            registry.Resolve("a").Should().BeAssignableTo<DeclarativeNotificationProvider>();
            registry.Resolve("b").Should().BeAssignableTo<DeclarativeNotificationProvider>();
            registry.Resolve("c").Should().BeAssignableTo<DeclarativeNotificationProvider>();
            registry.Names.Should().BeEquivalentTo(["a", "b", "c"]);
        }
        finally
        {
            foreach (var dir in new[] { builtinDir, userDir, projectDir })
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
