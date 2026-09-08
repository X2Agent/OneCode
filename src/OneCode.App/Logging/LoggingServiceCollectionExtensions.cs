using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OneCode.App.Logging;

/// <summary>
/// 日志引导 DI 注册——组合根专用的宿主日志配置（控制台/调试文件双通道）。
/// 由组合根 <see cref="OneCodeApp"/> 显式调用。
/// </summary>
public static class LoggingServiceCollectionExtensions
{
    public static IHostApplicationBuilder ConfigureApplicationLogging(
        this IHostApplicationBuilder builder,
        DebugLogConfig? debugConfig = null)
    {
        if (debugConfig is { Enabled: true })
        {
            builder.Logging.AddDebugMode(debugConfig);
        }
        else
        {
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.Logging.SetMinimumLevel(LogLevel.Information);

            var defaultConfig = new DebugLogConfig
            {
                Enabled = false,
                MinimumLevel = LogLevel.Debug,
                OutputToConsole = false,
                OutputToFile = true,
            };
            builder.Logging.Services.AddSingleton(Options.Create(defaultConfig));
            builder.Logging.Services.AddSingleton<ILoggerProvider, DebugFileLoggerProvider>();
            builder.Logging.AddFilter("System", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        }

        return builder;
    }
}
