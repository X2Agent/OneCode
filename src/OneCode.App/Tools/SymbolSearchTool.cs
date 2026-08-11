using System.ComponentModel;
using OneCode.Core.Lsp;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Text;

namespace OneCode.App.Tools;

/// <summary>
/// SymbolSearch tool — search indexed code symbols by name.
///
/// Strategy: LSP-first, code-index fallback.
/// When <c>useLsp</c> is true (default) and at least one LSP server is
/// running, the tool first issues <c>workspace/symbol</c> on each running server
/// — this yields semantic results with proper namespace and kind discrimination
/// (no false positives from same-named symbols in different namespaces). If LSP
/// returns no results or no server is running, the tool falls back to the local
/// code index (Levenshtein-based fuzzy match over indexed source files).
/// </summary>
public sealed class SymbolSearchTool
{
    private readonly ICodeIndexService _indexService;
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly ILspServerManager _serverManager;
    private readonly ILogger<SymbolSearchTool> _logger;

    public SymbolSearchTool(
        ICodeIndexService indexService,
        IWorkingDirectoryAccessor wd,
        ILspServerManager serverManager,
        ILogger<SymbolSearchTool>? logger = null)
    {
        _indexService = indexService;
        _wd = wd;
        _serverManager = serverManager;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SymbolSearchTool>.Instance;
    }

    [Description("Search code symbols (class, method, interface etc.) by name. Uses LSP workspace/symbol when available for semantic accuracy, falling back to the local code index on failure.")]
    public async Task<ToolResult> SymbolSearchAsync(
        [Description("Symbol name to search for.")] string query,
        [Description("Optional symbol kind: class, interface, struct, enum, method, etc.")] string? kind = null,
        [Description("Optional directory/file path scope.")] string? path = null,
        [Description("Max results (default 20, max 100).")] int maxResults = 20,
        [Description("When true (default), try LSP workspace/symbol first and fall back to the code index on failure.")] bool useLsp = true)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Error("'query' is required.");

        maxResults = Math.Clamp(maxResults, 1, Constants.Lsp.MaxResultsUpper);

        // LSP-first path
        // workspace/symbol returns SymbolInformation objects with proper namespace
        // and kind discrimination. Merge results across all running servers.
        if (useLsp && _serverManager is not null)
        {
            try
            {
                var lspResults = await TrySearchViaLspAsync(query, kind, maxResults).ConfigureAwait(false);
                if (lspResults.Count > 0)
                {
                    return ToolResult.Success(FormatLspResults(query, kind, lspResults));
                }
            }
            catch (Exception ex)
            {
                // LSP path failed — fall through to code index. Non-fatal.
                _logger.LogDebug(ex, "LSP workspace/symbol path failed for query {Query}, falling back to code index", query);
            }
        }

        // Code-index fallback
        if (_indexService is null)
            return ToolResult.Error("SymbolSearch unavailable: code index service not configured and no LSP server is running.");

        if (!_indexService.LastIndexedAt.HasValue && !_indexService.IsIndexing)
            return ToolResult.Error("Code index is still building. Please retry in a moment, or ensure an LSP server is running for semantic search.");

        var pathScope = path;
        if (!string.IsNullOrEmpty(pathScope) && !Path.IsPathRooted(pathScope))
            pathScope = Path.GetFullPath(Path.Combine(_wd.WorkingDirectory, pathScope));

        var matches = _indexService.Search(query, maxResults, kind, pathScope);
        if (matches.Count == 0)
        {
            var filterDesc = BuildFilter(kind, pathScope);
            return ToolResult.Success($"No symbols found matching \"{query}\"{filterDesc}.");
        }

        var results = matches.Select(m => new
        {
            name = m.Symbol.Name,
            kind = m.Symbol.Kind,
            file = m.Symbol.FilePath,
            line = m.Symbol.Line,
            column = m.Symbol.Column,
            score = Math.Round(m.RelevanceScore, 2),
            source = "index"
        }).ToList();

        var summary = $"Found {results.Count} result(s) for \"{query}\"" +
            (string.IsNullOrEmpty(kind) ? "" : $" [{kind}]") +
            (string.IsNullOrEmpty(pathScope) ? "" : $" in {pathScope}");

        // 保留自定义序列化选项（缩进 + camelCase），不走 JsonSuccess 默认选项。
        return ToolResult.Success(JsonSerializer.Serialize(new { summary, results }, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }

    /// <summary>
    /// Issue workspace/symbol on every running LSP server and merge the results.
    /// Returns an empty list when no server is initialized or no results come back,
    /// signalling the caller to fall back to the code index.
    /// </summary>
    private async Task<List<LspSymbolResult>> TrySearchViaLspAsync(string query, string? kind, int maxResults)
    {
        var status = _serverManager!.GetStatus();
        if (status.Count == 0)
            return [];

        var @params = JsonSerializer.SerializeToElement(new { query });
        var merged = new List<LspSymbolResult>();

        foreach (var s in status)
        {
            if (!s.IsInitialized) continue;
            try
            {
                var result = await _serverManager.SendRequestAsync(s.Name, "workspace/symbol", @params).ConfigureAwait(false);
                if (result is not { } el || el.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in el.EnumerateArray())
                {
                    var parsed = ParseSymbolInformation(item, s.Name);
                    if (parsed is null) continue;

                    // Apply optional kind filter (case-insensitive substring match
                    // on the LSP SymbolKind name, e.g. "class" matches Class).
                    if (!string.IsNullOrEmpty(kind) &&
                        !parsed.Kind.Contains(kind, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    merged.Add(parsed);
                }
            }
            catch (Exception ex)
            {
                // One server failing should not abort the whole query — continue.
                _logger.LogDebug(ex, "workspace/symbol failed on server {ServerName}, continuing", s.Name);
            }
        }

        return merged.Take(maxResults).ToList();
    }

    /// <summary>
    /// Parse a SymbolInformation / DocumentSymbol JSON object into a normalized
    /// LspSymbolResult. Tolerates both legacy and hierarchical shapes.
    /// </summary>
    private static LspSymbolResult? ParseSymbolInformation(JsonElement item, string serverName)
    {
        string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(name))
            return null;

        // LSP SymbolKind is an integer 1..26 — map to a friendly name.
        var kindNum = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Number
            ? k.GetInt32()
            : 0;
        var kindName = SymbolKindName(kindNum);

        // Location may be at top-level (SymbolInformation) or nested (DocumentSymbol).
        string file = "";
        int line = 0, column = 0;
        if (item.TryGetProperty("location", out var loc))
        {
            if (loc.TryGetProperty("uri", out var uri))
                file = UriToFilePath(uri.GetString() ?? "");
            if (loc.TryGetProperty("range", out var range) &&
                range.TryGetProperty("start", out var start))
            {
                line = (start.TryGetProperty("line", out var l) ? l.GetInt32() : 0) + 1;
                column = (start.TryGetProperty("character", out var c) ? c.GetInt32() : 0) + 1;
            }
        }
        else if (item.TryGetProperty("uri", out var uri))
        {
            // Some servers use a flat shape with uri/range at top-level.
            file = UriToFilePath(uri.GetString() ?? "");
            if (item.TryGetProperty("range", out var range) &&
                range.TryGetProperty("start", out var start))
            {
                line = (start.TryGetProperty("line", out var l) ? l.GetInt32() : 0) + 1;
                column = (start.TryGetProperty("character", out var c) ? c.GetInt32() : 0) + 1;
            }
        }

        // Optional containerName for namespace context
        var containerName = item.TryGetProperty("containerName", out var cn) ? cn.GetString() : null;

        return new LspSymbolResult(name, kindName, file, line, column, containerName, serverName);
    }

    private static string SymbolKindName(int kind) => kind switch
    {
        1 => "File",
        2 => "Module",
        3 => "Namespace",
        4 => "Package",
        5 => "Class",
        6 => "Method",
        7 => "Property",
        8 => "Field",
        9 => "Constructor",
        10 => "Enum",
        11 => "Interface",
        12 => "Function",
        13 => "Variable",
        14 => "Constant",
        15 => "String",
        16 => "Number",
        17 => "Boolean",
        18 => "Array",
        19 => "Object",
        20 => "Key",
        21 => "Null",
        22 => "EnumMember",
        23 => "Struct",
        24 => "Event",
        25 => "Operator",
        26 => "TypeParameter",
        _ => $"Kind{kind}"
    };

    private static string UriToFilePath(string uri) => LspUriHelper.UriToFilePath(uri);

    private static string FormatLspResults(string query, string? kind, List<LspSymbolResult> results)
    {
        var summary = $"[LSP] Found {results.Count} symbol(s) for \"{query}\"" +
            (string.IsNullOrEmpty(kind) ? "" : $" [{kind}]");

        var list = results.Select(r => new
        {
            name = r.Name,
            kind = r.Kind,
            container = r.ContainerName ?? "",
            file = r.File,
            line = r.Line,
            column = r.Column,
            server = r.ServerName,
            source = "lsp"
        }).ToList();

        return JsonSerializer.Serialize(new { summary, results = list }, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    private static string BuildFilter(string? kind, string? pathScope)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(kind)) parts.Add($" of kind \"{kind}\"");
        if (!string.IsNullOrEmpty(pathScope)) parts.Add($" under \"{pathScope}\"");
        return string.Concat(parts);
    }

    /// <summary>Result row for an LSP-resolved symbol.</summary>
    private sealed record LspSymbolResult(
        string Name, string Kind, string File, int Line, int Column,
        string? ContainerName, string ServerName);
}
