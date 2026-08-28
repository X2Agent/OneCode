using OneCode.App.Services.Hooks.Notifications;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks;

/// <summary>Loader 层结果：解析出的声明式 Provider 定义 + 诊断消息。</summary>
/// <param name="Path">notification-providers.json 绝对路径。</param>
/// <param name="Status">加载状态。</param>
/// <param name="Providers">渠道名 → 定义；加载失败或为空时为 null。</param>
/// <param name="Errors">非致命诊断消息（无效定义跳过原因等）。</param>
public sealed record NotificationProviderLoadResult(
    string Path,
    HookFileLoadStatus Status,
    Dictionary<string, NotificationProviderDefinition>? Providers,
    IReadOnlyList<string> Errors);

/// <summary>
/// 声明式通知渠道定义加载器——解析单目录下的 notification-providers.json。
///
/// 校验规则（无效定义跳过 + Warning，不影响同文件其他条目）：
/// url 必须以 https:// 开头、body 必须为 JSON 对象、signing.preset 必须已知。
/// JSON 损坏 / IO 失败记录错误并整体跳过（含行号）。
/// </summary>
public sealed class NotificationProviderDefinitionLoader
{
    /// <summary>定义文件名（位于各配置目录下）。</summary>
    public const string FileName = "notification-providers.json";

    private readonly ILogger<NotificationProviderDefinitionLoader> _logger;

    public NotificationProviderDefinitionLoader(ILogger<NotificationProviderDefinitionLoader> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 加载 <paramref name="configDir"/> 下的 notification-providers.json。
    /// 文件缺失时返回 NotFound、Providers 为 null（属正常状态，不算失败）。
    /// </summary>
    public NotificationProviderLoadResult Load(string configDir)
    {
        var path = Path.Combine(configDir, FileName);
        if (!File.Exists(path))
            return new NotificationProviderLoadResult(path, HookFileLoadStatus.NotFound, null, []);

        try
        {
            var json = File.ReadAllText(path);
            var raw = JsonSerializer.Deserialize(json, HookSerializerContext.Default.DictionaryStringNotificationProviderDefinition);
            if (raw is null)
                return Fail(path, [$"{FileName} 根节点必须是 JSON 对象"]);

            Dictionary<string, NotificationProviderDefinition> providers = new(StringComparer.OrdinalIgnoreCase);
            List<string> errors = [];

            foreach (var (name, definition) in raw)
            {
                var failure = Validate(name, definition);
                if (failure is not null)
                {
                    errors.Add(failure);
                    _logger.LogWarning("{FileName}: {Failure}", FileName, failure);
                    continue;
                }

                providers[name] = ExpandPreset(definition);
            }

            return new NotificationProviderLoadResult(
                path,
                HookFileLoadStatus.Loaded,
                providers.Count > 0 ? providers : null,
                errors);
        }
        catch (JsonException jex)
        {
            var location = jex.LineNumber >= 0 ? $"（第 {jex.LineNumber + 1} 行）" : string.Empty;
            return Fail(path, [$"JSON 解析失败{location}: {jex.Message}"], jex);
        }
        catch (Exception ex)
        {
            return Fail(path, [$"读取失败: {ex.Message}"], ex);
        }
    }

    private NotificationProviderLoadResult Fail(string path, IReadOnlyList<string> errors, Exception? exception = null)
    {
        _logger.LogWarning(exception, "Notification provider definitions {Path} failed to load: {Errors}",
            path, string.Join("; ", errors));
        return new NotificationProviderLoadResult(path, HookFileLoadStatus.Failed, null, errors);
    }

    /// <summary>校验单个定义；通过返回 null，否则返回跳过原因。</summary>
    private static string? Validate(string name, NotificationProviderDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Url) || !definition.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"'{name}' 已跳过：url 必须以 https:// 开头";

        if (definition.Body is not { ValueKind: JsonValueKind.Object })
            return $"'{name}' 已跳过：body 必须是 JSON 对象";

        var preset = definition.Signing?.Preset;
        if (preset is not null && NotificationSignaturePresets.Expand(preset) is null)
            return $"'{name}' 已跳过：未知签名 preset '{preset}'（可用: {string.Join(", ", NotificationSignaturePresets.Known)}）";

        return null;
    }

    /// <summary>签名 preset 糖：展开为完整签名定义（preset 存在时其余 signing 字段被忽略）。</summary>
    private static NotificationProviderDefinition ExpandPreset(NotificationProviderDefinition definition)
    {
        var preset = definition.Signing?.Preset;
        if (preset is null)
            return definition;

        var expanded = NotificationSignaturePresets.Expand(preset);
        return expanded is null ? definition : definition with { Signing = expanded };
    }
}
