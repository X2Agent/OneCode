using NSubstitute;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests.TestSupport;

/// <summary>
/// Test helpers for creating pre-configured <see cref="IConfigManager"/> substitutes.
/// </summary>
public static class TestConfigManager
{
    /// <summary>
    /// Creates an <see cref="IConfigManager"/> substitute with a real immutable snapshot
    /// so every consumer reads the same <see cref="IConfigManager.Current"/> entry point.
    /// </summary>
    public static IConfigManager Create(AppSettings? settings = null)
    {
        var cm = Substitute.For<IConfigManager>();
        cm.Current.Returns(ConfigSnapshot.FromEffective(settings ?? new AppSettings()));
        return cm;
    }
}
