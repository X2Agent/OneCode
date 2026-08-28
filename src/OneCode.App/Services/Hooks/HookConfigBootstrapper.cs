using OneCode.Core.Hooks.Notifications;


namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 配置启动加载器——从 hooks.json 加载 hook 配置并注册到 HookRegistry，
/// 同时三层合并声明式通知渠道定义（notification-providers.json）到 NotificationProviderRegistry。
///
/// 启动时调用一次，将 <c>~/.onecode/hooks.json</c> 和 <c>.onecode/hooks.json</c>
/// 中的 hook 配置注册到系统，并把各文件加载状态写入 <see cref="HookLoadDiagnostics"/>。
/// </summary>
public sealed class HookConfigBootstrapper
{
    private readonly HookSettingsLoader _loader;
    private readonly HookRegistry _registry;
    private readonly NotificationProviderDefinitionLoader _providerLoader;
    private readonly NotificationProviderRegistry _providerRegistry;
    private readonly HookLoadDiagnostics _loadDiagnostics;
    private readonly ILogger<HookConfigBootstrapper> _logger;

    public HookConfigBootstrapper(
        HookSettingsLoader loader,
        HookRegistry registry,
        NotificationProviderDefinitionLoader providerLoader,
        NotificationProviderRegistry providerRegistry,
        HookLoadDiagnostics loadDiagnostics,
        ILogger<HookConfigBootstrapper> logger)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _providerLoader = providerLoader ?? throw new ArgumentNullException(nameof(providerLoader));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _loadDiagnostics = loadDiagnostics ?? throw new ArgumentNullException(nameof(loadDiagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>config 来源注册名的固定前缀（热重载重建时据此区分配置 hook 与编程注册 hook）。</summary>
    public const string ConfigNamePrefix = "config:";

    /// <summary>
    /// 启动路径：基于 <see cref="Build"/> 的快照注册所有 hook，并刷新加载诊断与声明式通知渠道定义。
    /// </summary>
    /// <param name="userConfigDir">用户配置目录（~/.onecode）。</param>
    /// <param name="projectConfigDir">项目配置目录（.onecode，可选，优先级更高）。</param>
    /// <param name="builtinProviderDir">内置默认定义目录，缺省 AppContext.BaseDirectory。</param>
    /// <returns>已注册的 hook 总数。</returns>
    public int Bootstrap(string userConfigDir, string? projectConfigDir = null, string? builtinProviderDir = null)
    {
        var snapshot = Build(userConfigDir, projectConfigDir, builtinProviderDir);

        foreach (var registration in snapshot.Registrations)
            _registry.Register(registration);

        _loadDiagnostics.Record(snapshot.Reports);
        _providerRegistry.SetDefinitions(snapshot.ProviderDefinitions);

        if (snapshot.Registrations.Count > 0)
            _logger.LogInformation("Bootstrapped {Count} hooks total", snapshot.Registrations.Count);

        return snapshot.Registrations.Count;
    }

    /// <summary>
    /// 构建完整 hook 配置快照（纯读取，不触碰 HookRegistry / NotificationProviderRegistry）：
    /// 用户层 + 项目层合并出待注册项与各文件加载报告，并三层合并声明式通知渠道定义。
    /// 热重载先 <see cref="Build"/> 再经 <see cref="HookRegistry.ReplaceAll"/> 原子交换。
    /// </summary>
    /// <param name="userConfigDir">用户配置目录（~/.onecode）。</param>
    /// <param name="projectConfigDir">项目配置目录（.onecode，可选，优先级更高）。</param>
    /// <param name="builtinProviderDir">内置默认定义目录，缺省 AppContext.BaseDirectory。</param>
    public HookConfigSnapshot Build(string userConfigDir, string? projectConfigDir = null, string? builtinProviderDir = null)
    {
        var userResult = BuildFromDirectory(userConfigDir, basePriority: 100);
        var reports = new List<HookFileLoadReport> { userResult.Report };
        var registrations = new List<HookRegistration>(userResult.Registrations);

        if (!string.IsNullOrEmpty(projectConfigDir))
        {
            var projectResult = BuildFromDirectory(projectConfigDir, basePriority: 200);
            reports.Add(projectResult.Report);
            registrations.AddRange(projectResult.Registrations);
        }

        var providerDefinitions = MergeNotificationProviders(
            builtinProviderDir ?? AppContext.BaseDirectory, userConfigDir, projectConfigDir);

        return new HookConfigSnapshot(reports, registrations, providerDefinitions);
    }

    /// <summary>
    /// 三层合并声明式通知渠道定义：同名整条替换，高层胜出（Info 日志，非字段合并）。
    /// </summary>
    private Dictionary<string, NotificationProviderDefinition> MergeNotificationProviders(
        string builtinDir, string userDir, string? projectDir)
    {
        Dictionary<string, NotificationProviderDefinition> definitions = new(StringComparer.OrdinalIgnoreCase);
        var layers = new[] { ("内置", builtinDir), ("用户", userDir), ("项目", projectDir ?? string.Empty) };

        foreach (var (label, dir) in layers)
        {
            if (string.IsNullOrEmpty(dir))
                continue;

            var load = _providerLoader.Load(dir);
            if (load.Providers is null)
                continue;

            foreach (var (name, definition) in load.Providers)
            {
                if (definitions.ContainsKey(name))
                    _logger.LogInformation("notification-providers: '{Name}' 由{Label}层定义覆盖（整条替换）", name, label);
                definitions[name] = definition;
            }
        }

        _logger.LogDebug("Loaded {Count} declarative notification provider definitions", definitions.Count);
        return definitions;
    }

    private (HookFileLoadReport Report, List<HookRegistration> Registrations) BuildFromDirectory(
        string configDir, int basePriority)
    {
        var load = _loader.Load(configDir);
        if (load.Status != HookFileLoadStatus.Loaded || load.Hooks is null)
            return (new HookFileLoadReport(configDir, load.Status, 0, load.Errors), []);

        List<HookRegistration> registrations = [];
        foreach (var (@event, groups) in load.Hooks)
        {
            foreach (var group in groups)
            {
                foreach (var config in group.Hooks)
                {
                    HookType hookType;
                    try
                    {
                        hookType = HookTypeParser.Parse(config.Type);
                    }
                    catch (ArgumentException ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Skipping hook with unknown type '{Type}' for event {Event} in {ConfigDir}",
                            config.Type,
                            @event,
                            configDir);
                        continue;
                    }

                    registrations.Add(new HookRegistration
                    {
                        Name = $"{ConfigNamePrefix}{@event}:{hookType}:{Guid.NewGuid():N}",
                        Event = @event,
                        Matcher = group.Matcher,
                        Priority = config.Priority ?? basePriority,
                        Once = config.Once,
                        ExecutorType = hookType,
                        TimeoutMs = config.TimeoutMs ?? 5000,
                        Config = config,
                    });
                }
            }
        }

        _logger.LogDebug("Bootstrapped {Count} hooks from {ConfigDir}", registrations.Count, configDir);
        return (new HookFileLoadReport(configDir, HookFileLoadStatus.Loaded, registrations.Count, load.Errors),
            registrations);
    }
}

/// <summary>一次完整 hook 配置构建的不可变快照（启动 Bootstrap 与热重载共用）。</summary>
/// <param name="Reports">各配置文件加载状态报告（写入 HookLoadDiagnostics）。</param>
/// <param name="Registrations">待注册的 hook 注册项（经 HookRegistry.ReplaceAll 原子交换）。</param>
/// <param name="ProviderDefinitions">三层合并后的声明式通知渠道定义。</param>
public sealed record HookConfigSnapshot(
    IReadOnlyList<HookFileLoadReport> Reports,
    IReadOnlyList<HookRegistration> Registrations,
    IReadOnlyDictionary<string, NotificationProviderDefinition> ProviderDefinitions);
