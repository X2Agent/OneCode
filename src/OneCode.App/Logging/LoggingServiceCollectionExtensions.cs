using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OneCode.App.Logging;

/// <summary>
/// 日志引导 DI 注册——组合根专用的宿主日志配置（控制台 + 调试文件双通道）。
/// 最低级别与控制台样式由构建模式决定，见 <see cref="DebugFileLoggerProvider.MinimumLevel"/>。
/// 由组合根 <see cref="OneCodeApp"/> 显式调用。
/// </summary>
public static class LoggingServiceCollectionExtensions
{
    public static IHostApplicationBuilder ConfigureApplicationLogging(this IHostApplicationBuilder builder)
    {
        // 必须先清空宿主默认 provider，否则与下面的 SimpleConsole 叠加会让同一条日志重复打印。
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(DebugFileLoggerProvider.MinimumLevel);

        var debugBuild = DebugFileLoggerProvider.IsDebugBuild;
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = !debugBuild;
            options.TimestampFormat = debugBuild ? "yyyy-MM-dd HH:mm:ss.fff " : "HH:mm:ss ";
            options.IncludeScopes = debugBuild;
            options.UseUtcTimestamp = false;
        });

        builder.Logging.Services.AddSingleton<ILoggerProvider, DebugFileLoggerProvider>();

        builder.Logging.AddFilter("System", DebugFileLoggerProvider.MinimumLevel);
        builder.Logging.AddFilter("Microsoft", DebugFileLoggerProvider.MinimumLevel);

        return builder;
    }
}
