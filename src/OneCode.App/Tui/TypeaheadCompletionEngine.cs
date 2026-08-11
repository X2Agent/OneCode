namespace OneCode.App.Tui;

/// <summary>
/// Provides typeahead completion suggestions for the REPL prompt.
/// Supports three completion modes:
///   1. Slash commands (/command → /config, /help, ...)
///   2. File paths  (read "path → read "path/to/file.cs")
///   3. Tool names   (@Tool → @WebFetch, @Read, ...)
/// </summary>
public sealed class TypeaheadCompletionEngine
{
    private IReadOnlyList<SlashCommandEntry> _commands;
    private readonly string _workingDirectory;
    private readonly Func<IReadOnlyCollection<string>> _toolNameProvider;

    public TypeaheadCompletionEngine(
        IReadOnlyList<SlashCommandEntry> commands,
        string workingDirectory,
        Func<IReadOnlyCollection<string>> toolNameProvider)
    {
        _commands = commands;
        _workingDirectory = workingDirectory;
        _toolNameProvider = toolNameProvider;
    }

    private IReadOnlyCollection<string> GetToolNames()
        => _toolNameProvider.Invoke();

    /// <summary>
    /// Replaces the command list used for slash-completion at runtime (e.g. after skills/MCP refresh).
    /// </summary>
    public void UpdateCommands(IReadOnlyList<SlashCommandEntry> commands)
        => _commands = commands;

    /// <summary>
    /// Detect the completion mode and return matching suggestions.
    /// Returns empty list if no completions are available.
    /// </summary>
    public List<string> GetCompletions(string input)
    {
        if (string.IsNullOrEmpty(input))
            return [];

        // Mode 1: Slash commands — check the last word, not just the whole input.
        // This lets "/help" work even after other text like "hello /help".
        var lastWord = ExtractLastWord(input);
        if (lastWord.StartsWith('/'))
            return GetCommandCompletions(lastWord);

        // Mode 2: @Tool references or @file path references
        if (input.EndsWith('@'))
        {
            var fileSuggestions = GetWorkingDirEntries(input, "");
            var toolSuggestions = GetToolNames()
                .Select(t => $"{input}{t}")
                .ToList();
            fileSuggestions.AddRange(toolSuggestions);
            return fileSuggestions;
        }

        var lastAt = input.LastIndexOf('@');
        if (lastAt >= 0 && lastAt < input.Length - 1)
        {
            var prefix = input[..lastAt];
            var query = input[(lastAt + 1)..];
            if (!query.Contains(' '))
            {
                // File path completions first (more commonly used after @)
                var fileMatches = GetAtFileCompletions(prefix, query);

                var toolMatches = GetToolNames()
                    .Where(t => t.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    .Select(t => $"{prefix}@{t} ")
                    .ToList();

                fileMatches.AddRange(toolMatches);

                if (fileMatches.Count > 0)
                    return fileMatches;
            }
        }

        // Mode 3: File paths (when text contains quote or path-like patterns)
        if (TryGetFilePathCompletion(input, out var pathCompletions))
            return pathCompletions;

        return [];
    }

    /// <summary>
    /// Returns display items for the completion popup.
    /// Each item is (display text, insertion text).
    /// The display text is what the user sees (e.g. just "readme.txt"),
    /// while the insertion text is what gets placed into the input (e.g. "hello @readme.txt").
    /// </summary>
    public List<(string Display, string Insert)> GetCompletionItems(string input)
    {
        var completions = GetCompletions(input);
        return completions.Select(c => (ExtractDisplayText(input, c), c)).ToList();
    }

    /// <summary>
    /// Extracts a human-friendly display string from a completion result.
    /// For @-prefixed completions, shows only the part after the last @ (the file/tool name).
    /// For slash commands, shows the full "/command" string.
    /// For file paths, shows the path without any input prefix.
    /// </summary>
    private static string ExtractDisplayText(string input, string completion)
    {
        // Slash commands: display as-is
        if (completion.StartsWith('/'))
            return completion;

        // @-prefixed completions: show only the part after the last @
        var lastAt = completion.LastIndexOf('@');
        if (lastAt >= 0)
        {
            var afterAt = completion[(lastAt + 1)..];
            return afterAt;
        }

        // Quoted path completions: show without surrounding text
        if (completion.StartsWith('"'))
        {
            var endQuote = completion.LastIndexOf('"');
            if (endQuote > 0)
                return completion[1..endQuote];
        }

        // For other path completions, try to show just the last segment
        // (the part that was actually completed, not the user's existing input)
        if (input.Length > 0 && completion.StartsWith(input, StringComparison.Ordinal))
        {
            var suffix = completion[input.Length..];
            if (!string.IsNullOrEmpty(suffix))
                return suffix;
        }

        return completion;
    }

    private List<string> GetCommandCompletions(string input)
    {
        var query = input.TrimStart('/').ToLowerInvariant();

        // Phase 1: Exact name prefix match (highest priority).
        // When the user types /status, only /status should match — not /hooks
        // just because its description contains the word "status".
        var nameMatches = _commands
            .Where(c => string.IsNullOrEmpty(query)
                || c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"/{c.Name}")
            .ToList();

        if (nameMatches.Count > 0)
            return nameMatches;

        // Phase 2: Description fallback (only when no name prefix matched).
        // Match whole words in the description, not arbitrary substrings, to avoid
        // false positives like /status matching /hooks (description contains "status").
        if (string.IsNullOrEmpty(query))
            return _commands.Select(c => $"/{c.Name}").ToList();

        var descMatches = _commands
            .Where(c => c.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.Equals(query, StringComparison.OrdinalIgnoreCase)))
            .Select(c => $"/{c.Name}")
            .ToList();

        return descMatches;
    }

    /// <summary>
    /// Complete file paths after @ symbol, supporting @src/foo or @./relative patterns.
    /// </summary>
    private List<string> GetAtFileCompletions(string prefix, string query)
    {
        try
        {
            var dir = Path.GetDirectoryName(query);
            if (string.IsNullOrEmpty(dir))
                dir = _workingDirectory;
            else if (!Path.IsPathRooted(dir))
                dir = Path.Combine(_workingDirectory, dir);

            if (!Directory.Exists(dir))
                return [];

            var fileName = Path.GetFileName(query);
            var entries = Directory.GetFileSystemEntries(dir)
                .Select(p => Path.GetFileName(p))
                .Where(f => string.IsNullOrEmpty(fileName) || f.StartsWith(fileName, StringComparison.OrdinalIgnoreCase))
                .Take(15)
                .ToList();

            if (entries.Count == 0) return [];

            var prefixDir = Path.GetDirectoryName(query) ?? "";
            if (prefixDir.Length > 0)
                prefixDir += Path.DirectorySeparatorChar;

            return entries.Select(f =>
            {
                var relativePath = prefixDir + f;
                var fullPath = Path.Combine(_workingDirectory, relativePath);
                var suffix = Directory.Exists(fullPath) ? Path.DirectorySeparatorChar.ToString() : "";
                return $"{prefix}@{relativePath}{suffix}";
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    private List<string> GetWorkingDirEntries(string prefix, string query)
    {
        try
        {
            var entries = Directory.GetFileSystemEntries(_workingDirectory)
                .Select(Path.GetFileName)
                .Where(f => f is not null && !f.StartsWith('.'))
                .Take(10)
                .ToList();
            return entries.Select(f => $"{prefix}{f}").ToList()!;
        }
        catch
        {
            return [];
        }
    }

    private bool TryGetFilePathCompletion(string input, out List<string> completions)
    {
        completions = [];

        // Check if the last "word" looks like a path
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return false;

        var lastWord = words[^1];

        // Don't treat words starting with '/' as paths — those are slash commands
        // handled by GetCommandCompletions. Without this guard, "/he" would match
        // the path-like pattern (contains '/') and trigger file completion.
        if (lastWord.StartsWith('/'))
            return false;

        // Path-like patterns: contains / or \, or starts with . or ~  or is in quotes
        var isInQuotes = lastWord.StartsWith('"') && !lastWord.EndsWith('"');
        var isPathLike = lastWord.Contains('/') || lastWord.Contains('\\')
            || lastWord.StartsWith('.') || lastWord.StartsWith('~');

        if (!isInQuotes && !isPathLike)
            return false;

        var pathPrefix = isInQuotes ? lastWord[1..] : lastWord;
        var originalPrefix = isInQuotes ? "\"" + pathPrefix : pathPrefix;

        try
        {
            var dir = Path.GetDirectoryName(pathPrefix);
            if (string.IsNullOrEmpty(dir))
                dir = _workingDirectory;
            else if (!Path.IsPathRooted(dir))
                dir = Path.Combine(_workingDirectory, dir);

            if (!Directory.Exists(dir))
                return false;

            var fileName = Path.GetFileName(pathPrefix);
            var entries = Directory.GetFileSystemEntries(dir)
                .Select(p => Path.GetFileName(p))
                .Where(f => f.StartsWith(fileName, StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToList();

            if (entries.Count == 0)
                return false;

            var prefixDir = Path.GetDirectoryName(pathPrefix) ?? "";
            if (prefixDir.Length > 0)
                prefixDir += Path.DirectorySeparatorChar;

            completions = entries.Select(f =>
            {
                var full = prefixDir + f;
                var fullPath = Path.Combine(_workingDirectory, full);
                var suffix = Directory.Exists(fullPath) ? Path.DirectorySeparatorChar.ToString() : "";
                return isInQuotes ? $"\"{full}{suffix}\"" : $"{full}{suffix}";
            }).ToList();

            return completions.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts the last space-delimited word from the input, respecting double-quoted segments.
    /// </summary>
    private static string ExtractLastWord(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var inQuotes = false;
        var lastSpace = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') inQuotes = !inQuotes;
            else if (text[i] == ' ' && !inQuotes) lastSpace = i;
        }
        return lastSpace >= 0 ? text[(lastSpace + 1)..] : text;
    }
}
