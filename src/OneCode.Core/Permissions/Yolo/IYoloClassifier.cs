namespace OneCode.Core.Permissions.Yolo;

/// <summary>
/// Auto-mode safety classifier: rule-store matching without LLM calls.
/// </summary>
public interface IYoloClassifier
{
    Task<YoloClassifierResult> ClassifyAsync(
        string toolName,
        JsonElement toolInput,
        CancellationToken ct = default);

    bool IsAllowlistedTool(string toolName);
}
