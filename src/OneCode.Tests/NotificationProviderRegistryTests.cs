using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Hooks.Notifications;


using OneCode.App.Services.Hooks;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.Tests;

/// <summary>
/// NotificationProviderRegistry 单测——声明式定义优先、编译型兜底、同名声明式胜出、
/// 实例缓存与 SetDefinitions 缓存失效。
/// </summary>
public sealed class NotificationProviderRegistryTests
{
    [Fact]
    public void Resolve_DeclarativeName_ReturnsEngineInstance()
    {
        var registry = CreateRegistry([], definitions: new Dictionary<string, NotificationProviderDefinition>
        {
            ["dingtalk"] = CreateDefinition("https://example.com"),
        });

        registry.Resolve("dingtalk").Should().BeAssignableTo<DeclarativeNotificationProvider>();
    }

    [Fact]
    public void Resolve_CompiledName_FallsBackToCompiledProvider()
    {
        var compiled = CreateCompiledProvider("compiled1");
        var registry = CreateRegistry([compiled]);

        registry.Resolve("compiled1").Should().BeSameAs(compiled);
    }

    [Fact]
    public void Resolve_SameName_DeclarativeShadowsCompiled()
    {
        var compiled = CreateCompiledProvider("dual");
        var registry = CreateRegistry(
            [compiled],
            definitions: new Dictionary<string, NotificationProviderDefinition> { ["dual"] = CreateDefinition("https://example.com") });

        registry.Resolve("dual").Should().BeAssignableTo<DeclarativeNotificationProvider>();
    }

    [Fact]
    public void Resolve_UnknownName_ReturnsNull()
    {
        CreateRegistry([]).Resolve("ghost").Should().BeNull();
    }

    [Fact]
    public void Resolve_DeclarativeInstance_IsCached()
    {
        var registry = CreateRegistry([], definitions: new Dictionary<string, NotificationProviderDefinition>
        {
            ["x"] = CreateDefinition("https://example.com"),
        });

        registry.Resolve("x").Should().BeSameAs(registry.Resolve("X"), "实例缓存且名称大小写不敏感");
    }

    [Fact]
    public void SetDefinitions_ClearsInstanceCache_AndRestoresCompiledFallback()
    {
        var compiled = CreateCompiledProvider("dual");
        var registry = CreateRegistry(
            [compiled],
            definitions: new Dictionary<string, NotificationProviderDefinition> { ["dual"] = CreateDefinition("https://example.com") });
        registry.Resolve("dual").Should().BeAssignableTo<DeclarativeNotificationProvider>();

        registry.SetDefinitions(new Dictionary<string, NotificationProviderDefinition>());

        registry.Resolve("dual").Should().BeSameAs(compiled, "定义移除后应回落到编译型 Provider");
    }

    [Fact]
    public void Names_UnionsDeclarativeAndCompiled()
    {
        var registry = CreateRegistry(
            [CreateCompiledProvider("compiled1"), CreateCompiledProvider("shared")],
            definitions: new Dictionary<string, NotificationProviderDefinition> { ["shared"] = CreateDefinition("https://example.com") });

        registry.Names.Should().BeEquivalentTo(["shared", "compiled1"]);
    }

    // ---------- Helpers ----------

    private static NotificationProviderRegistry CreateRegistry(
        IEnumerable<INotificationProvider> compiled,
        Dictionary<string, NotificationProviderDefinition>? definitions = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var registry = new NotificationProviderRegistry(compiled, factory, NullLoggerFactory.Instance);
        if (definitions is not null)
            registry.SetDefinitions(definitions);
        return registry;
    }

    private static INotificationProvider CreateCompiledProvider(string name)
    {
        var provider = Substitute.For<INotificationProvider>();
        provider.Name.Returns(name);
        return provider;
    }

    private static NotificationProviderDefinition CreateDefinition(string url) => new()
    {
        Url = url,
        Body = JsonBody("""{"content":"{{Text}}"}"""),
    };

    private static System.Text.Json.JsonElement JsonBody(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
