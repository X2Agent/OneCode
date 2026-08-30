namespace OneCode.Core.Models;

/// <summary>
/// Read-only view of the models.dev catalog snapshot used for context windows
/// and capability (attachment / reasoning) lookups.
/// </summary>
public interface IModelCatalog
{
    int GetContextWindow(string? modelId);
    bool SupportsAttachment(string? modelId);
    bool SupportsReasoning(string? modelId);
    IReadOnlyList<ReasoningOption> GetReasoningOptions(string? modelId);
    int Count { get; }
}

/// <summary>
/// Mutable holder for the current <see cref="ModelCatalog"/> snapshot.
/// Updated by cache load / refresh; injected as the sole <see cref="IModelCatalog"/>.
/// </summary>
public sealed class ModelCatalogStore : IModelCatalog
{
    private ModelCatalog _current = ModelCatalog.Empty;

    public void Replace(ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Interlocked.Exchange(ref _current, catalog);
    }

    public ModelCatalog Current => _current;

    public int GetContextWindow(string? modelId) => _current.GetContextWindow(modelId);
    public bool SupportsAttachment(string? modelId) => _current.SupportsAttachment(modelId);
    public bool SupportsReasoning(string? modelId) => _current.SupportsReasoning(modelId);
    public IReadOnlyList<ReasoningOption> GetReasoningOptions(string? modelId) => _current.GetReasoningOptions(modelId);
    public int Count => _current.Count;
}
