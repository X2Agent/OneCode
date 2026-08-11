using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OneCode.App.Services.Agent;

public sealed class AgentTemplateConfig
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string Template { get; set; } = "magentic-orchestrator";
    public List<WorkerTemplateConfig> Workers { get; set; } = [];
    public int MaxRounds { get; set; } = 20;

    public static AgentTemplateConfig FromYamlFile(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        return FromYaml(yaml);
    }

    /// <summary>
    /// 从嵌入式资源加载团队模板（用于内置模板）。
    /// 资源命名约定：OneCode.App.prompts.teams.{name}.yaml
    /// </summary>
    public static AgentTemplateConfig FromYamlResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded team template not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return FromYaml(reader.ReadToEnd());
    }

    public static AgentTemplateConfig FromYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<AgentTemplateConfig>(yaml);
    }
}

public sealed class WorkerTemplateConfig
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "general";
    public string Instructions { get; set; } = "";
    public List<string>? AllowedTools { get; set; }
}
