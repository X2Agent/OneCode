namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 配置加载器——从独立 hooks.json 解析 hook 配置
///
/// 加载结果由 HookConfigBootstrapper 注册到 HookRegistry。
///
/// 配置格式：每个事件下是 matcher 分组数组（HookMatcherGroup）。
/// </summary>
public sealed class HookSettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<HookSettingsLoader> _logger;

    public HookSettingsLoader(ILogger<HookSettingsLoader> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Dictionary<HookEvent, List<HookMatcherGroup>>? Load(string configDir)
    {
        var hooksJsonPath = Path.Combine(configDir, "hooks.json");
        if (!File.Exists(hooksJsonPath))
            return null;

        try
        {
            var json = File.ReadAllText(hooksJsonPath);
            using var doc = JsonDocument.Parse(json);

            // hooks.json 根对象就是 hooks 内容（不再是 settings.json 的子属性）
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            Dictionary<HookEvent, List<HookMatcherGroup>> result = [];

            foreach (var eventProp in doc.RootElement.EnumerateObject())
            {
                if (!Enum.TryParse<HookEvent>(eventProp.Name, ignoreCase: true, out var hookEvent))
                    continue;

                var groups = ParseEventHooks(eventProp.Value);
                if (groups is { Count: > 0 })
                    result[hookEvent] = groups;
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load hooks.json from {Path}", hooksJsonPath);
            return null;
        }
    }

    /// <summary>
    /// 解析单个事件下的 hook 配置（matcher-group 格式）。
    /// </summary>
    private static List<HookMatcherGroup> ParseEventHooks(JsonElement eventArray)
    {
        if (eventArray.ValueKind != JsonValueKind.Array)
            return [];

        List<HookMatcherGroup> groups = [];

        foreach (var item in eventArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var group = JsonSerializer.Deserialize<HookMatcherGroup>(item.GetRawText(), JsonOptions);
            if (group is not null && group.Hooks.Count > 0)
                groups.Add(group);
        }

        return groups;
    }
}
