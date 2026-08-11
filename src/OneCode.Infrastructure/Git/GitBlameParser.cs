namespace OneCode.Infrastructure.Git;

/// <summary>
/// A single line attribution from <c>git blame --porcelain</c>.
/// </summary>
public sealed record GitBlameEntry(
    string CommitHash,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorTime,
    string FilePath,
    int LineNumber,
    string Content
);

/// <summary>
/// Parses <c>git blame --porcelain</c> output into <see cref="GitBlameEntry"/> records.
/// Uses the porcelain format for reliable machine-readable parsing.
/// </summary>
public static class GitBlameParser
{
    /// <summary>
    /// Parse the stdout of <c>git blame --porcelain &lt;file&gt;</c>.
    /// Returns one entry per source line in the file.
    /// </summary>
    public static IReadOnlyList<GitBlameEntry> Parse(string porcelainOutput, string filePath)
    {
        if (string.IsNullOrWhiteSpace(porcelainOutput))
            return Array.Empty<GitBlameEntry>();

        var lines = porcelainOutput.Split('\n');
        List<GitBlameEntry> entries = [];

        var hash = string.Empty;
        var authorName = string.Empty;
        var authorEmail = string.Empty;
        var authorTime = DateTimeOffset.MinValue;
        var lineNumber = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Header line: "<40-char hash> <orig-line> <final-line> [<num-lines>]".
            // The hash itself must not contain spaces, so a space in the first 40
            // chars disqualifies a line as a header. (Previous form was
            // `!line.Contains(' ') is false` which was equivalent but unreadable.)
            if (line.Length >= 40
                && !line.StartsWith('\t')
                && !line[..40].Contains(' ')
                && IsHexString(line[..40]))
            {
                hash = line[..40];
                var parts = line.Split(' ');
                if (parts.Length >= 3 && int.TryParse(parts[2], out var ln))
                    lineNumber = ln;
                continue;
            }

            if (line.StartsWith("author ", StringComparison.Ordinal))
            {
                authorName = line["author ".Length..];
                continue;
            }
            if (line.StartsWith("author-mail ", StringComparison.Ordinal))
            {
                authorEmail = line["author-mail ".Length..].Trim('<', '>');
                continue;
            }
            if (line.StartsWith("author-time ", StringComparison.Ordinal))
            {
                if (long.TryParse(line["author-time ".Length..], out var epoch))
                    authorTime = DateTimeOffset.FromUnixTimeSeconds(epoch);
                continue;
            }

            // Content line starts with a tab
            if (line.StartsWith('\t') && !string.IsNullOrEmpty(hash))
            {
                entries.Add(new GitBlameEntry(
                    hash, authorName, authorEmail, authorTime,
                    filePath, lineNumber, line[1..]));
            }
        }

        return entries;
    }

    private static bool IsHexString(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
