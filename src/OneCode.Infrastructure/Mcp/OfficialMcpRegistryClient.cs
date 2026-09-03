using OneCode.Infrastructure.Config;

namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// Client for the official MCP Registry (https://registry.modelcontextprotocol.io).
/// Backed by Anthropic/GitHub/PulseMCP/Microsoft. The read-only REST API is unauthenticated,
/// so no OAuth or API key is required — matching OneCode's "open, no-auth" registry goal.
///
/// The registry exposes server metadata that points at a package (npm/pypi/nuget/oci/mcpb)
/// or a remote endpoint. Local (stdio) servers run via <c>npx</c>/<c>uvx</c>/<c>docker</c>.
/// </summary>
public sealed class OfficialMcpRegistryClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OfficialMcpRegistryClient> _logger;
    private const string HttpClientName = Constants.HttpClientNames.McpRegistry;
    private const int PageSize = 100;

    // 全量列表缓存：/mcp search 需要跨全量做关键词过滤，而官方 registry 无服务端搜索端点，
    // 故首次拉取后按 name 去重并缓存，TTL 内复用避免每次搜索都遍历 50+ 页。
    private IReadOnlyList<OfficialRegistryServer>? _cachedServers;
    private DateTimeOffset _cacheTime;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public OfficialMcpRegistryClient(
        IHttpClientFactory httpClientFactory,
        ILogger<OfficialMcpRegistryClient>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OfficialMcpRegistryClient>.Instance;
    }

    /// <summary>
    /// Search servers by keyword (client-side filter over name/title/description).
    /// The official registry has no server-side search endpoint, so this pulls the full
    /// (cached) index and filters locally.
    /// </summary>
    public async Task<IReadOnlyList<OfficialRegistryServer>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var all = await EnsureServersAsync(ct).ConfigureAwait(false);
        if (all.Count == 0)
            return [];

        var q = query.Trim();
        return all
            .Select(s => (Server: s, Score: Score(s, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Server.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.Server)
            .ToList();
    }

    /// <summary>
    /// Get the latest published metadata for a single server by its qualified name
    /// (e.g. "agency.kesey/pretrip").
    /// </summary>
    public async Task<OfficialRegistryServer?> GetLatestAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var url = $"/v0.1/servers/{Uri.EscapeDataString(name)}/versions/latest";
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<OfficialRegistryLatestResponse>(json)?.Server;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get MCP server '{ServerName}' from official registry", name);
            return null;
        }
    }

    // Search support

    private static int Score(OfficialRegistryServer server, string query)
    {
        var name = server.Name ?? "";
        var title = server.Title ?? "";
        var description = server.Description ?? "";

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (description.Contains(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    // Full-index loading (paginated + deduplicated + cached)

    private async Task<IReadOnlyList<OfficialRegistryServer>> EnsureServersAsync(CancellationToken ct)
    {
        if (_cachedServers is not null && DateTimeOffset.UtcNow - _cacheTime < CacheTtl)
            return _cachedServers;

        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // double-checked: another caller may have populated the cache while we waited
            if (_cachedServers is not null && DateTimeOffset.UtcNow - _cacheTime < CacheTtl)
                return _cachedServers;

            var servers = await LoadAllAsync(ct).ConfigureAwait(false);
            _cachedServers = servers;
            _cacheTime = DateTimeOffset.UtcNow;
            return servers;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<IReadOnlyList<OfficialRegistryServer>> LoadAllAsync(CancellationToken ct)
    {
        var byName = new Dictionary<string, OfficialRegistryServer>(StringComparer.Ordinal);
        string? cursor = null;

        while (true)
        {
            var url = $"/v0.1/servers?limit={PageSize}"
                      + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<OfficialRegistryListResponse>(json);
            if (page?.Servers is { Count: > 0 } entries)
            {
                foreach (var entry in entries)
                {
                    var server = entry.Server;
                    if (server is null || string.IsNullOrEmpty(server.Name))
                        continue;

                    // 同一 name 有多个版本；isLatest=true 的版本胜出（覆盖先入的旧版本）。
                    if (!byName.ContainsKey(server.Name) || IsLatest(entry))
                        byName[server.Name] = server;
                }
            }

            var next = page?.Metadata?.NextCursor;
            if (string.IsNullOrEmpty(next))
                break;
            cursor = next;
        }

        return byName.Values.ToList();
    }

    private static bool IsLatest(OfficialRegistryServerEntry entry)
    {
        if (entry.Meta is not { ValueKind: JsonValueKind.Object } meta)
            return false;
        if (!meta.TryGetProperty("io.modelcontextprotocol.registry/official", out var official))
            return false;
        return official.ValueKind == JsonValueKind.Object
               && official.TryGetProperty("isLatest", out var isLatest)
               && isLatest.ValueKind == JsonValueKind.True;
    }
}

// Registry data models (match the official server.json schema)

/// <summary>A single server's metadata (name is reverse-DNS, e.g. "io.github.user/server").</summary>
public sealed class OfficialRegistryServer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("websiteUrl")]
    public string? WebsiteUrl { get; set; }

    /// <summary>Local install packages (npm/pypi/nuget/oci/mcpb) — present for stdio servers.</summary>
    [JsonPropertyName("packages")]
    public List<OfficialRegistryPackage>? Packages { get; set; }

    /// <summary>Remote endpoints (streamable-http/sse) — present for hosted servers.</summary>
    [JsonPropertyName("remotes")]
    public List<OfficialRegistryRemote>? Remotes { get; set; }
}

/// <summary>A package that can be installed and run locally over stdio.</summary>
public sealed class OfficialRegistryPackage
{
    /// <summary>npm | pypi | nuget | oci | mcpb.</summary>
    [JsonPropertyName("registryType")]
    public string RegistryType { get; set; } = "";

    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("transport")]
    public OfficialRegistryTransport? Transport { get; set; }

    /// <summary>SHA-256 of the artifact (mcpb only); clients must verify before install.</summary>
    [JsonPropertyName("fileSha256")]
    public string? FileSha256 { get; set; }
}

public sealed class OfficialRegistryTransport
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public sealed class OfficialRegistryRemote
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class OfficialRegistryServerEntry
{
    [JsonPropertyName("server")]
    public OfficialRegistryServer? Server { get; set; }

    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; set; }
}

public sealed class OfficialRegistryListResponse
{
    [JsonPropertyName("servers")]
    public List<OfficialRegistryServerEntry> Servers { get; set; } = [];

    [JsonPropertyName("metadata")]
    public OfficialRegistryListMetadata? Metadata { get; set; }
}

public sealed class OfficialRegistryListMetadata
{
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public sealed class OfficialRegistryLatestResponse
{
    [JsonPropertyName("server")]
    public OfficialRegistryServer? Server { get; set; }
}
