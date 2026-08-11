using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OneCode.Infrastructure.Skills;

/// <summary>Strictly parses the invocation-relevant subset of SKILL.md frontmatter.</summary>
public static partial class SkillFrontmatterParser
{
    [GeneratedRegex(@"\A---\s*\r?\n(?<yaml>.*?)\r?\n---\s*(?:\r?\n|\z)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    private static readonly IDeserializer s_deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static bool TryParse(string content, string fallbackName, out SkillDocument document)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackName);

        var match = FrontmatterRegex().Match(content);
        SkillFrontmatterRaw? metadata = null;
        if (content.StartsWith("---", StringComparison.Ordinal))
        {
            if (!match.Success)
            {
                document = default!;
                return false;
            }

            try
            {
                metadata = s_deserializer.Deserialize<SkillFrontmatterRaw?>(match.Groups["yaml"].Value);
            }
            catch
            {
                document = default!;
                return false;
            }
        }

        var name = string.IsNullOrWhiteSpace(metadata?.Name) ? fallbackName : metadata.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            document = default!;
            return false;
        }

        var body = (match.Success ? content[match.Length..] : content).TrimStart();
        var description = metadata?.Description?.Trim();
        if (string.IsNullOrWhiteSpace(description))
            description = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.TrimStart('#', ' ') ?? string.Empty;

        var argumentNames = (metadata?.ArgumentNames ?? [])
            .Select(static n => n.Trim())
            .Where(static n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        document = new SkillDocument(
            name,
            description,
            body,
            metadata?.ArgumentHint?.Trim(),
            argumentNames,
            metadata?.UserInvocable ?? true,
            metadata?.DisableModelInvocation ?? false);
        return true;
    }
}
