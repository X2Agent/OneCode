namespace OneCode.Core.Prompt;

public sealed class PromptManager : IPromptManager
{
    private readonly List<IPromptStore> _stores = new();
    private readonly Dictionary<string, PromptTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public PromptManager(ILogger<PromptManager>? logger = null)
    {
        _logger = logger;
    }

    public PromptManager AddStore(IPromptStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _stores.Add(store);
        return this;
    }

    public PromptManager RegisterTemplate(PromptTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        _templates[template.Name] = template;
        return this;
    }

    public async Task<string?> GetPromptAsync(string name, CancellationToken ct = default)
    {
        foreach (var store in _stores)
        {
            var content = await store.GetAsync(name, ct).ConfigureAwait(false);
            if (content != null)
            {
                _logger?.LogDebug("Prompt '{Name}' loaded from store", name);
                return content;
            }
        }

        if (_templates.TryGetValue(name, out var template))
        {
            _logger?.LogDebug("Prompt '{Name}' loaded from registered template", name);
            return template.Render();
        }

        _logger?.LogWarning("Prompt '{Name}' not found in any store", name);
        return null;
    }

    public async Task<string> GetPromptOrDefaultAsync(string name, string defaultValue, CancellationToken ct = default)
    {
        var result = await GetPromptAsync(name, ct).ConfigureAwait(false);
        return result ?? defaultValue;
    }

    public async Task<string> RenderPromptAsync(
        string name,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken ct = default)
    {
        var raw = await GetPromptAsync(name, ct).ConfigureAwait(false);
        if (raw == null)
        {
            if (_templates.TryGetValue(name, out var template))
            {
                return template.Render(variables);
            }
            throw new InvalidOperationException($"Prompt '{name}' not found");
        }

        var promptTemplate = new PromptTemplate(name, raw);
        return promptTemplate.Render(variables);
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken ct = default)
    {
        foreach (var store in _stores)
        {
            if (await store.ExistsAsync(name, ct).ConfigureAwait(false))
                return true;
        }

        return _templates.ContainsKey(name);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetCategoryAsync(
        string category, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var store in _stores)
        {
            var entries = await store.GetAllAsync(category, ct).ConfigureAwait(false);
            foreach (var (key, value) in entries)
            {
                result.TryAdd(key, value);
            }
        }

        return result;
    }
}
