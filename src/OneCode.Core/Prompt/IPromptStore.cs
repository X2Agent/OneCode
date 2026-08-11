namespace OneCode.Core.Prompt;

public interface IPromptStore
{
    Task<string?> GetAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(string category, CancellationToken ct = default);

    Task<bool> ExistsAsync(string name, CancellationToken ct = default);
}
