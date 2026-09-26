using OneCode.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneCode.App.Commands;
using OneCode.App.Logging;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Query;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.AutoDream;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Coordinator;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Setup;
using OneCode.App.Services.Context;
using OneCode.App.Services.Cron;
using OneCode.App.Services.Hooks;
using OneCode.App.Services.Lsp;
using OneCode.App.Services.Loop;
using OneCode.App.Services.Memory;
using OneCode.App.Services.Mcp;
using OneCode.App.Services.Observability;
using OneCode.App.Services.Permissions;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Search;
using OneCode.App.Services.Skills;
using OneCode.App.Services.Tasks;
using OneCode.App.Session;
using OneCode.App.Tui;
using OneCode.App.Tools;
using OneCode.Core.Models;

namespace OneCode.App;

public sealed class OneCodeApp(
    IHost host,
    InteractiveModeExecutor executor,
    IConfigManager configManager,
    ILogger<OneCodeApp> logger,
    AppStartupService startup) : IAsyncDisposable
{
    private readonly IHost _host = host;
    private readonly InteractiveModeExecutor _executor = executor;
    private readonly IConfigManager _configManager = configManager;
    private readonly ILogger<OneCodeApp> _logger = logger;
    private readonly AppStartupService _startup = startup;

    public static OneCodeApp Create(string[] args)
    {
        var startupTimer = new StartupTimer();
        startupTimer.Mark("entry");

        var workingDir = Environment.CurrentDirectory;

        var builder = Host.CreateApplicationBuilder(args);
        startupTimer.Mark("builder-created");

        var debugConfig = GetDebugConfig();

        builder.Services
            .AddSkillServices(workingDir)
            .AddChatClientServices()
            .AddSessionServices(workingDir)
            .AddTaskServices()
            .AddPlanModeServices()
            .AddContextServices()
            .AddSearchServices()
            .AddMcpServices()
            .AddTokenObservabilityServices()
            .AddHookServices()
            .AddPermissionServices()
            .AddCommandSourceServices()
            .AddCronSchedulingServices()
            .AddModelCatalogServices()
            .AddBuildModeServices()
            .AddPlanWorkflowServices()
            .AddChatQueryServices()
            .AddSetupServices()
            .AddAgentRuntimeServices()
            .AddGoalServices()
            .AddLoopServices()
            .AddToolServices()
            .AddMemoryServices()
            .AddCompactServices()
            .AddPromptServices(workingDir)
            .AddLspServices()
            .AddNamedHttpClients()
            .AddTeamServices()
            .AddAutoDreamServices()
            .AddPlatformServices()
            .AddCommands()
            .AddInteractiveServices();

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
