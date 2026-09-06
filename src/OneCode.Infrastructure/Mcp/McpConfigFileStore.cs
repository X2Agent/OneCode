namespace OneCode.Infrastructure.Mcp;

/// <summary>JSON config-file entry for a single MCP server (distinct from the runtime McpServerDefinition).</summary>
public sealed record McpConfigEntry(
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("command")] string? Command = null,
    [property: JsonPropertyName("args")] string[]? Args = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env = null,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers = null,
    [property: JsonPropertyName("disabled")] bool? Disabled = null,
    [property: JsonPropertyName("initTimeoutMs")] int? InitTimeoutMs = null,
    [property: JsonPropertyName("startupTimeoutMs")] int? StartupTimeoutMs = null,
    // 工具白名单（支持 * 通配符）。null = 未配置（全部暴露）；空数组 = 不暴露任何工具。
    [property: JsonPropertyName("tools")] string[]? Tools = null);

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

    /// <summary>解析作用域（project/user）为配置文件路径。project 作用域复用
    /// <see cref="McpMultiScopeConfigLoader.FindProjectConfigPath"/> 的读取解析
    /// （从工作目录向上找最近的 .mcp.json，.git 边界止步），未找到时回落工作目录——
    /// 使写路径与多作用域读取路径一致，避免"读到上级配置、写当前目录新文件"的配置分裂。</summary>
    public static string GetPath(string scope, string? workingDirectory = null) =>
        scope == "project"
            ? McpMultiScopeConfigLoader.FindProjectConfigPath(workingDirectory)
              ?? Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), ".mcp.json")
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

        // 目标目录不存在时先创建（首次写 project/user 配置时父目录可能尚未建立）。
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // 原子写：先写同目录唯一临时文件再替换目标。直接 WriteAllAsync 在进程崩溃/
        // 断电时会留下半写的 .mcp.json，而 Load 对损坏文件抛 InvalidOperationException
        //（防覆盖保护），届时所有配置修改都会被拒绝——必须从写入侧杜绝半写状态。
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
