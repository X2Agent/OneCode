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

    /// <summary>
    /// 单次请求预算。官方 search 端点可用但响应慢（2026-09 实测 18~26s 返回 200），
    /// 预算必须覆盖最慢路径，否则会把"慢"误判为"失败"；默认 30s，可注入以便测试。
    /// </summary>
    private readonly TimeSpan _requestBudget;

    public OfficialMcpRegistryClient(
        IHttpClientFactory httpClientFactory,
        ILogger<OfficialMcpRegistryClient>? logger = null,
        TimeSpan? requestBudget = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OfficialMcpRegistryClient>.Instance;
        _requestBudget = requestBudget ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Search servers by keyword — 按官方规范走服务端
    /// <c>GET /v0.1/servers?search=…&amp;version=latest&amp;limit=…</c>（name 子串匹配）。
    /// 该端点官方实例响应较慢（实测 18~26s），由宽请求预算覆盖；超时/网络故障向上冒泡，
    /// 由调用方提示重试——绝不能因慢而退回全量分页扫描（历史实现曾 300+ 页卡死数分钟）。
    /// 客户端按 name/title/description 命中度排序（仅影响展示顺序，不过滤——避免丢弃
    /// 服务端命中但本地打分为 0 的条目）。
    /// </summary>
    public async Task<IReadOnlyList<OfficialRegistryServer>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        var q = query.Trim();

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_requestBudget);

        var entries = await SearchServerSideAsync(q, limit, attempt.Token).ConfigureAwait(false);
        var byName = new Dictionary<string, OfficialRegistryServer>(StringComparer.Ordinal);
        CollectEntries(byName, entries);
        return Rank(byName, q, limit);
    }

    private async Task<IReadOnlyList<OfficialRegistryServerEntry>> SearchServerSideAsync(
        string query, int limit, CancellationToken ct)
    {
        var url = $"/v0.1/servers?search={Uri.EscapeDataString(query)}&version=latest&limit={limit}";
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var page = JsonSerializer.Deserialize<OfficialRegistryListResponse>(json);
        return page?.Servers ?? [];
    }

    /// <summary>命中度排序：name > title > description；弃用的 server 排在活跃之后。
    /// 仅排序不过滤——服务端命中的条目即使本地打分为 0 也保留。</summary>
    private static List<OfficialRegistryServer> Rank(
        Dictionary<string, OfficialRegistryServer> byName,
        string query,
        int limit)
    {
        return byName.Values
            .Select(s => (Server: s, Relevance: Score(s, query)))
            .OrderByDescending(x => x.Relevance)
            .ThenBy(x => x.Server.IsDeprecated)
            .ThenBy(x => x.Server.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.Server)
            .ToList();
    }

    /// <summary>同一 name 可能返回多个版本条目：isLatest=true 的版本胜出（覆盖先入的旧版本）；
    /// deleted 条目跳过（规范 include_deleted 默认 false，此处防御）；deprecated 打标供展示。</summary>
    private static void CollectEntries(
        Dictionary<string, OfficialRegistryServer> byName,
        IEnumerable<OfficialRegistryServerEntry> entries)
    {
        foreach (var entry in entries)
        {
            var server = entry.Server;
            if (server is null || string.IsNullOrEmpty(server.Name))
                continue;

            string? status = null;
            if (TryGetOfficialMeta(entry, out var official)
                && official.TryGetProperty("status", out var statusElement)
                && statusElement.ValueKind == JsonValueKind.String)
            {
                status = statusElement.GetString();
            }

            if (status is "deleted")
                continue;
            server.IsDeprecated = status is "deprecated";

            if (!byName.TryGetValue(server.Name, out _) || IsLatest(entry))
                byName[server.Name] = server;
        }
    }

    /// <summary>
    /// Get the latest published metadata for a single server by its qualified name
    /// (e.g. "agency.kesey/pretrip"). 规范要求 path 参数整体 URL 编码
    /// （<c>com.example%2Fmy-server</c>）——实测不编码的原始斜杠返回 404。
    /// 语义约定：非成功状态 → null（未找到）；网络故障/超时 → 抛出（请求预算内的取消异常），
    /// 由调用方区分"不存在"与"registry 不可达"。
    /// </summary>
    public async Task<OfficialRegistryServer?> GetLatestAsync(string name, CancellationToken ct = default)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_requestBudget);
        var url = $"/v0.1/servers/{Uri.EscapeDataString(name)}/versions/latest";
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, attempt.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<OfficialRegistryLatestResponse>(json)?.Server;
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

    /// <summary>解包 <c>_meta["io.modelcontextprotocol.registry/official"]</c>（isLatest/status/publishedAt）。</summary>
    private static bool TryGetOfficialMeta(OfficialRegistryServerEntry entry, out JsonElement official)
    {
        official = default;
        if (entry.Meta is not { ValueKind: JsonValueKind.Object } meta)
            return false;
        return meta.TryGetProperty("io.modelcontextprotocol.registry/official", out official)
               && official.ValueKind == JsonValueKind.Object;
    }

    private static bool IsLatest(OfficialRegistryServerEntry entry)
        => TryGetOfficialMeta(entry, out var official)
           && official.TryGetProperty("isLatest", out var isLatest)
           && isLatest.ValueKind == JsonValueKind.True;
}

// Registry data models (match the official server.json schema — openapi.json ServerJSON)
// 只做展示与本地 install 决策，不回传 registry，因此统一序列化时省略 null，
// 防止把缺失的可选字段当空串回写。

/// <summary>A single server's metadata (name is reverse-DNS, e.g. "io.github.user/server").</summary>
public sealed class OfficialRegistryServer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("version"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("websiteUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebsiteUrl { get; set; }

    /// <summary>Registry lifecycle status (active/deprecated/deleted) — 已废弃时展示打标。</summary>
    [JsonIgnore]
    public bool IsDeprecated { get; internal set; }

    /// <summary>Local install packages (npm/pypi/nuget/oci/mcpb) — present for stdio servers.</summary>
    [JsonPropertyName("packages"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OfficialRegistryPackage>? Packages { get; set; }

    /// <summary>Remote endpoints (streamable-http/sse) — present for hosted servers.</summary>
    [JsonPropertyName("remotes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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

    [JsonPropertyName("version"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("transport"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OfficialRegistryTransport? Transport { get; set; }

    /// <summary>SHA-256 of the artifact (mcpb only); clients must verify before install.</summary>
    [JsonPropertyName("fileSha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileSha256 { get; set; }
}

public sealed class OfficialRegistryTransport
{
    /// <summary>stdio | streamable-http | sse。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class OfficialRegistryRemote
{
    /// <summary>streamable-http | sse。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

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
