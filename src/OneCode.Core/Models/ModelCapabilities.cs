namespace OneCode.Core.Models;

/// <summary>
/// 模型能力检测——根据 modelId 和 providerId 判断模型支持的特性。
/// 优先使用 catalog 数据（<see cref="ModelInfo"/>），缺省时回退到启发式判断。
/// </summary>
/// <remarks>
/// 用于 <see cref="Tools.ToolResult"/> 序列化时选择格式：
/// 支持结构化 JSON 的模型返回 JSON，不支持的模型返回 Markdown 字符串。
/// </remarks>
public static class ModelCapabilities
{
    /// <summary>
    /// 上下文窗口阈值——≥ 此值的未知 provider 模型走 FullToolProvider（全量工具），
    /// 低于此值走 FilteredToolProvider（Always + 检索命中 + 已激活）。
    /// 32K 足以容纳 34 个工具定义 + 系统 prompt，无需过滤。
    /// 已知 provider（Anthropic/OpenAI/Ollama）不使用此阈值——由 provider 直接决定。
    /// </summary>
    public const int ToolFilteringContextWindowThreshold = 32_768;

    /// <summary>
    /// 判断模型是否支持结构化 JSON 工具结果。
    /// 优先使用 <paramref name="model"/> 中的 catalog 数据；缺省时回退到启发式。
    /// </summary>
    public static bool SupportsStructuredToolResults(string? modelId, string? providerId, ModelInfo? model = null)
    {
        // catalog 覆盖
        if (model?.SupportsStructuredToolResults is { } capability)
            return capability;

        if (string.IsNullOrWhiteSpace(modelId)) return false;

        var lower = modelId.ToLowerInvariant();

        // 按 provider 判断
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var providerLower = providerId.ToLowerInvariant();
            if (providerLower.Contains(Constants.ModelProviders.Ollama))
                return false; // Ollama 本地模型默认不支持
            if (providerLower.Contains(Constants.ModelProviders.Anthropic))
                return true;
            if (providerLower.Contains(Constants.ModelProviders.OpenAI))
                return true;
        }

        // 按 model id 子串判断（provider 未知时的 fallback）
        if (lower.Contains("claude")) return true;
        if (lower.Contains("gpt-4") || lower.Contains("gpt-5")) return true;
        if (lower.Contains("o1") || lower.Contains("o3") || lower.Contains("o4")) return true;
        if (lower.Contains("deepseek")) return true;

        return false;
    }

    /// <summary>
    /// 判断模型是否需要工具过滤（本地小模型场景）。
    /// 优先使用 <paramref name="model"/> 中的 catalog 数据；缺省时回退到启发式。
    /// </summary>
    /// <param name="providerId">模型提供者（如 "ollama"、"anthropic"）。</param>
    /// <param name="contextWindow">模型上下文窗口大小（token 数）。</param>
    /// <param name="model">可选的 catalog 模型信息，覆盖启发式判断。</param>
    /// <returns>true 表示需要过滤工具集；false 表示全量加载。</returns>
    public static bool RequiresToolFiltering(string? providerId, int contextWindow, ModelInfo? model = null)
    {
        // catalog 覆盖
        if (model?.RequiresToolFiltering is { } filtering)
            return filtering;

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var providerLower = providerId.ToLowerInvariant();
            // 云端模型不过滤——prompt caching 使全量工具集更高效
            if (providerLower.Contains(Constants.ModelProviders.Anthropic))
                return false;
            if (providerLower.Contains(Constants.ModelProviders.OpenAI))
                return false;
            // 本地模型始终过滤——工具定义 token 开销对小上下文窗口影响显著
            if (providerLower.Contains(Constants.ModelProviders.Ollama))
                return true;
        }

        // 未知 provider 按上下文窗口决定
        return contextWindow < ToolFilteringContextWindowThreshold;
    }
}
