using OneCode.App.Services.Hooks.Notifications;


using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// 通知渠道解析中枢——声明式定义优先、编译型 <see cref="INotificationProvider"/> 兜底，同名声明式胜出。
///
/// 声明式定义由 <see cref="NotificationProviderDefinitionLoader"/> 加载并经 HookConfigBootstrapper
/// 三层合并（内置 &lt; 用户 &lt; 项目，整条替换）后经 <see cref="SetDefinitions"/> 注入；
/// <see cref="DeclarativeNotificationProvider"/> 实例按名称缓存，HttpClient 来自命名工厂客户端。
/// </summary>
public sealed class NotificationProviderRegistry(
    IEnumerable<INotificationProvider> compiled,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory)
{
    /// <summary>声明式引擎使用的命名 HttpClient。</summary>
    public const string HttpClientName = "hook-notification-providers";

    private readonly Dictionary<string, INotificationProvider> _compiled =
        compiled?.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, INotificationProvider>(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly object _lock = new();
    private Dictionary<string, NotificationProviderDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, INotificationProvider> _declarative = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>整体替换声明式定义（Bootstrap/热重载时调用），并清空实例缓存。</summary>
    public void SetDefinitions(IReadOnlyDictionary<string, NotificationProviderDefinition> definitions)
    {
        lock (_lock)
        {
            _definitions = new Dictionary<string, NotificationProviderDefinition>(definitions, StringComparer.OrdinalIgnoreCase);
            _declarative.Clear();
        }
    }

    /// <summary>按名称解析渠道：声明式定义优先，编译型兜底；未注册返回 null。</summary>
    public INotificationProvider? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        lock (_lock)
        {
            if (_declarative.TryGetValue(name, out var cached))
                return cached;

            if (_definitions.TryGetValue(name, out var definition))
            {
                var provider = new DeclarativeNotificationProvider(
                    name,
                    definition,
                    _httpClientFactory.CreateClient(HttpClientName),
                    _loggerFactory.CreateLogger<DeclarativeNotificationProvider>());
                _declarative[name] = provider;
                return provider;
            }
        }

        return _compiled.GetValueOrDefault(name);
    }

    /// <summary>全部可用渠道名（声明式 ∪ 编译型），供诊断信息展示。</summary>
    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_lock)
            {
                return _definitions.Keys
                    .Concat(_compiled.Keys.Where(k => !_definitions.ContainsKey(k)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }
}
