using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using OneCode.App.Services.Lsp;
using OneCode.Core.Lsp;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Text;
using Microsoft.Extensions.FileSystemGlobbing;

namespace OneCode.App.Tools;

/// <summary>
/// Finds all references / usages of a symbol across the codebase.
///
/// Strategy: LSP-first, text-search fallback.
/// When <c>useLsp</c> is true (default) and a declaration site can be
/// located via the code index, the tool delegates to <c>textDocument/references</c>
/// on the matching language server — this yields semantic results (no false
/// positives from comments, strings, or shadowed identifiers). If LSP is
/// unavailable, uninitialized, or returns no results, the tool transparently
/// falls back to the original ripgrep/native word-boundary text search.
/// </summary>
public sealed class FindReferencesTool
{
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fileSystem;
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly ILspServerManager _serverManager;
    private readonly LanguagePackRegistry _packRegistry;
    private readonly ICodeIndexService _indexService;
    private readonly ILogger<FindReferencesTool> _logger;

    public FindReferencesTool(
        IProcessRunner processRunner,
        IFileSystem fileSystem,
        IWorkingDirectoryAccessor wd,
        ILspServerManager serverManager,
        LanguagePackRegistry packRegistry,
        ICodeIndexService indexService,
        ILogger<FindReferencesTool>? logger = null)
    {
        _processRunner = processRunner;
        _fileSystem = fileSystem;
        _wd = wd;
        _serverManager = serverManager;
        _packRegistry = packRegistry;
        _indexService = indexService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FindReferencesTool>.Instance;
    }

    [Description("Find all usages/references of a symbol across the codebase. Uses LSP textDocument/references when available for semantic accuracy, falling back to ripgrep word-boundary search otherwise.")]
    public async Task<ToolResult> FindAsync(
        [Description("The symbol name to find references for (function, class, variable, etc.)")] string symbol,
        [Description("Directory or file to search in (defaults to working directory)")] string? path = null,
        [Description("Glob pattern to filter files (e.g. *.cs,*.tsx)")] string? glob = null,
        [Description("Glob patterns to exclude (comma-separated, e.g. *.Tests,**/bin/**)")] string? exclude_glob = null,
        [Description("If true (default), wrap symbol in word-boundary anchors. Set false to find substring matches.")] bool exactWord = true,
        [Description("Maximum number of results to return (default 200)")] int max_results = 200,
        [Description("When true (default), try LSP textDocument/references first and fall back to text search on failure.")] bool useLsp = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return ToolResult.Error("symbol is required");
        }

        var workingDir = _wd.WorkingDirectory;
        var resolveResult = PathsHelper.SafeResolve(path ?? ".", workingDir, _wd.AdditionalDirectories);
        if (!resolveResult.IsSuccess)
            return ToolResult.Error(resolveResult.Error);
        var searchPath = resolveResult.Value;

        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
            return ToolResult.Error($"Path does not exist: {path}");

        // LSP-first path
        // Try semantic lookup via textDocument/references. Requires:
        //   (a) a server manager and pack registry are wired in,
        //   (b) at least one LSP server is running and initialized,
        //   (c) a declaration site can be located for the symbol (via the code
        //       index when available, or via a best-effort file scan otherwise).
        // Any failure here is non-fatal — we fall through to the text search.
        if (useLsp)
        {
            try
            {
                var lspResult = await TryFindViaLspAsync(symbol, searchPath, ct).ConfigureAwait(false);
                if (lspResult is { } refs && refs.Count > 0)
                {
                    return ToolResult.Success(FormatLspResults(symbol, refs, max_results));
                }
            }
            catch (Exception ex)
            {
                // LSP path failed for any reason — fall through to text search.
                // Errors are not surfaced to the user because the fallback is
                // authoritative when LSP is unavailable.
                _logger.LogDebug(ex, "LSP references path failed for symbol {Symbol}, falling back to text search", symbol);
            }
        }

        // Text-search fallback
        var escapedSymbol = Regex.Escape(symbol);
        var pattern = exactWord ? $@"\b{escapedSymbol}\b" : escapedSymbol;

        var results = await SearchAsync(searchPath, pattern, glob, exclude_glob, ct);

        if (results.Count == 0)
            return ToolResult.Success($"No references found for '{symbol}'");

        var truncated = results.Count > max_results;
        var limited = truncated ? results.Take(max_results).ToList() : results;

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Found {results.Count} reference{(results.Count == 1 ? "" : "s")} to '{symbol}':");
        if (truncated) sb.AppendLine(CultureInfo.InvariantCulture, $"[Showing first {max_results} of {results.Count}. Use max_results or path to narrow search.]");
        sb.Append(string.Join("\n", limited));
        return ToolResult.Success(sb.ToString());
    }

    /// <summary>
    /// Resolve references via LSP. Returns null when LSP cannot serve the request
    /// (no server, no declaration site found, or server returns no results) so the
    /// caller knows to fall back to text search.
    /// </summary>
    private async Task<List<LspReferenceLocation>?> TryFindViaLspAsync(string symbol, string searchPath, CancellationToken ct)
    {
        if (_serverManager is null || _packRegistry is null)
            return null;

        var status = _serverManager.GetStatus();
        if (status.Count == 0)
            return null;

        // Locate a declaration site (file, line, column) so we can issue a
        // position-based textDocument/references request. Prefer the code index
        // when available; otherwise scan files in searchPath for the first match.
        var declaration = await FindDeclarationSiteAsync(symbol, searchPath, ct).ConfigureAwait(false);
        if (declaration is null)
            return null;

        var serverName = _packRegistry.ResolveServerName(declaration.File) ?? status.FirstOrDefault(s => s.IsInitialized)?.Name;
        if (string.IsNullOrEmpty(serverName))
            return null;

        var uri = BuildFileUri(declaration.File);
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
        catch (Exception ex)
        {
            // Server errors, timeouts, or method-not-supported all fall through to text search.
            _logger.LogDebug(ex, "LSP textDocument/references failed for server {ServerName}", serverName);
            return null;
        }

        if (response is not { } el)
            return null;

        return await ParseLocationArrayAsync(el, searchPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Locate a declaration site for the given symbol. Uses the code index when
    /// available (semantic-aware); otherwise scans files in searchPath for the
    /// first line containing the symbol as a word-boundary match.
    /// </summary>
    private async Task<DeclarationSite?> FindDeclarationSiteAsync(string symbol, string searchPath, CancellationToken ct)
    {
        // Preferred path: code index knows about declaration kinds and line numbers.
        if (_indexService is { } index && index.LastIndexedAt.HasValue)
        {
            try
            {
                var matches = index.Search(symbol, maxResults: Constants.Lsp.DeclarationSearchMax);
                // Prefer class/method/interface/enum declarations over references.
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
                _logger.LogDebug(ex, "Code index search failed for symbol {Symbol}, falling through to file scan", symbol);
            }
        }

        // Fallback: scan files in searchPath for first word-boundary match.
        // We only need one declaration site to seed the LSP references request.
        if (Directory.Exists(searchPath))
        {
            var escapedSymbol = Regex.Escape(symbol);
            var pattern = new Regex($@"\b{escapedSymbol}\b", RegexOptions.Compiled);
            var defaultExcludes = new[] { ".git", ".svn", "node_modules", "bin", "obj" };
            var files = _fileSystem.FindFiles(searchPath, null, defaultExcludes);

            foreach (var file in files)
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
                    for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        var match = pattern.Match(lines[lineIndex]);
                        if (match.Success)
                        {
                            // Heuristic: declaration lines often contain keywords
                            // like "class", "func", "def", "fn", "public", etc.
                            // But for seeding references, any position works.
                            return new DeclarationSite(file, lineIndex + 1, match.Index + 1);
                        }
                    }
                }
                catch (IOException ex) { _logger.LogDebug(ex, "Skipped file during declaration search: {File}", file); }
                catch (UnauthorizedAccessException ex) { _logger.LogDebug(ex, "Skipped file (unauthorized) during declaration search: {File}", file); }
            }
        }

        return null;
    }

    /// <summary>
    /// Parse the JSON array returned by textDocument/references into a flat list
    /// of normalized locations. LSP responses may be either a Location[] array
    /// or a Location (single object) — both shapes are handled.
    /// </summary>
    private async Task<List<LspReferenceLocation>> ParseLocationArrayAsync(
        JsonElement el, string searchPath, CancellationToken ct)
    {
        var result = new List<LspReferenceLocation>();

        // LSP spec allows a single Location or Location[] — iterate both shapes.
        IEnumerable<JsonElement> elements = el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray().Select(e => e)
            : new[] { el };

        // 同一文件通常有多个引用位置——缓存行内容避免每位置重复读盘。
        var lineCache = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in elements)
        {
            if (!item.TryGetProperty("uri", out var uriEl))
                continue;

            var uri = uriEl.GetString() ?? "";
            var filePath = UriToFilePath(uri);

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

            // Read the source line for human-friendly output (best effort —
            // file may not exist on disk if it's a remote/unsynced buffer).
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
                        _logger.LogDebug(ex, "Failed to read source line from {FilePath}", filePath);
                        lines = null;
                    }
                    lineCache[filePath] = lines;
                }
                if (lines is not null && line > 0 && line <= lines.Length)
                    lineText = lines[line - 1];
            }

            result.Add(new LspReferenceLocation(filePath, line, column, lineText ?? ""));
        }

        return result;
    }

    private static string UriToFilePath(string uri) => LspUriHelper.UriToFilePath(uri);

    private static string FormatLspResults(string symbol, List<LspReferenceLocation> refs, int maxResults)
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

    private async Task<List<string>> SearchAsync(
        string searchPath, string pattern, string? glob, string? excludeGlob,
        CancellationToken ct)
    {
        if (await _processRunner.CommandExistsAsync("rg").ConfigureAwait(false))
            return await SearchRipgrepAsync(searchPath, pattern, glob, excludeGlob, ct).ConfigureAwait(false);
        return await SearchNativeAsync(searchPath, pattern, glob, excludeGlob, ct).ConfigureAwait(false);
    }

    private async Task<List<string>> SearchRipgrepAsync(
        string searchPath, string pattern, string? glob, string? excludeGlob,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "--hidden",
            "--glob", "!.git",
            "--glob", "!.svn",
            "--glob", "!node_modules",
            "--max-columns", Constants.Lsp.RipgrepMaxColumns.ToString(CultureInfo.InvariantCulture),
            "-n"
        };

        if (!string.IsNullOrEmpty(glob))
        {
            foreach (var g in glob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { args.Add("--glob"); args.Add(g); }
        }
        if (!string.IsNullOrEmpty(excludeGlob))
        {
            foreach (var eg in excludeGlob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { args.Add("--glob"); args.Add($"!{eg}"); }
        }

        args.Add(pattern);

        var result = await _processRunner.ExecuteAsync("rg", args.ToArray(), searchPath, ct: ct);
        if (result == null || (!result.Success && string.IsNullOrWhiteSpace(result.Stdout)))
        {
            return [];
        }

        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var prefix = searchPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return lines
            .Select(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? l[prefix.Length..] : l)
            .ToList();
    }

    private async Task<List<string>> SearchNativeAsync(
        string searchPath, string pattern, string? glob, string? excludeGlob,
        CancellationToken ct)
    {
        List<string> results = [];
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.Compiled); }
        catch (ArgumentException ex) { return new List<string> { $"Invalid regex: {ex.Message}" }; }

        var excludePatterns = string.IsNullOrEmpty(excludeGlob)
            ? Array.Empty<string>()
            : excludeGlob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var searchPattern = string.IsNullOrEmpty(glob) ? null : glob;
        var defaultExcludes = new[] { ".git", ".svn", "node_modules", "bin", "obj" };
        var files = _fileSystem.FindFiles(searchPath, searchPattern, defaultExcludes);

        if (excludePatterns.Length > 0)
        {
            files = files.Where(f =>
            {
                var rel = Path.GetRelativePath(searchPath, f).Replace('\\', '/');
                return !excludePatterns.Any(ep => IsGlobMatch(rel, ep));
            }).ToList();
        }

        foreach (var file in files)
        {
            try
            {
                var relativePath = Path.GetRelativePath(searchPath, file);
                var lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (regex.IsMatch(line))
                        results.Add($"{relativePath}:{i + 1}:{line}");
                }
            }
            catch (IOException ex) { _logger.LogDebug(ex, "Skipped file during reference scan: {File}", file); }
            catch (UnauthorizedAccessException ex) { _logger.LogDebug(ex, "Skipped file (unauthorized) during reference scan: {File}", file); }
        }
        return results;
    }

    private static bool IsGlobMatch(string relativePath, string pattern)
    {
        var matcher = new Matcher();
        matcher.AddInclude(pattern.Replace('\\', '/'));
        return matcher.Match(relativePath.Replace('\\', '/')).HasMatches;
    }

    private static string BuildFileUri(string filePath) => LspUriHelper.BuildFileUri(filePath);

    /// <summary>Result row for an LSP-resolved reference location.</summary>
    private sealed record LspReferenceLocation(string File, int Line, int Column, string LineText);

    /// <summary>A located declaration site used to seed an LSP references request.</summary>
    private sealed record DeclarationSite(string File, int Line, int Column);
}
