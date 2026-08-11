using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
namespace OneCode.App.Logging;

public static class DebugLoggingExtensions
{
    public static ILoggingBuilder AddDebugMode(
        this ILoggingBuilder builder,
        DebugLogConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.Enabled)
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("System", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);
            return builder;
        }

        builder.SetMinimumLevel(config.MinimumLevel);
        builder.ClearProviders();

        if (config.OutputToConsole)
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = false;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                options.IncludeScopes = true;
                options.UseUtcTimestamp = false;
            });
        }

        if (config.OutputToFile)
        {
            builder.Services.AddSingleton(Options.Create(config));
            builder.Services.AddSingleton<ILoggerProvider, DebugFileLoggerProvider>();
        }

        builder.AddFilter("System", config.MinimumLevel);
        builder.AddFilter("Microsoft", config.MinimumLevel);

        return builder;
    }
}
