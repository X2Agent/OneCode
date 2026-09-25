using OneCode.App.Services;
using OneCode.App.Services.Compact;
using OneCode.Core.Config;
using OneCode.Core.Domain;
using OneCode.Core.Models;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

public sealed class TokenBudgetContextWindowTests
{
    static TokenBudgetContextWindowTests()
    {
        ModelCatalogTestHelper.Initialize();
    }

    private static IModelCatalog Catalog => ModelCatalogTestHelper.Store;

    /// <summary>
    /// 无 ModelManager 时走 <see cref="ModelContextDefaults.Resolve"/>：
    /// catalog 精确命中返回其值，未命中回退 128_000 默认值。
    /// </summary>
    [Theory]
    [InlineData("zhipuai/glm-4.6", 204_800)]
    [InlineData("anthropic/claude-sonnet-4-5", 1_000_000)]
    [InlineData("deepseek/deepseek-v4-pro[1m]", 1_000_000)] // bracket 后缀需被剥离后再查 catalog
    [InlineData("unknown/random-model", 128_000)]
    public void GetMaxContextTokens_WithoutRegistry_FallsBackToCatalogSnapshot(string modelId, int expected)
    {
        TokenBudget.GetMaxContextTokens(modelId, modelManager: null, catalog: Catalog).Should().Be(expected);
    }

    /// <summary>有 ModelManager 时优先使用其解析结果（其 ContextWindow 实时委托 catalog）。</summary>
    [Theory]
    [InlineData("deepseek/deepseek-v4-pro", 1_000_000)]
    [InlineData("anthropic/claude-sonnet-4-5", 1_000_000)]
    public void GetMaxContextTokens_WithRegistry_ReturnsCatalogValue(string modelId, int expected)
    {
        var registry = CreateRegistry(modelId);

        TokenBudget.GetMaxContextTokens(modelId, registry).Should().Be(expected);
    }

    [Fact]
    public void Estimate_WithRegistry_UsesCatalogContextWindow()
    {
        var registry = CreateRegistry("deepseek/deepseek-v4-pro");
        var session = new Conversation { Model = "deepseek/deepseek-v4-pro" };
        session.Messages.Add(new UserMessage("1", "Hello", DateTimeOffset.UtcNow));

        var status = TokenBudget.Estimate(session, TestTokenEstimators.Default, modelManager: registry);

        // 1_000_000 context window minus the 8_192 reserved output tokens.
        status.MaxInputTokens.Should().Be(1_000_000 - 8_192);
        // "Hello" costs ceil(5 / 4) = 2 tokens plus the 12-token message overhead.
        status.EstimatedInputTokens.Should().Be(14);
    }

    [Fact]
    public void Estimate_WithoutRegistry_UsesSnapshot()
    {
        var session = new Conversation { Model = "zhipuai/glm-5" };
        session.Messages.Add(new UserMessage("1", "Hello", DateTimeOffset.UtcNow));

        var status = TokenBudget.Estimate(session, TestTokenEstimators.Default, modelManager: null, catalog: Catalog);

        status.MaxInputTokens.Should().Be(204_800 - 8_192);
        status.EstimatedInputTokens.Should().Be(14);
    }

    private static ModelManager CreateRegistry(string modelId)
    {
        var configManager = TestConfigManager.Create(new AppSettings { Model = modelId });

        return new ModelManager(configManager, Catalog);
    }
}
