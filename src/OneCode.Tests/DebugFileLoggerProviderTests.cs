using OneCode.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneCode.App.Logging;

namespace OneCode.Tests;

/// <summary>
/// 锁定日志级别的唯一来源：构建模式。
/// Debug 构建记录 Debug 及以上，Release 构建记录 Warning 及以上，不存在运行时覆盖开关。
/// </summary>
public sealed class DebugFileLoggerProviderTests
{
    [Fact]
    public void MinimumLevel_FollowsCurrentBuildMode()
    {
        var expected =
#if DEBUG
            LogLevel.Debug;
#else
            LogLevel.Warning;
#endif
        DebugFileLoggerProvider.MinimumLevel.Should().Be(expected);
    }

    [Fact]
    public void GetLogFilePath_PointsToDebugDirectoryWithLogExtension()
    {
        var path = DebugFileLoggerProvider.GetLogFilePath();

        Path.GetFileName(Path.GetDirectoryName(path)).Should().Be("debug");
        Path.GetExtension(path).Should().Be(".log");
    }

    [Fact]
    public void ConfigureApplicationLogging_RegistersExactlyOneConsoleProvider()
    {
        var builder = Host.CreateApplicationBuilder([]);

        builder.ConfigureApplicationLogging();

        using var host = builder.Build();
        var providers = host.Services.GetServices<ILoggerProvider>().ToList();

        providers.Should().ContainSingle(provider => provider is DebugFileLoggerProvider);
        providers.Where(provider => provider is not DebugFileLoggerProvider).Should().ContainSingle(
            "宿主默认 provider 必须被 ClearProviders 清掉，否则同一条日志会被两个 console provider 重复打印");
    }
}
