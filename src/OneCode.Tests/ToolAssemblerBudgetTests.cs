// ToolAssembler 的预算解析：maxBudgetTokens 的 0 = 不设限语义（null 归一化）。
using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.App.Query;
using OneCode.Core.Config;
using OneCode.Core.Tools;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

public sealed class ToolAssemblerBudgetTests
{
    private static ToolAssembler CreateAssembler(AppSettings settings)
        => new(
            Substitute.For<IToolCatalog>(),
            TestConfigManager.Create(settings),
            Substitute.For<ISessionToolSetManager>(),
            Substitute.For<IToolCapabilityResolver>(),
            Substitute.For<ILogger>());

    [Fact]
    public void ResolveMaxBudgetTokens_PositiveValue_ReturnsConfiguredLimit()
    {
        var sut = CreateAssembler(new AppSettings { MaxBudgetTokens = 5_000_000 });

        sut.ResolveMaxBudgetTokens().Should().Be(5_000_000);
    }

    [Fact]
    public void ResolveMaxBudgetTokens_ZeroMeansUnlimited_ReturnsNull()
    {
        var sut = CreateAssembler(new AppSettings { MaxBudgetTokens = 0 });

        sut.ResolveMaxBudgetTokens().Should().BeNull("maxBudgetTokens: 0 explicitly means unlimited");
    }

    [Fact]
    public void ResolveMaxBudgetTokens_NegativeValue_TreatedAsUnlimited()
    {
        var sut = CreateAssembler(new AppSettings { MaxBudgetTokens = -1 });

        sut.ResolveMaxBudgetTokens().Should().BeNull();
    }
}
