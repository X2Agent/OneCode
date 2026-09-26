namespace OneCode.Core.Prompt;

public sealed class PromptTemplate
{
    private readonly string _rawTemplate;

    public string Name { get; }

    public PromptTemplate(string name, string rawTemplate)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(rawTemplate);
        Name = name;
        _rawTemplate = rawTemplate;
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

        return result;
    }
}
