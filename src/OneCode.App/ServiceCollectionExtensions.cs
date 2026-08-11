using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OneCode.App.Logging;
using OneCode.App.Services;
using OneCode.App.Session;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using InfraConstants = OneCode.Infrastructure.Config.Constants;

namespace OneCode.App;

public static partial class ServiceCollectionExtensions
{
    /// <summary>Creates an HttpClientHandler pre-configured with proxy and mTLS.</summary>
    private static HttpClientHandler CreateProxyAwareHandler()
    {
        var handler = new HttpClientHandler();
        ProxyConfigService.ApplyToHandler(handler);
        return handler;
    }

    public static IServiceCollection RegisterCoreServices(this IServiceCollection services,
        string workingDir)
    {
        services.Configure<SessionOptions>(o => o.InitialWorkingDirectory = workingDir);

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<LocalAgentFileStore>();
        services.AddSingleton<IFileSystem>(sp => sp.GetRequiredService<LocalAgentFileStore>());
        services.AddSingleton<AgentFileStore>(sp => sp.GetRequiredService<LocalAgentFileStore>());
        services.AddSingleton<OneCode.Core.IO.IClipboardService, ClipboardService>();
        services.AddSingleton<OneCode.Core.ITokenEstimator, OneCode.Infrastructure.TokenEstimator>();

        services.AddSingleton<ISessionStore, SessionStore>();
        // SessionManager is registered in RegisterToolServices after SessionToolSetManager
        // so ISessionToolSetManager can be required (no optional GetService).
        services.AddSingleton<SessionIdHolder>();
        services.AddSingleton<Core.Domain.ISessionIdProvider>(sp => sp.GetRequiredService<SessionIdHolder>());
        services.AddSingleton<IWorkingDirectoryAccessor, SessionWorkingDirectoryAccessor>();

        return services;
    }

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

    public static IServiceCollection RegisterPromptManagement(
        this IServiceCollection services,
        string workingDir)
    {
        services.AddSingleton<Core.Prompt.PromptManager>(sp =>
        {
            var manager = new Core.Prompt.PromptManager(
                sp.GetService<ILogger<Core.Prompt.PromptManager>>());

            var projectPromptsDir = Path.Combine(workingDir, InfraConstants.App.ConfigDirName, InfraConstants.Subdirs.Prompts);
            if (Directory.Exists(projectPromptsDir))
                manager.AddStore(new Infrastructure.Prompt.FilePromptStore(projectPromptsDir));

            var userPromptsDir = Path.Combine(PathsHelper.GetUserConfigDir(), InfraConstants.Subdirs.Prompts);
            if (Directory.Exists(userPromptsDir))
                manager.AddStore(new Infrastructure.Prompt.FilePromptStore(userPromptsDir));

            var defaultPromptsDir = Path.Combine(AppContext.BaseDirectory, InfraConstants.Subdirs.Prompts);
            manager.AddStore(new Infrastructure.Prompt.FilePromptStore(defaultPromptsDir));

            return manager;
        });
        services.AddSingleton<Core.Prompt.IPromptManager>(sp => sp.GetRequiredService<Core.Prompt.PromptManager>());
        services.AddSingleton<PromptComposer>();

        return services;
    }
}
