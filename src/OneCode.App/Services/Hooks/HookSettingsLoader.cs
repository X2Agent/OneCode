namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 配置加载器——从独立 hooks.json 解析 hook 配置
///
/// 加载结果由 HookConfigBootstrapper 注册到 HookRegistry，并写入 <see cref="HookLoadDiagnostics"/>。
///
/// 配置格式：每个事件下是 matcher 分组数组（HookMatcherGroup）。
/// 未知事件名降级为警告并跳过（记录到诊断，不中断整个文件）；
/// JSON 损坏 / IO 失败记录错误并整体跳过（含行号）。
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

    /// <summary>
    /// 加载 <paramref name="configDir"/> 下的 hooks.json。
    /// 文件缺失时返回 NotFound、Hooks 为 null（属正常状态，不算失败）。
    /// </summary>
    public HookFileLoadResult Load(string configDir)
    {
        var hooksJsonPath = Path.Combine(configDir, "hooks.json");
        if (!File.Exists(hooksJsonPath))
            return new HookFileLoadResult(hooksJsonPath, HookFileLoadStatus.NotFound, null, []);

        try
        {
            var json = File.ReadAllText(hooksJsonPath);
            using var doc = JsonDocument.Parse(json);

            // hooks.json 根对象就是 hooks 内容（不再是 settings.json 的子属性）
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Fail(hooksJsonPath, ["hooks.json 根节点必须是 JSON 对象"]);

            Dictionary<HookEvent, List<HookMatcherGroup>> result = [];
            List<string> errors = [];

            foreach (var eventProp in doc.RootElement.EnumerateObject())
            {
                if (!Enum.TryParse<HookEvent>(eventProp.Name, ignoreCase: true, out var hookEvent))
                {
                    var message = $"未知事件名 '{eventProp.Name}'（有效值: {string.Join(", ", Enum.GetNames<HookEvent>())}），已跳过";
                    errors.Add(message);
                    _logger.LogWarning("hooks.json: {Message}", message);
                    continue;
                }

                var groups = ParseEventHooks(eventProp.Value);
                if (groups is { Count: > 0 })
                    result[hookEvent] = groups;
            }

            return new HookFileLoadResult(
                hooksJsonPath,
                HookFileLoadStatus.Loaded,
                result.Count > 0 ? result : null,
                errors);
        }
        catch (JsonException jex)
        {
            var location = jex.LineNumber >= 0 ? $"（第 {jex.LineNumber + 1} 行）" : string.Empty;
            return Fail(hooksJsonPath, [$"JSON 解析失败{location}: {jex.Message}"], jex);
        }
        catch (Exception ex)
        {
            return Fail(hooksJsonPath, [$"读取失败: {ex.Message}"], ex);
        }
    }

    private HookFileLoadResult Fail(string path, IReadOnlyList<string> errors, Exception? exception = null)
    {
        _logger.LogWarning(exception, "Hook configuration {Path} failed to load: {Errors}", path, string.Join("; ", errors));
        return new HookFileLoadResult(path, HookFileLoadStatus.Failed, null, errors);
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
