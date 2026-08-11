namespace OneCode.Core.Prompt;

/// <summary>
/// Read/render API for prompt templates. Store registration stays on the concrete
/// <see cref="PromptManager"/> and is performed only at the composition root.
/// </summary>
public interface IPromptManager
{
    Task<string?> GetPromptAsync(string name, CancellationToken ct = default);
    Task<string> GetPromptOrDefaultAsync(string name, string defaultValue, CancellationToken ct = default);
    Task<string> RenderPromptAsync(
        string name,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken ct = default);
    Task<bool> ExistsAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetCategoryAsync(string category, CancellationToken ct = default);
}
