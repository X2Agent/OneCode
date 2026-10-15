using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// 一个自定义子代理定义（<c>.onecode/agents/*.md</c>：frontmatter 元数据 + 正文 instructions）。
/// </summary>
public sealed record AgentDefinition(
    string Name,
    string Description,
    string Instructions,
    IReadOnlyList<string> AllowedTools,
    string SourcePath,
    // frontmatter max_turns：该代理的轮数上限（派工点经 AgentRunRequest.MaxTurnsCeiling 透传）。
    int? MaxTurns = null);

/// <summary>自定义代理定义解析失败。消息自带文件路径与原因（含 YAML 行号），由派工入口显式报错。</summary>
public sealed class AgentDefinitionParseException(string sourcePath, string reason, Exception? innerException = null)
    : Exception($"Agent definition '{sourcePath}': {reason}", innerException)
{
    public string SourcePath { get; } = sourcePath;
}

/// <summary>
/// 严格解析 <c>.onecode/agents/*.md</c>：未知 frontmatter 字段、缺失 description、坏 YAML
/// 一律显式报错——未知字段不静默忽略（历史教训：静默忽略让配置错误长期潜伏）。
/// </summary>
public static partial class AgentDefinitionParser
{
    [GeneratedRegex(@"\A---\s*\r?\n(?<yaml>.*?)\r?\n---\s*(?:\r?\n|\z)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    private static readonly IDeserializer s_deserializer = new DeserializerBuilder()
        // UnderscoredNamingConvention：frontmatter 字段 allowed_tools（对齐 agents 定义文件的生态惯例）
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static AgentDefinition Parse(string content, string sourcePath)
    {
        var match = FrontmatterRegex().Match(content);
        if (!match.Success)
            throw new AgentDefinitionParseException(sourcePath, "missing '---' frontmatter block.");

        AgentFrontmatter? metadata;
        try
        {
            metadata = s_deserializer.Deserialize<AgentFrontmatter?>(match.Groups["yaml"].Value);
            if (metadata is null)
                throw new AgentDefinitionParseException(sourcePath, "frontmatter is empty.");
        }
        catch (AgentDefinitionParseException)
        {
            throw;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new AgentDefinitionParseException(
                sourcePath, $"invalid YAML at line {ex.Start.Line}: {ex.Message}", ex);
        }

        var name = metadata.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            name = Path.GetFileNameWithoutExtension(sourcePath);

        var description = metadata.Description?.Trim();
        if (string.IsNullOrEmpty(description))
            throw new AgentDefinitionParseException(
                sourcePath, "frontmatter 'description' is required — it is what makes the agent discoverable.");

        var body = content[match.Length..].Trim();
        if (string.IsNullOrEmpty(body))
            throw new AgentDefinitionParseException(
                sourcePath, "instructions body is empty — add instructions below the frontmatter.");

        var allowedTools = (metadata.AllowedTools ?? [])
            .Select(static t => t.Trim())
            .Where(static t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AgentDefinition(name, description, body, allowedTools, sourcePath, NormalizeMaxTurns(metadata.MaxTurns, sourcePath));
    }

    /// <summary>max_turns 非法值（≤0 或溢出）显式报错——静默取默认会让误配置的代理失控。</summary>
    private static int? NormalizeMaxTurns(int? maxTurns, string sourcePath)
    {
        if (maxTurns is null)
            return null;
        if (maxTurns is <= 0)
            throw new AgentDefinitionParseException(sourcePath, "frontmatter 'max_turns' must be a positive integer.");
        return maxTurns;
    }

    internal sealed class AgentFrontmatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string[]? AllowedTools { get; set; }
        public int? MaxTurns { get; set; }
    }
}
