namespace OneCode.Core.Models;

public sealed class ModelCatalog
{
    /// <summary>
    /// 统一索引：同时存储 "provider/model" 和 bare "model" 两种 key。
    /// bare model 用 TryAdd（首个 provider 入场的赢）。
    /// </summary>
    private readonly Dictionary<string, ModelEntry> _models = new(StringComparer.OrdinalIgnoreCase);

    private ModelCatalog() { }

    /// <summary>Empty catalog used before the first disk/API load.</summary>
    public static ModelCatalog Empty { get; } = new();

    public static ModelCatalog LoadFromStream(Stream stream)
    {
        var catalog = new ModelCatalog();
        using var doc = JsonDocument.Parse(stream);

        foreach (var providerProp in doc.RootElement.EnumerateObject())
        {
            var providerId = providerProp.Name;
            if (!providerProp.Value.TryGetProperty("models", out var modelsProp)) continue;

            foreach (var modelProp in modelsProp.EnumerateObject())
            {
                var modelId = modelProp.Name;
                var modelValue = modelProp.Value;

                var context = 0;
                if (modelValue.TryGetProperty("limit", out var limitProp)
                    && limitProp.TryGetProperty("context", out var contextProp))
                {
                    context = ReadInt(contextProp);
                }

                var supportsAttachment = modelValue.TryGetProperty("attachment", out var attachProp)
                    && attachProp.ValueKind == JsonValueKind.True;

                var supportsReasoning = modelValue.TryGetProperty("reasoning", out var reasoningProp)
                    && reasoningProp.ValueKind == JsonValueKind.True;

                if (context <= 0) continue;

                var fullId = $"{providerId}/{modelId}";
                var entry = new ModelEntry(fullId, context, supportsAttachment, supportsReasoning);

                // "anthropic/claude-sonnet-4-20250514" → entry（精确覆盖）
                catalog._models[fullId] = entry;
                // "claude-sonnet-4-20250514" → entry（首个 provider 赢，不覆盖）
                catalog._models.TryAdd(modelId, entry);
            }
        }
        return catalog;
    }

    public int GetContextWindow(string? modelId) => Resolve(modelId)?.ContextWindow ?? 0;

    /// <summary>Whether the model supports multimodal (image) attachments.</summary>
    public bool SupportsAttachment(string? modelId) => Resolve(modelId)?.SupportsAttachment ?? false;

    /// <summary>Whether the model supports reasoning / chain-of-thought.</summary>
    public bool SupportsReasoning(string? modelId) => Resolve(modelId)?.SupportsReasoning ?? false;

    public int Count => _models.Values.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private ModelEntry? Resolve(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return null;

        // 直接命中（覆盖 "provider/model" 和 bare "model" 两种 key）
        if (_models.TryGetValue(modelId, out var entry)) return entry;

        // 剥掉 [Nk]/[Nm] 后缀再试（如 "deepseek/deepseek-v4-pro[1m]" → "deepseek/deepseek-v4-pro"）
        var bracketIdx = modelId.IndexOf('[');
        if (bracketIdx > 0)
        {
            var baseId = modelId[..bracketIdx];
            if (_models.TryGetValue(baseId, out var bracketEntry))
            {
                // 如果后缀声明了上下文窗口，覆盖快照值
                var suffixCtx = TryParseBracketContext(modelId.AsSpan(bracketIdx));
                if (suffixCtx > 0)
                    return bracketEntry with { ContextWindow = suffixCtx };
                return bracketEntry;
            }
            // 剥掉后缀后也试一下去前缀
            var slashIdx2 = baseId.IndexOf('/');
            if (slashIdx2 >= 0)
            {
                var tail = baseId[(slashIdx2 + 1)..];
                if (_models.TryGetValue(tail, out var fallback2))
                {
                    var suffixCtx2 = TryParseBracketContext(modelId.AsSpan(bracketIdx));
                    if (suffixCtx2 > 0)
                        return fallback2 with { ContextWindow = suffixCtx2 };
                    return fallback2;
                }
            }
        }

        // 剥掉第一层前缀再试（如用户传 "requesty/anthropic/claude-sonnet-4" → 试 "anthropic/claude-sonnet-4"）
        var slashIdx = modelId.IndexOf('/');
        if (slashIdx >= 0)
        {
            var tail = modelId[(slashIdx + 1)..];
            if (_models.TryGetValue(tail, out var fallback)) return fallback;
        }

        return null;
    }

    /// <summary>
    /// 解析模型 ID 中的 [Nk]/[Nm]/[N] 后缀为上下文窗口大小（token 数）。
    /// 如 [1m] → 1_000_000, [128k] → 131_072, [32k] → 32_768。
    /// </summary>
    private static int TryParseBracketContext(ReadOnlySpan<char> bracketSpan)
    {
        // bracketSpan 形如 "[1m]" 或 "[128k]"
        if (bracketSpan.Length < 3 || bracketSpan[0] != '[') return 0;
        var closeIdx = bracketSpan.IndexOf(']');
        if (closeIdx < 0) return 0;

        var inner = bracketSpan[1..closeIdx];
        var lastChar = inner[^1];
        int multiplier;
        ReadOnlySpan<char> numSpan;

        if (lastChar is 'k' or 'K')
        {
            multiplier = 1024;
            numSpan = inner[..^1];
        }
        else if (lastChar is 'm' or 'M')
        {
            multiplier = 1_000_000;
            numSpan = inner[..^1];
        }
        else
        {
            multiplier = 1;
            numSpan = inner;
        }

        if (!int.TryParse(numSpan, out var num)) return 0;
        return num * multiplier;
    }

    private static int ReadInt(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number) return 0;
        if (element.TryGetInt64(out var longVal)) return longVal > int.MaxValue ? int.MaxValue : (int)longVal;
        if (element.TryGetDouble(out var doubleVal)) return doubleVal > int.MaxValue ? int.MaxValue : (int)doubleVal;
        return 0;
    }

}

/// <summary>单个模型的目录条目。</summary>
public sealed record ModelEntry(
    string Id,
    int ContextWindow,
    bool SupportsAttachment = false,
    bool SupportsReasoning = false);

