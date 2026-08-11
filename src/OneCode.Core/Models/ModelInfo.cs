namespace OneCode.Core.Models;

/// <summary>
/// 模型信息——从配置加载的模型描述。
/// 替代硬编码的 ModelConfig 记录。
/// </summary>
public sealed record ModelInfo(
    string Id,              // 模型标识，即用户配置的 model 值（如 "gpt-5.4"），不含 provider 前缀
    string ProviderId,       // API 协议标识 "anthropic"/"openai"/"ollama"
    string ModelId,          // 发送给 API 的实际模型 ID（与 Id 相同）
    int MaxOutputTokens,
    int? ThinkingBudget,
    int ContextWindow)
{
    /// <summary>模型是否支持结构化 JSON 工具结果。null = 未知，使用启发式判断。</summary>
    public bool? SupportsStructuredToolResults { get; init; }

    /// <summary>模型是否需要工具过滤。null = 未知，使用启发式判断。</summary>
    public bool? RequiresToolFiltering { get; init; }
}
