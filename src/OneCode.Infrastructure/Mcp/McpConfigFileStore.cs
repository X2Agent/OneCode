namespace OneCode.Infrastructure.Mcp;

/// <summary>JSON config-file entry for a single MCP server (distinct from the runtime McpServerDefinition).</summary>
public sealed record McpConfigEntry(
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("command")] string? Command = null,
    [property: JsonPropertyName("args")] string[]? Args = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env = null,
    [property: JsonPropertyName("disabled")] bool? Disabled = null);

/// <summary>Root object of MCP config files (user-scope .mcp.json, project-scope .mcp.json).</summary>
public sealed class McpConfigFile
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpConfigEntry> McpServers { get; set; } = new();
}

/// <summary>
/// .mcp.json 配置文件的加载/保存/路径解析。project 作用域为工作目录下的 .mcp.json，
/// user 作用域复用 <see cref="McpMultiScopeConfigLoader.GetUserConfigPath"/> 的路径约定。
/// </summary>
public static class McpConfigFileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>解析作用域（project/user）为配置文件路径。</summary>
    public static string GetPath(string scope) =>
        scope == "project"
            ? Path.Combine(Directory.GetCurrentDirectory(), ".mcp.json")
            : McpMultiScopeConfigLoader.GetUserConfigPath();

    /// <summary>
    /// 加载配置文件。文件不存在时返回 null；
    /// 文件存在但损坏/不可读时抛出 <see cref="InvalidOperationException"/>——
    /// 绝不按"不存在"处理，防止调用方随后的 Save 静默覆盖用户原有配置。
    /// </summary>
    public static McpConfigFile? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<McpConfigFile>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidOperationException(
                $"MCP config file '{path}' exists but could not be read (corrupted or locked). "
                + "Fix or delete it before modifying MCP servers.",
                ex);
        }
    }

    public static async Task SaveAsync(string path, McpConfigFile config, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }
}
