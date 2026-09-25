using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Compact;
using OneCode.Core.Config;
using OneCode.Core.Models;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// 覆盖 <see cref="ModelManager"/> 的窗口/provider 解析：
/// 本地 Ollama 的窗口真相源是下发的 num_ctx（<c>ollamaContextWindow</c>），
/// 云端（含 OpenRouter 上的小模型）继续以 models.dev catalog 为准——两条路径互不泄漏。
/// </summary>
public sealed class ModelManagerContextWindowTests
{
    static ModelManagerContextWindowTests()
    {
        ModelCatalogTestHelper.Initialize();
    }

    private static IModelCatalog Catalog => ModelCatalogTestHelper.Store;

    private static ModelManager CreateLocalOllama(string model, int contextWindow)
        => new(
            TestConfigManager.Create(new AppSettings
            {
                Provider = "ollama",
                Model = model,
                OllamaContextWindow = contextWindow,
            }),
            Catalog);

    [Fact]
    public void Resolve_LocalOllama_UsesConfiguredContextWindow()
    {
        // 反证：修复前 AddModel 硬编码 ContextWindow: 0，且 catalog 不覆盖本地模型，
        // 该值会原样透出 0，压缩装配按 0 走严格校验直接抛错。
        var manager = CreateLocalOllama("qwen2.5-coder:7b", 32_768);

        manager.Resolve("qwen2.5-coder:7b")!.ContextWindow.Should().Be(32_768);
    }

    [Fact]
    public void Resolve_LocalOllama_UnsetContextWindow_FallsBackToDefault()
    {
        var manager = CreateLocalOllama("qwen2.5-coder:7b", 0);

        manager.Resolve("qwen2.5-coder:7b")!.ContextWindow
            .Should().Be(ModelContextDefaults.DefaultContextWindow);
    }

    [Fact]
    public void Resolve_LocalOllama_IgnoresCatalogHit()
    {
        // catalog 对本地模型没有权威性：其窗口由我们下发的 num_ctx 强制，
        // 即使 catalog 恰好命中同名字符串也必须以 ollamaContextWindow 为准。
        var fakeCatalog = Substitute.For<IModelCatalog>();
        fakeCatalog.GetContextWindow(Arg.Any<string>()).Returns(131_072);

        var manager = new ModelManager(
            TestConfigManager.Create(new AppSettings
            {
                Provider = "ollama",
                Model = "qwen3:8b",
                OllamaContextWindow = 32_768,
            }),
            fakeCatalog);

        manager.Resolve("qwen3:8b")!.ContextWindow.Should().Be(32_768);
        fakeCatalog.DidNotReceive().GetContextWindow(Arg.Any<string>());
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("openrouter")]
    [InlineData("anthropic")]
    [InlineData(null)]
    public void Resolve_NonLocalProvider_NeverReadsOllamaContextWindow(string? provider)
    {
        // 反证：ollamaContextWindow 对任何非 ollama provider 都必须无效——
        // OpenRouter 上的小模型同样走 catalog/默认值，不得被本地旋钮影响。
        var manager = new ModelManager(
            TestConfigManager.Create(new AppSettings
            {
                Provider = provider,
                Model = "unknown/random-model",
                OllamaContextWindow = 999_999,
            }),
            Catalog);

        manager.Resolve("unknown/random-model")!.ContextWindow
            .Should().Be(ModelContextDefaults.DefaultContextWindow);
    }

    [Fact]
    public void Resolve_CloudCatalogHit_ReturnsCatalogWindow()
    {
        var manager = new ModelManager(
            TestConfigManager.Create(new AppSettings { Provider = "openai", Model = "zhipuai/glm-4.6" }),
            Catalog);

        manager.Resolve("zhipuai/glm-4.6")!.ContextWindow.Should().Be(204_800);
    }

    [Fact]
    public void Resolve_CloudCatalogMiss_FallsBackToDefaultWindow()
    {
        // 修复前此分支返回 0（catalog 未命中即原样返回），与 TokenBudget 的 128_000 回落口径矛盾。
        var manager = new ModelManager(
            TestConfigManager.Create(new AppSettings { Provider = "openai", Model = "unknown/random-model" }),
            Catalog);

        manager.Resolve("unknown/random-model")!.ContextWindow
            .Should().Be(ModelContextDefaults.DefaultContextWindow);
    }

    [Fact]
    public void Resolve_LocalOllama_KeepsOllamaProviderId()
    {
        // 反证：修复前 ollama 被折叠成 "openai"，工具结果按 JSON 序列化，
        // 与本地模型应返回 Markdown 的设计相反。
        var manager = CreateLocalOllama("qwen2.5-coder:7b", 32_768);

        manager.Resolve("qwen2.5-coder:7b")!.ProviderId.Should().Be("ollama");
    }

    [Theory]
    [InlineData(null, "anthropic")]
    [InlineData("", "anthropic")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("Anthropic", "anthropic")]
    [InlineData("openai", "openai")]
    [InlineData("openrouter", "openai")]
    [InlineData("OLLAMA", "ollama")]
    public void Resolve_ProviderId_MappingUnchangedExceptOllama(string? provider, string expected)
    {
        var manager = new ModelManager(
            TestConfigManager.Create(new AppSettings { Provider = provider, Model = "some-model" }),
            Catalog);

        manager.Resolve("some-model")!.ProviderId.Should().Be(expected);
    }

    [Fact]
    public void GetMainModel_RuntimeRegisteredModel_LocalOllamaWindowApplies()
    {
        // 运行时新模型（/config 切换）经 EnsureModelRegistered 注册，必须与启动路径同口径。
        var manager = CreateLocalOllama("qwen2.5-coder:7b", 32_768);

        var model = manager.GetMainModel("qwen3:8b");

        model.Id.Should().Be("qwen3:8b");
        model.ProviderId.Should().Be("ollama");
        model.ContextWindow.Should().Be(32_768);
    }

    [Fact]
    public void TokenBudget_LocalOllama_UsesOllamaWindowAndQuarterReservation()
    {
        // 端到端口径：窗口 32_768，本地预留收敛为 8_192（窗口/4 与固定 8_192 取小），
        // /status 与实际压缩预算不再互相矛盾。
        var manager = CreateLocalOllama("qwen2.5-coder:7b", 32_768);

        TokenBudget.GetMaxContextTokens("qwen2.5-coder:7b", manager).Should().Be(32_768);
    }

    [Fact]
    public void TokenBudget_LocalOllamaSmallWindow_ReservationFollowsWindow()
    {
        var manager = CreateLocalOllama("qwen2.5-coder:7b", 4_096);
        var session = new OneCode.Core.Domain.Conversation { Model = "qwen2.5-coder:7b" };

        var status = TokenBudget.Estimate(session, TestTokenEstimators.Default, modelManager: manager);

        // 4_096 / 4 = 1_024 预留，而非固定 8_192（后者会把输入预算压到 1）。
        status.MaxInputTokens.Should().Be(4_096 - 1_024);
    }
}
