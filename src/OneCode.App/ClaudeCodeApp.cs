using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneCode.App.Commands;
using OneCode.App.Logging;
using OneCode.App.Services;
using OneCode.Core.Models;
using OneCode.Infrastructure.Config;

namespace OneCode.App;

public sealed class OneCodeApp : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly InteractiveModeExecutor _executor;
    private readonly IConfigManager _configManager;
    private readonly ILogger<OneCodeApp> _logger;
    private readonly AppStartupService _startup;

    private OneCodeApp(
        IHost host,
        InteractiveModeExecutor executor,
        IConfigManager configManager,
        ILogger<OneCodeApp> logger,
        AppStartupService startup)
    {
        _host = host;
        _executor = executor;
        _configManager = configManager;
        _logger = logger;
        _startup = startup;
    }

    public static OneCodeApp Create(string[] args)
    {
        var startupTimer = new StartupTimer();
        startupTimer.Mark("entry");

        var workingDir = Environment.CurrentDirectory;

        var builder = Host.CreateApplicationBuilder(args);
        startupTimer.Mark("builder-created");

        var debugConfig = GetDebugConfig();

        builder.Services
            .RegisterCoreServices(workingDir)
            .RegisterSkillServices(workingDir)
            .RegisterChatClient()
            .RegisterBusinessServices()
            .RegisterToolServices()
            .RegisterMemoryServices()
            .RegisterPromptManagement(workingDir)
            .RegisterLspAndMcpServices()
            .RegisterAdvancedServices(builder.Configuration)
            .AddCommands()
            .RegisterInteractiveServices();

        builder.ConfigureApplicationLogging(debugConfig);
        startupTimer.Mark("services-registered");

        var host = builder.Build();
        startupTimer.Mark("host-built");

        _ = host.Services.GetRequiredService<IModelCatalogCache>();
        startupTimer.Mark("model-catalog-loaded");

        var logger = host.Services.GetRequiredService<ILogger<OneCodeApp>>();
        logger.LogDebug("Startup timing:\n{Summary}", startupTimer.FormatSummary());
        WriteDebugLogHint(debugConfig);

        return new OneCodeApp(
            host,
            host.Services.GetRequiredService<InteractiveModeExecutor>(),
            host.Services.GetRequiredService<IConfigManager>(),
            logger,
            host.Services.GetRequiredService<AppStartupService>());
    }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        try
        {
            AppStartupService.LogStartupBanner(_logger);

            return await _executor.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 99;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Application error");
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Starts hosted services then runs one-time warm-up. Composition root owns
    /// <see cref="IHost.StartAsync"/>; warm-up deps are constructor-injected into
    /// <see cref="AppStartupService"/>.
    /// </summary>
    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        await _host.StartAsync(ct).ConfigureAwait(false);
        await _startup.WarmUpAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }

    private static void WriteDebugLogHint(DebugLogConfig debugConfig)
    {
        if (debugConfig.Enabled)
            Console.Error.WriteLine($"Log file: {debugConfig.GetLogFilePath()}");
    }

    private static DebugLogConfig GetDebugConfig()
    {
        var levelEnv = Environment.GetEnvironmentVariable(OneCode.Core.Constants.EnvVars.LogLevel);
        return DebugLogConfig.Resolve(DebugLogConfig.DebugBuild, levelEnv);
    }
}
