using OneCode.Infrastructure.Config;

namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// Client for the Smithery MCP registry (https://registry.smithery.ai).
/// Provides search and server-detail lookup so users can discover and install
/// MCP servers without hand-crafting configuration.
/// </summary>
public sealed class McpRegistryClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpRegistryClient> _logger;
    private const string HttpClientName = Constants.HttpClientNames.McpRegistry;

    public McpRegistryClient(IHttpClientFactory httpClientFactory, ILogger<McpRegistryClient>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<McpRegistryClient>.Instance;
    }

    /// <summary>
    /// Search the registry for servers matching <paramref name="query"/>.
    /// </summary>
    public async Task<IReadOnlyList<RegistryServer>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"/servers?q={Uri.EscapeDataString(query)}&pageSize={limit}";
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<RegistrySearchResult>(json);
            return result?.Servers ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search MCP registry for '{Query}'", query);
            return [];
        }
    }

    /// <summary>
    /// Get full details (connections, tools, config schema) for a single server
    /// by its <paramref name="qualifiedName"/> (e.g. "jina" or "smithery-ai/github").
    /// </summary>
    public async Task<RegistryServer?> GetServerAsync(
        string qualifiedName,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"/servers/{Uri.EscapeDataString(qualifiedName)}";
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RegistryServer>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get MCP server '{ServerName}'", qualifiedName);
            return null;
        }
    }

    /// <summary>
    /// List the most popular servers (no query filter).
    /// </summary>
    public async Task<IReadOnlyList<RegistryServer>> ListAllAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"/servers?pageSize={limit}";
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<RegistrySearchResult>(json);
            return result?.Servers ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list MCP servers from registry");
            return [];
        }
    }
}

// Registry data models (match the real Smithery API response shape)

public sealed class RegistryServer
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Stable identifier used in install URLs, e.g. "jina" or "smithery-ai/github".</summary>
    [JsonPropertyName("qualifiedName")]
    public string QualifiedName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }

    /// <summary>How many times this server has been used (popularity signal).</summary>
    [JsonPropertyName("useCount")]
    public int UseCount { get; set; }

    /// <summary>true = hosted on Smithery cloud (connect via http/sse); false = runs locally (stdio).</summary>
    [JsonPropertyName("remote")]
    public bool Remote { get; set; }

    [JsonPropertyName("isDeployed")]
    public bool IsDeployed { get; set; }

    // Detail-only fields (absent from search results)

    /// <summary>Direct HTTP endpoint for remote servers (e.g. "https://jina.run.tools").</summary>
    [JsonPropertyName("deploymentUrl")]
    public string? DeploymentUrl { get; set; }

    [JsonPropertyName("connections")]
    public List<RegistryConnection>? Connections { get; set; }

    [JsonPropertyName("tools")]
    public List<RegistryTool>? Tools { get; set; }
}

public sealed class RegistryConnection
{
    /// <summary>Transport type, typically "http" for Smithery-hosted servers.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("deploymentUrl")]
    public string? DeploymentUrl { get; set; }

    /// <summary>JSON schema describing config parameters the user may need to supply.</summary>
    [JsonPropertyName("configSchema")]
    public JsonElement? ConfigSchema { get; set; }
}

public sealed class RegistryTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class RegistrySearchResult
{
    [JsonPropertyName("servers")]
    public List<RegistryServer> Servers { get; set; } = [];

    [JsonPropertyName("pagination")]
    public RegistryPagination? Pagination { get; set; }
}

public sealed class RegistryPagination
{
    [JsonPropertyName("currentPage")]
    public int CurrentPage { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}
