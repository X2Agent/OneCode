using System.Text;
using System.Text.RegularExpressions;
using OneCode.Infrastructure.Search;
using OneCode.Core.Results;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Text;

namespace OneCode.App.Tools;

/// <summary>
/// LspTool 的符号级操作 partial：workspaceSymbol（LSP 优先、代码索引回退）与
/// 按符号名查引用（workspaceSymbol 定位声明 → textDocument/references → 词边界文本搜索兜底）。
/// </summary>
public sealed partial class LspTool
{
    /// <summary>workspaceSymbol 索引回退的最大返回数（与原符号搜索工具的上限一致）。</summary>
    private const int MaxSymbolResults = Constants.Lsp.MaxResultsUpper;

    /// <summary>
    /// workspaceSymbol：在所有已初始化服务器上执行 workspace/symbol 并合并结果；
    /// 无服务器或无结果时回退到本地代码索引（模糊匹配，path 过滤）。
    /// </summary>
    private async Task<ToolResult> ExecuteWorkspaceSymbolAsync(string query, string? path, CancellationToken ct)
    {
        // Resolve the path scope once, up front, so the LSP and index paths filter on the same value.
        string? pathScope = null;
        if (!string.IsNullOrEmpty(path))
        {
            var resolvedScope = ResolveWorkspacePath(path);
            if (!resolvedScope.IsSuccess)
                return ToolResult.Error(resolvedScope.Error ?? "Path resolution failed.");
            pathScope = resolvedScope.Value;
        }

        // LSP-first path: merge SymbolInformation across all running servers.
        if (_serverManager is not null)
        {
            try
            {
                var lspResults = await TrySearchViaLspAsync(query, pathScope, ct).ConfigureAwait(false);
                if (lspResults.Count > 0)
                {
                    return ToolResult.JsonSuccess(FormatSymbolResults("[LSP] " + BuildSymbolSummary(query, lspResults.Count, pathScope), lspResults, "lsp"));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // LSP path failed — fall through to code index. Non-fatal.
                _logger?.LogDebug(ex, "LSP workspace/symbol path failed for query {Query}, falling back to code index", query);
            }
        }

        // Code-index fallback
        if (_indexService is null)
            return ToolResult.Error("Symbol search unavailable: code index service not configured and no LSP server is running.");

        if (!_indexService.LastIndexedAt.HasValue && !_indexService.IsIndexing)
            return ToolResult.Error("Code index is still building. Please retry in a moment, or ensure an LSP server is running for semantic search.");

        var matches = _indexService.Search(query, MaxSymbolResults, null, pathScope);
        if (matches.Count == 0)
            return ToolResult.Success($"No symbols found matching \"{query}\"{(string.IsNullOrEmpty(pathScope) ? "" : $" in {pathScope}")}.");

        var rows = matches.Select(m => new
        {
            name = m.Symbol.Name,
            kind = m.Symbol.Kind,
            file = m.Symbol.FilePath,
            line = m.Symbol.Line,
            column = m.Symbol.Column,
            score = Math.Round(m.RelevanceScore, 2),
            source = "index"
        }).ToList();

        return ToolResult.JsonSuccess(new
        {
            summary = BuildSymbolSummary(query, rows.Count, pathScope),
            results = (object)rows,
        });
    }

    private static string BuildSymbolSummary(string query, int count, string? pathScope) =>
        $"Found {count} symbol(s) for \"{query}\"" + (string.IsNullOrEmpty(pathScope) ? "" : $" in {pathScope}");

    /// <summary>
    /// Issue workspace/symbol on every running LSP server and merge the results.
    /// Returns an empty list when no server is initialized or no results come back,
    /// signalling the caller to fall back to the code index.
    /// </summary>
    private async Task<List<SymbolRow>> TrySearchViaLspAsync(string query, string? pathScope, CancellationToken ct)
    {
        var status = _serverManager.GetStatus();
        if (status.Count == 0)
            return [];

        var @params = JsonSerializer.SerializeToElement(new { query });
        List<SymbolRow> merged = [];

        foreach (var s in status)
        {
            ct.ThrowIfCancellationRequested();

            if (!s.IsInitialized) continue;
            try
            {
                var result = await _serverManager.SendRequestAsync(s.Name, "workspace/symbol", @params, ct).ConfigureAwait(false);
                if (result is not { } el || el.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in el.EnumerateArray())
                {
                    var parsed = ParseSymbolInformation(item, s.Name);
                    if (parsed is null) continue;

                    if (!IsWithinPathScope(parsed.File, pathScope))
                        continue;

                    merged.Add(parsed);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One server failing should not abort the whole query — continue.
                _logger?.LogDebug(ex, "workspace/symbol failed on server {ServerName}, continuing", s.Name);
            }
        }

        return merged.Take(MaxSymbolResults).ToList();
    }

    /// <summary>
    /// Filters <b>before</b> truncation so a scoped search cannot be crowded out by out-of-scope hits.
    /// </summary>
    private static bool IsWithinPathScope(string filePath, string? pathScope)
    {
        if (string.IsNullOrEmpty(pathScope) || string.IsNullOrEmpty(filePath))
            return true;

        var normalizedFile = Path.GetFullPath(filePath);
        var normalizedScope = Path.GetFullPath(pathScope);

        return normalizedFile.Equals(normalizedScope, StringComparison.OrdinalIgnoreCase)
            || normalizedFile.StartsWith(
                normalizedScope.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse a SymbolInformation / DocumentSymbol JSON object into a normalized
    /// symbol row. Tolerates both legacy and hierarchical shapes.
    /// </summary>
    private static SymbolRow? ParseSymbolInformation(JsonElement item, string serverName)
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
            (file, line, column) = ExtractLocation(loc);
        }
        else if (item.TryGetProperty("uri", out _))
        {
            // Some servers use a flat shape with uri/range at top-level.
            (file, line, column) = ExtractLocation(item);
        }

        // Optional containerName for namespace context
        var containerName = item.TryGetProperty("containerName", out var cn) ? cn.GetString() : null;

        return new SymbolRow(name, kindName, file, line, column, containerName, serverName);
    }

    private static (string File, int Line, int Column) ExtractLocation(JsonElement element)
    {
        string file = "";
        int line = 0, column = 0;
        if (element.TryGetProperty("uri", out var uri))
            file = LspUriHelper.UriToFilePath(uri.GetString() ?? "");
        if (element.TryGetProperty("range", out var range) &&
            range.TryGetProperty("start", out var start))
        {
            line = (start.TryGetProperty("line", out var l) ? l.GetInt32() : 0) + 1;
            column = (start.TryGetProperty("character", out var c) ? c.GetInt32() : 0) + 1;
        }
        return (file, line, column);
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

    private Result<string> ResolveWorkspacePath(string path)
    {
        var baseDir = string.IsNullOrWhiteSpace(_wd.WorkingDirectory)
            ? Environment.CurrentDirectory
            : _wd.WorkingDirectory;
        return PathsHelper.SafeResolve(path, baseDir, _wd.AdditionalDirectories);
    }

    private static object FormatSymbolResults(string summary, List<SymbolRow> results, string source) => new
    {
        summary,
        results = results.Select(r => new
        {
            name = r.Name,
            kind = r.Kind,
            container = r.ContainerName ?? "",
            file = r.File,
            line = r.Line,
            column = r.Column,
            server = r.ServerName,
            source,
        }).ToList(),
    };

    /// <summary>
    /// 按符号名查引用：定位声明（workspaceSymbol → 代码索引）后
    /// 走 textDocument/references；LSP 不可用或无结果时退回词边界文本搜索。
    /// </summary>
    private async Task<ToolResult> ExecuteReferencesBySymbolAsync(
        string symbol, string? path, string? glob, string? excludeGlob, bool exactWord, int maxResults, CancellationToken ct)
    {
        var resolveResult = ResolveWorkspacePath(path ?? ".");
        if (!resolveResult.IsSuccess)
            return ToolResult.Error(resolveResult.Error ?? "Path resolution failed");
        var searchPath = resolveResult.Value;
        var workingDir = string.IsNullOrWhiteSpace(_wd.WorkingDirectory)
            ? Environment.CurrentDirectory
            : _wd.WorkingDirectory;

        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
            return ToolResult.Error($"Path does not exist: {path}");

        // LSP-first path
        try
        {
            var refs = await TryFindReferencesViaLspAsync(symbol, ct).ConfigureAwait(false);
            if (refs is { Count: > 0 })
            {
                return ToolResult.Success(FormatLspReferences(symbol, refs, maxResults));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "LSP references path failed for symbol {Symbol}, falling back to text search", symbol);
        }

        // Text-search fallback: word-boundary anchored by default.
        var escapedSymbol = Regex.Escape(symbol);
        var pattern = exactWord ? $@"\b{escapedSymbol}\b" : escapedSymbol;

        var results = await _textSearch.SearchAsync(
            new TextSearchRequest(searchPath, pattern, glob, excludeGlob,
                OutputMode: "content", WorkspaceRoot: workingDir),
            ct).ConfigureAwait(false);

        if (results.Count == 0)
            return ToolResult.Success($"No references found for '{symbol}'");

        var truncated = results.Count > maxResults;
        var limited = truncated ? results.Take(maxResults).ToList() : results.ToList();

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Found {results.Count} reference{(results.Count == 1 ? "" : "s")} to '{symbol}':");
        if (truncated) sb.AppendLine(CultureInfo.InvariantCulture, $"[Showing first {maxResults} of {results.Count}. Use max_results or path to narrow search.]");
        sb.Append(string.Join("\n", limited));
        return ToolResult.Success(sb.ToString());
    }

    /// <summary>
    /// Locate the declaration site, resolve the owning server, then request references.
    /// Returns null when any step fails, signalling the caller to fall back to text search.
    /// </summary>
    private async Task<List<ReferenceRow>?> TryFindReferencesViaLspAsync(string symbol, CancellationToken ct)
    {
        if (_serverManager is null || _packRegistry is null)
            return null;

        var status = _serverManager.GetStatus();
        if (!status.Any(s => s.IsInitialized))
            return null;

        var declaration = await FindDeclarationAsync(symbol, ct).ConfigureAwait(false);
        if (declaration is null)
            return null;

        var serverName = _packRegistry.ResolveServerName(declaration.File) ?? status.FirstOrDefault(s => s.IsInitialized)?.Name;
        if (string.IsNullOrEmpty(serverName))
            return null;

        var uri = LspUriHelper.BuildFileUri(declaration.File);
        var @params = JsonSerializer.SerializeToElement(new
        {
            textDocument = new { uri },
            position = new { line = declaration.Line - 1, character = declaration.Column - 1 },
            context = new { includeDeclaration = true }
        });

        JsonElement? response;
        try
        {
            response = await _serverManager.SendRequestAsync(serverName, "textDocument/references", @params, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "LSP textDocument/references failed for server {ServerName}", serverName);
            return null;
        }

        if (response is not { } el)
            return null;

        return await ParseLocationArrayAsync(el, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Locate a declaration site for the symbol: LSP workspaceSymbol first (semantic),
    /// then the code index.
    /// </summary>
    private async Task<DeclarationSite?> FindDeclarationAsync(string symbol, CancellationToken ct)
    {
        // 1. LSP workspaceSymbol: exact-name match wins; locations are 0-based.
        var wsRows = await TrySearchViaLspAsync(symbol, pathScope: null, ct).ConfigureAwait(false);
        var wsMatch = wsRows.FirstOrDefault(r => string.Equals(r.Name, symbol, StringComparison.Ordinal));
        if (wsMatch is not null && !string.IsNullOrEmpty(wsMatch.File))
        {
            return new DeclarationSite(wsMatch.File, wsMatch.Line, wsMatch.Column);
        }

        // 2. Code index
        if (_indexService is { } index && index.LastIndexedAt.HasValue)
        {
            try
            {
                var matches = index.Search(symbol, maxResults: Constants.Lsp.DeclarationSearchMax);
                var declaration = matches
                    .OrderByDescending(m => m.RelevanceScore)
                    .FirstOrDefault(m =>
                        string.Equals(m.Symbol.Name, symbol, StringComparison.Ordinal));

                if (declaration is { } m)
                {
                    return new DeclarationSite(m.Symbol.FilePath, m.Symbol.Line, m.Symbol.Column);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Code index search failed for symbol {Symbol}", symbol);
            }
        }

        return null;
    }

    private async Task<List<ReferenceRow>> ParseLocationArrayAsync(JsonElement el, CancellationToken ct)
    {
        List<ReferenceRow> result = [];

        IEnumerable<JsonElement> elements = el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray().Select(e => e)
            : new[] { el };

        var lineCache = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in elements)
        {
            if (!item.TryGetProperty("uri", out var uriEl))
                continue;

            var uri = uriEl.GetString() ?? "";
            var filePath = LspUriHelper.UriToFilePath(uri);

            string? lineText = null;
            int line = 0, column = 0;
            if (item.TryGetProperty("range", out var range) &&
                range.TryGetProperty("start", out var start))
            {
                if (start.TryGetProperty("line", out var lEl))
                    line = lEl.GetInt32() + 1;
                if (start.TryGetProperty("character", out var cEl))
                    column = cEl.GetInt32() + 1;
            }

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                if (!lineCache.TryGetValue(filePath, out var lines))
                {
                    try
                    {
                        lines = await File.ReadAllLinesAsync(filePath, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to read source line from {FilePath}", filePath);
                        lines = null;
                    }
                    lineCache[filePath] = lines;
                }
                if (lines is not null && line > 0 && line <= lines.Length)
                    lineText = lines[line - 1];
            }

            result.Add(new ReferenceRow(filePath, line, column, lineText ?? ""));
        }

        return result;
    }

    private static string FormatLspReferences(string symbol, List<ReferenceRow> refs, int maxResults)
    {
        var truncated = refs.Count > maxResults;
        var limited = truncated ? refs.Take(maxResults).ToList() : refs;
        var note = truncated ? $"[Showing first {maxResults} of {refs.Count}. Use max_results to widen.]" : "";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"[LSP] Found {refs.Count} reference{(refs.Count == 1 ? "" : "s")} to '{symbol}':");
        if (!string.IsNullOrEmpty(note)) sb.AppendLine(note);
        foreach (var r in limited)
        {
            var display = string.IsNullOrEmpty(r.LineText)
                ? $"{r.File}:{r.Line}:{r.Column}"
                : $"{r.File}:{r.Line}:{r.Column}: {r.LineText.Trim()}";
            sb.AppendLine(display);
        }
        return sb.ToString();
    }

    /// <summary>Result row for an LSP-resolved symbol.</summary>
    private sealed record SymbolRow(
        string Name, string Kind, string File, int Line, int Column,
        string? ContainerName, string ServerName);

    /// <summary>Result row for an LSP-resolved reference location.</summary>
    private sealed record ReferenceRow(string File, int Line, int Column, string LineText);

    /// <summary>A located declaration site used to seed an LSP references request.</summary>
    private sealed record DeclarationSite(string File, int Line, int Column);
}
