using OneCode.Infrastructure;
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
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Setup;
using OneCode.App.Services.Streaming;
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
using OneCode.App.Services.Interaction;
using OneCode.App.Services.Skills;
using OneCode.App.Services.Tasks;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Models;

namespace OneCode.App;

public sealed class OneCodeApp(
    IHost host,
    IConfigManager configManager,
    ILogger<OneCodeApp> logger,
    AppStartupService startup) : IAsyncDisposable
{
    private readonly IHost _host = host;
    private readonly IConfigManager _configManager = configManager;
    private readonly ILogger<OneCodeApp> _logger = logger;
    private readonly AppStartupService _startup = startup;

    /// <summary>
    /// 注册 OneCode 核心服务图（UI 无关，不含宿主构建与日志配置）。
    /// 由 TUI、WebUI 与无头宿主共用；<paramref name="configureFrontend"/> 在前端专属注册之前调用，
    /// 以保证核心服务先就绪。
    /// </summary>
    /// <param name="services">目标服务集合。</param>
    /// <param name="workingDir">工作目录（由宿主显式传入，不读进程当前目录）。</param>
    /// <param name="configureFrontend">前端专属注册（TUI 栈 / WebUI 端口实现），可为 null。</param>
    public static void AddOneCodeCore(
        IServiceCollection services,
        string workingDir,
        Action<IServiceCollection>? configureFrontend = null)
    {
        services
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
            .AddCronSchedulingServices(workingDir)
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
            .AddAutoDreamServices()
            .AddPlatformServices()
            .AddCommands()
            .AddStreamingServices()
            .AddAppRuntimeServices()
            .AddInteractionCoreServices();

        configureFrontend?.Invoke(services);
    }

    /// <summary>
    /// 构建组合根。<paramref name="configureTuiStack"/> 由前端进程（OneCode.Tui）注入
    /// Terminal.Gui 栈注册（AddTuiServices），必须在交互核心注册之后调用；
    /// 无头宿主（daemon / attach）可传 null 复用同一组合根。
    /// </summary>
    public static OneCodeApp Create(string[] args, Action<IServiceCollection>? configureTuiStack = null)
    {
        var startupTimer = new StartupTimer();
        startupTimer.Mark("entry");

        var workingDir = Environment.CurrentDirectory;

        var builder = Host.CreateApplicationBuilder(args);
        startupTimer.Mark("builder-created");

        AddOneCodeCore(builder.Services, workingDir, configureTuiStack);

        builder.ConfigureApplicationLogging();
        startupTimer.Mark("services-registered");

        var host = builder.Build();
        startupTimer.Mark("host-built");

        _ = host.Services.GetRequiredService<IModelCatalogCache>();
        startupTimer.Mark("model-catalog-loaded");

        var logger = host.Services.GetRequiredService<ILogger<OneCodeApp>>();
        logger.LogDebug("Startup timing:\n{Summary}", startupTimer.FormatSummary());
        WriteDebugLogHint();

        return new OneCodeApp(
            host,
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

            var shell = _host.Services.GetRequiredService<InteractiveShellRunner>();
            return await shell(ct).ConfigureAwait(false);
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

    /// <summary>
    /// 只在 DEBUG 构建提示日志路径：Release 构建正常启动不产生非 warning/error 输出，
    /// 其日志文件位置记录在 <c>docs/settings.md</c>。
    /// </summary>
    private static void WriteDebugLogHint()
    {
        if (DebugFileLoggerProvider.IsDebugBuild)
            Console.Error.WriteLine($"Log file: {DebugFileLoggerProvider.GetLogFilePath()}");
    }
}
