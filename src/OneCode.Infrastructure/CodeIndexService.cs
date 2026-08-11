using OneCode.Infrastructure.Abstractions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace OneCode.Infrastructure;

/// <summary>
/// Scans source files and builds an inverted index of code symbols
/// (classes, methods, interfaces, enums, structs) for fast lookups.
///
/// Supports C#, TypeScript, JavaScript, Python, Go, and Java.
/// </summary>
public sealed partial class CodeIndexService : ICodeIndexService
{
    private readonly ILogger<CodeIndexService>? _logger;

    // Inverted index: lowercase symbol name → list of symbols
    private readonly ConcurrentDictionary<string, List<CodeSymbol>> _index = new(StringComparer.OrdinalIgnoreCase);

    // Sorted snapshot of all lowercase keys — rebuilt after each index operation for O(log N) prefix search
    private volatile string[] _sortedKeys = Array.Empty<string>();

    // Track which files are indexed
    private readonly ConcurrentDictionary<string, byte> _indexedFiles = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _isIndexing;

    public bool IsIndexing => _isIndexing;
    public int SymbolCount => _index.Values.Sum(v => v.Count);
    public DateTimeOffset? LastIndexedAt { get; private set; }

    public CodeIndexService(ILogger<CodeIndexService>? logger = null)
    {
        _logger = logger;
    }

    public Task BuildIndexAsync(string rootDirectory, CancellationToken ct = default)
    {
        if (_isIndexing)
        {
            _logger?.LogInformation("Index build already in progress, skipping");
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                _isIndexing = true;
                Clear();

                if (!Directory.Exists(rootDirectory))
                {
                    _logger?.LogWarning("Index root directory does not exist: {Dir}", rootDirectory);
                    return;
                }

                // Match common source file extensions
                var matcher = new Matcher();
                matcher.AddIncludePatterns(new[]
                {
                    "**/*.cs", "**/*.ts", "**/*.tsx", "**/*.js", "**/*.jsx",
                    "**/*.py", "**/*.go", "**/*.java", "**/*.rs",
                    "**/*.vb", "**/*.fs", "**/*.fsx",
                });
                matcher.AddExcludePatterns(new[]
                {
                    "**/node_modules/**", "**/bin/**", "**/obj/**",
                    "**/.git/**", "**/dist/**", "**/out/**",
                    "**/__pycache__/**", "**/vendor/**", "**/target/**",
                    "**/.vs/**", "**/Debug/**", "**/Release/**",
                });

                var files = matcher.GetResultsInFullPath(rootDirectory).ToList();
                _logger?.LogInformation("Indexing {Count} source files in {Dir}", files.Count, rootDirectory);

                var processed = 0;
                var parallelOptions = new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                };

                Parallel.ForEach(files, parallelOptions, file =>
                {
                    IndexFile(file);
                    var done = Interlocked.Increment(ref processed);
                    if (done % 500 == 0)
                        _logger?.LogInformation("Indexed {Done}/{Total} files", done, files.Count);
                });

                LastIndexedAt = DateTimeOffset.UtcNow;
                RebuildSortedKeys();
                _logger?.LogInformation("Index complete: {Count} symbols in {Files} files",
                    SymbolCount, _indexedFiles.Count);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("Index build cancelled");
                Clear();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Index build failed");
            }
            finally
            {
                _isIndexing = false;
            }
        }, ct);
    }

    public Task UpdateFilesAsync(IEnumerable<string> changedFiles, IEnumerable<string>? removedFiles = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (removedFiles is not null)
            {
                foreach (var file in removedFiles)
                {
                    RemoveFileFromIndex(file);
                }
            }

            // Re-index changed files
            foreach (var file in changedFiles)
            {
                if (ct.IsCancellationRequested) break;
                RemoveFileFromIndex(file);
                if (File.Exists(file))
                    IndexFile(file);
            }
            RebuildSortedKeys();
            LastIndexedAt = DateTimeOffset.UtcNow;
        }, ct);
    }

    public IReadOnlyList<CodeSymbolMatch> Search(
        string query,
        int maxResults = 50,
        string? kindFilter = null,
        string? pathScope = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<CodeSymbolMatch>();

        var normalized = query.Trim();
        List<CodeSymbolMatch> results = [];

        // Exact match (highest relevance)
        if (_index.TryGetValue(normalized, out var exactMatches))
        {
            results.AddRange(exactMatches.Select(s => new CodeSymbolMatch(s, 1.0)));
        }

        // Prefix matches — O(log N) binary search over sorted key snapshot
        var keys = _sortedKeys; // local ref so it doesn't change mid-scan
        var prefixLow = normalized.ToLowerInvariant();
        var startIdx = Array.BinarySearch(keys, prefixLow, StringComparer.Ordinal);
        if (startIdx < 0) startIdx = ~startIdx;
        for (var i = startIdx; i < keys.Length; i++)
        {
            var key = keys[i];
            if (!key.StartsWith(prefixLow, StringComparison.Ordinal)) break;
            if (key.Equals(prefixLow, StringComparison.OrdinalIgnoreCase)) continue; // already added as exact
            if (_index.TryGetValue(key, out var prefixSymbols))
                results.AddRange(prefixSymbols.Select(s => new CodeSymbolMatch(s, 0.8)));
        }

        // Substring matches — still O(N) but fast array scan, only when prefix didn't match everything
        foreach (var key in keys)
        {
            if (key.Equals(prefixLow, StringComparison.OrdinalIgnoreCase)) continue;
            if (key.StartsWith(prefixLow, StringComparison.OrdinalIgnoreCase)) continue; // already covered
            if (key.Contains(prefixLow, StringComparison.OrdinalIgnoreCase))
            {
                if (_index.TryGetValue(key, out var substringSymbols))
                    results.AddRange(substringSymbols.Select(s => new CodeSymbolMatch(s, 0.5)));
            }
        }

        // Fuzzy / typo-tolerant fallback — only when exact+prefix+substring all returned nothing.
        // Uses Levenshtein distance capped at max(1, query.Length/4) to allow e.g. "GetUsr"→"GetUser".
        if (results.Count == 0 && normalized.Length >= 3)
        {
            var maxDist = Math.Max(1, normalized.Length / 4);
            foreach (var key in keys)
            {
                var dist = OneCode.Core.Text.StringDistance.Levenshtein(prefixLow, key);
                if (dist <= maxDist)
                {
                    var score = 1.0 - (double)dist / Math.Max(prefixLow.Length, key.Length);
                    if (_index.TryGetValue(key, out var fuzzySymbols))
                        results.AddRange(fuzzySymbols.Select(s => new CodeSymbolMatch(s, score * 0.4)));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(kindFilter))
        {
            results = results
                .Where(r => r.Symbol.Kind.Equals(kindFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Apply path scope filter — normalise separators before comparing
        if (!string.IsNullOrWhiteSpace(pathScope))
        {
            var normalizedScope = pathScope
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);

            results = results
                .Where(r => r.Symbol.FilePath.StartsWith(normalizedScope, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return results
            .DistinctBy(r => (r.Symbol.Name, r.Symbol.FilePath, r.Symbol.Line))
            .OrderByDescending(r => r.RelevanceScore)
            .ThenBy(r => r.Symbol.Name)
            .Take(maxResults)
            .ToList();
    }

    public void Clear()
    {
        _index.Clear();
        _indexedFiles.Clear();
        _sortedKeys = Array.Empty<string>();
        LastIndexedAt = null;
    }

    /// <summary>
    /// Rebuilds the sorted key array from the current index snapshot.
    /// Called after every bulk index operation to enable O(log N) prefix search.
    /// </summary>
    private void RebuildSortedKeys()
    {
        var keys = _index.Keys.Select(k => k.ToLowerInvariant()).Distinct().ToArray();
        Array.Sort(keys, StringComparer.Ordinal);
        _sortedKeys = keys;
    }

    #region Symbol Extraction

    private void IndexFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (!IsSourceFile(ext))
                return;

            _indexedFiles[filePath] = 0;
            var lines = File.ReadLines(filePath);

            var lineNumber = 0;
            foreach (var line in lines)
            {
                lineNumber++;
                var symbols = ExtractSymbolsFromLine(line, lineNumber, filePath, ext);
                foreach (var symbol in symbols)
                {
                    var list = _index.GetOrAdd(symbol.Name.ToLowerInvariant(), _ => []);
                    lock (list)
                    {
                        list.Add(symbol);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to index file: {File}", filePath);
        }
    }

    private void RemoveFileFromIndex(string filePath)
    {
        _indexedFiles.TryRemove(filePath, out _);

        // Remove all symbols from this file. Snapshot keys inside a single
        // atomic pass: take a snapshot of the current keys, then process each.
        // If a concurrent TryRemove removes the entry between our snapshot
        // and TryGetValue, TryGetValue simply returns false — no exception.
        foreach (var key in _index.Keys.ToList())
        {
            if (_index.TryGetValue(key, out var list))
            {
                lock (list)
                {
                    list.RemoveAll(s => string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                    if (list.Count == 0)
                        _index.TryRemove(key, out _);
                }
            }
        }
    }

    private static List<CodeSymbol> ExtractSymbolsFromLine(string line, int lineNumber, string filePath, string extension)
    {
        List<CodeSymbol> symbols = [];
        var trimmed = line.TrimStart();

        if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('#') || trimmed.StartsWith("--", StringComparison.Ordinal))
            return symbols;

        // .NET: try type declarations first, then fall back to method declarations.
        if (extension is ".cs" or ".vb" or ".fs" or ".fsx")
        {
            var typeMatch = _msDotNetPattern.Match(trimmed);
            if (typeMatch.Success)
            {
                // "kind" group is always populated (class/interface/struct/enum/record)
                AddSymbol(symbols, typeMatch, "type", line, lineNumber, filePath);
                return symbols;
            }
            var methodMatch = _msDotNetMethodPattern.Match(trimmed);
            if (methodMatch.Success)
                AddSymbol(symbols, methodMatch, "method", line, lineNumber, filePath);
            return symbols;
        }

        // TypeScript/JavaScript: try named declarations, then const arrow functions
        if (extension is ".ts" or ".tsx" or ".js" or ".jsx")
        {
            var tsMatch = _msTypeScriptPattern.Match(trimmed);
            if (tsMatch.Success)
            {
                AddSymbol(symbols, tsMatch, "function", line, lineNumber, filePath);
                return symbols;
            }
            var arrowMatch = _msTypeScriptArrowPattern.Match(trimmed);
            if (arrowMatch.Success)
                AddSymbol(symbols, arrowMatch, "function", line, lineNumber, filePath);
            return symbols;
        }

        Match match = extension switch
        {
            ".py" => _msPythonPattern.Match(trimmed),
            ".go" => _msGoPattern.Match(trimmed),
            ".java" => _msJavaPattern.Match(trimmed),
            ".rs" => _msRustPattern.Match(trimmed),
            _ => Match.Empty,
        };

        if (match.Success)
            AddSymbol(symbols, match, "function", line, lineNumber, filePath);

        return symbols;
    }

    private static void AddSymbol(
        List<CodeSymbol> symbols, Match match, string fallbackKind,
        string originalLine, int lineNumber, string filePath)
    {
        var kind = match.Groups["kind"].Value;
        if (string.IsNullOrWhiteSpace(kind))
            kind = fallbackKind;

        var name = match.Groups["name"].Value.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        var column = originalLine.IndexOf(name, StringComparison.Ordinal) + 1;
        if (column <= 0) column = 1;

        symbols.Add(new CodeSymbol(name, kind, filePath, lineNumber, column));
    }

    private static bool IsSourceFile(string extension) => extension is ".cs" or ".vb" or ".fs" or ".fsx"
        or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".go" or ".java" or ".rs";

    #endregion

    #region Regex Patterns

    // .NET: class, interface, struct, enum, record, method (with visibility modifier)
    [GeneratedRegex(
        @"(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|virtual|override|async|unsafe|extern|new\s+)*\s*(?<kind>class|interface|struct|enum|record)\s+(?<name>[A-Za-z_]\w*)\b")]
    private static partial Regex _msDotNetPattern { get; }

    // .NET method declaration: modifier(s) returnType MethodName(
    [GeneratedRegex(
        @"(?:public|private|protected|internal|static|sealed|abstract|virtual|override|async|unsafe|extern|new\s+)+\s+(?:[\w<>[\],.]+\s+)+(?<name>[A-Za-z_]\w*)\s*[<(]")]
    private static partial Regex _msDotNetMethodPattern { get; }

    // TypeScript/JavaScript: class, interface, enum, function, const arrow function, export default function
    [GeneratedRegex(
        @"(?:export\s+(?:default\s+)?)?(?<kind>class|interface|enum|function|type)\s+(?<name>[A-Za-z_$]\w*)\b")]
    private static partial Regex _msTypeScriptPattern { get; }

    // TypeScript/JavaScript: const/let/var name = (async)? (...) => or function
    [GeneratedRegex(
        @"(?:export\s+)?(?:const|let|var)\s+(?<name>[A-Za-z_$]\w*)\s*=\s*(?:async\s*)?\(")]
    private static partial Regex _msTypeScriptArrowPattern { get; }

    // Python: class, def (function)
    [GeneratedRegex(
        @"(?<kind>class|def)\s+(?<name>[A-Za-z_]\w*)\b")]
    private static partial Regex _msPythonPattern { get; }

    // Go: type, func
    [GeneratedRegex(
        @"(?<kind>type|func)\s+(?:\([^)]*\)\s+)?(?<name>[A-Za-z_]\w*)\b")]
    private static partial Regex _msGoPattern { get; }

    // Java: class, interface, enum, record
    [GeneratedRegex(
        @"(?:public|private|protected|static|final|abstract|synchronized|native|strictfp\s+)*\s*(?<kind>class|interface|enum|record)\s+(?<name>[A-Za-z_$]\w*)\b")]
    private static partial Regex _msJavaPattern { get; }

    // Rust: struct, enum, trait, impl, fn
    [GeneratedRegex(
        @"(?<kind>struct|enum|trait|impl|fn)\s+(?<name>[A-Za-z_]\w*)\b")]
    private static partial Regex _msRustPattern { get; }

    #endregion
}
