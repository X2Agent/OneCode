namespace OneCode.Core.Prompt;

public sealed class PromptTemplate
{
    private readonly string _name;
    private readonly string _rawTemplate;
    private readonly Dictionary<string, string> _defaults;

    public string Name => _name;

    public PromptTemplate(string name, string rawTemplate)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(rawTemplate);
        _name = name;
        _rawTemplate = rawTemplate;
        _defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public PromptTemplate WithDefault(string variable, string value)
    {
        _defaults[variable] = value;
        return this;
    }

    public string Render(IReadOnlyDictionary<string, string>? variables = null)
    {
        var result = _rawTemplate;

        if (variables is { Count: > 0 })
        {
            foreach (var (key, value) in variables)
            {
                result = result.Replace($"{{{{{key}}}}}", value);
            }
        }

        foreach (var (key, value) in _defaults)
        {
            result = result.Replace($"{{{{{key}}}}}", value);
        }

        return result;
    }

    public static PromptTemplate FromRaw(string name, string rawContent)
    {
        return new PromptTemplate(name, rawContent);
    }
}
