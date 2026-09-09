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
