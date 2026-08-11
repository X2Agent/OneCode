namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 配置启动加载器——从 hooks.json 加载 hook 配置并注册到 HookRegistry。
///
/// 启动时调用一次，将 <c>~/.onecode/hooks.json</c> 和 <c>.onecode/hooks.json</c>
/// 中的 hook 配置注册到系统。
/// </summary>
public sealed class HookConfigBootstrapper
{
    private readonly HookSettingsLoader _loader;
    private readonly HookRegistry _registry;
    private readonly ILogger<HookConfigBootstrapper> _logger;

    public HookConfigBootstrapper(
        HookSettingsLoader loader,
        HookRegistry registry,
        ILogger<HookConfigBootstrapper> logger)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 从配置目录加载并注册所有 hook。
    /// </summary>
    /// <param name="userConfigDir">用户配置目录（~/.onecode）。</param>
    /// <param name="projectConfigDir">项目配置目录（.OneCode，可选，优先级更高）。</param>
    /// <returns>已注册的 hook 总数。</returns>
    public int Bootstrap(string userConfigDir, string? projectConfigDir = null)
    {
        var count = BootstrapFromDirectory(userConfigDir, basePriority: 100);
        if (!string.IsNullOrEmpty(projectConfigDir))
            count += BootstrapFromDirectory(projectConfigDir, basePriority: 200);

        if (count > 0)
            _logger.LogInformation("Bootstrapped {Count} hooks total", count);

        return count;
    }

    private int BootstrapFromDirectory(string configDir, int basePriority)
    {
        var hookConfig = _loader.Load(configDir);
        if (hookConfig is null || hookConfig.Count == 0)
        {
            _logger.LogDebug("No hook configuration found in {ConfigDir}", configDir);
            return 0;
        }

        var count = 0;
        foreach (var (@event, groups) in hookConfig)
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

                    var registration = new HookRegistration
                    {
                        Name = $"config:{@event}:{hookType}:{Guid.NewGuid():N}",
                        Event = @event,
                        Matcher = group.Matcher,
                        Priority = config.Priority ?? basePriority,
                        Once = config.Once,
                        ExecutorType = hookType,
                        TimeoutMs = config.TimeoutMs ?? 5000,
                        Config = config,
                    };

                    _registry.Register(registration);
                    count++;
                }
            }
        }

        _logger.LogDebug("Bootstrapped {Count} hooks from {ConfigDir}", count, configDir);
        return count;
    }

}
