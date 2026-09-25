namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 配置加载器——从独立 hooks.json 解析 hook 配置
///
/// 加载结果由 HookConfigBootstrapper 注册到 HookRegistry，并写入 <see cref="HookLoadDiagnostics"/>。
///
/// 配置格式：每个拦截点下是 matcher 分组数组（HookMatcherGroup）。
/// 节点名只接受 <see cref="HookInterceptionPoints"/> 的协议线格式名；
/// 已废弃的 OneCode 专有事件名给出**显式迁移诊断**（有后继节点的提示新名，无后继节点的提示不支持），
/// 不静默跳过；JSON 损坏 / IO 失败记录错误并整体跳过（含行号）。
/// </summary>
public sealed class HookSettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string OpenPointList =
        string.Join(", ", HookInterceptionPoints.Open.Select(HookInterceptionPoints.ToWireName));

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

            Dictionary<HookInterceptionPoint, List<HookMatcherGroup>> result = [];
            List<string> errors = [];

            foreach (var eventProp in doc.RootElement.EnumerateObject())
            {
                if (HookInterceptionPoints.TryParseWireName(eventProp.Name, out var point))
                {
                    if (!HookInterceptionPoints.IsOpen(point))
                    {
                        var closed = $"拦截点 '{eventProp.Name}' 为内部生命周期边界，不对用户配置开放，已跳过";
                        errors.Add(closed);
                        _logger.LogWarning("hooks.json: {Message}", closed);
                        continue;
                    }
                }
                else
                {
                    var successor = HookInterceptionPoints.TryGetLegacySuccessor(eventProp.Name);
                    var message = successor is not null
                        ? $"事件 '{eventProp.Name}' 已迁移为拦截点 '{successor}'，请改写 hooks.json"
                        : $"事件 '{eventProp.Name}' 不再属于 Hook 拦截范围（有效节点: {OpenPointList}），已跳过";
                    errors.Add(message);
                    _logger.LogWarning("hooks.json: {Message}", message);
                    continue;
                }

                var groups = ParseEventHooks(eventProp.Value);
                if (groups is { Count: > 0 })
                    result[point] = groups;
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
    /// 解析单个拦截点下的 hook 配置（matcher-group 格式）。
    /// </summary>
    private static List<HookMatcherGroup> ParseEventHooks(JsonElement pointArray)
    {
        if (pointArray.ValueKind != JsonValueKind.Array)
            return [];

        List<HookMatcherGroup> groups = [];

        foreach (var item in pointArray.EnumerateArray())
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
