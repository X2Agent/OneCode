using OneCode.App.Services;

using OneCode.App.Services.Compact;

using OneCode.Core.Domain;

using OneCode.Core.Models;

using OneCode.Infrastructure.Config;

using OneCode.Tests.TestSupport;



namespace OneCode.Tests;



public sealed class TokenBudgetContextWindowTests

{

    static TokenBudgetContextWindowTests()

    {

        ModelCatalogTestHelper.Initialize();

    }



    private static IModelCatalog Catalog => ModelCatalogTestHelper.Store;



    [Fact]

    public void GetMaxContextTokens_WithRegistry_ReturnsCatalogValue()

    {

        var registry = CreateRegistry("deepseek/deepseek-v4-pro");

        TokenBudget.GetMaxContextTokens("deepseek/deepseek-v4-pro", registry)

            .Should().BeGreaterThan(0);

    }



    [Fact]

    public void GetMaxContextTokens_WithRegistryUnregisteredModel_FallsBackToSnapshot()

    {

        TokenBudget.GetMaxContextTokens("zhipuai/glm-4.6", modelManager: null, catalog: Catalog)

            .Should().Be(204_800);

    }



    [Fact]

    public void GetMaxContextTokens_WithoutRegistry_ReturnsFromSnapshot()

    {

        TokenBudget.GetMaxContextTokens("anthropic/claude-sonnet-4-5", modelManager: null, catalog: Catalog)

            .Should().Be(1_000_000);

    }



    [Fact]

    public void GetMaxContextTokens_DeepSeekV4ProWithBracket1M_FallbackPrefix()

    {

        TokenBudget.GetMaxContextTokens("deepseek/deepseek-v4-pro[1m]", modelManager: null, catalog: Catalog)

            .Should().Be(1_000_000);

    }



    [Fact]

    public void GetMaxContextTokens_UnknownModel_ReturnsDefault()

    {

        TokenBudget.GetMaxContextTokens("unknown/random-model", modelManager: null, catalog: Catalog)

            .Should().Be(128_000);

    }



    [Fact]

    public void GetMaxContextTokens_ConfigurationFromCatalog()

    {

        var registry = CreateRegistry("anthropic/claude-sonnet-4-5");

        TokenBudget.GetMaxContextTokens("anthropic/claude-sonnet-4-5", registry)

            .Should().BeGreaterThan(0);

    }



    [Fact]

    public void Estimate_WithRegistry_UsesCatalogContextWindow()

    {

        var registry = CreateRegistry("deepseek/deepseek-v3.2");

        var session = new Conversation { Model = "deepseek/deepseek-v3.2" };

        session.Messages.Add(new UserMessage("1", "Hello", DateTimeOffset.UtcNow));



        var status = TokenBudget.Estimate(session, TestTokenEstimators.Default, modelManager: registry);

        status.MaxInputTokens.Should().BeGreaterThan(0);

    }



    [Fact]

    public void Estimate_WithoutRegistry_UsesSnapshot()

    {

        var session = new Conversation { Model = "zhipuai/glm-5" };

        session.Messages.Add(new UserMessage("1", "Hello", DateTimeOffset.UtcNow));



        var status = TokenBudget.Estimate(session, TestTokenEstimators.Default, modelManager: null, catalog: Catalog);

        status.MaxInputTokens.Should().Be(204_800 - 8192);

    }



    private static ModelManager CreateRegistry(string modelId)

    {

        var configManager = TestConfigManager.Create(new AppSettings { Model = modelId });

        return new ModelManager(configManager, Catalog);

    }

}


