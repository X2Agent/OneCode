namespace OneCode.Infrastructure.Text;

/// <summary>
/// Line-level unified diff using a standard LCS (Longest Common Subsequence) algorithm.
///
/// The output format mirrors `git diff --unified=N`:
///   --- a/{path}
///   +++ b/{path}
///   @@ -origStart,origCount +modStart,modCount @@
///    context line
///   -removed line
///   +added line
/// </summary>
public static class UnifiedDiff
{
    /// <summary>Maximum number of lines per file before diff is skipped.</summary>
    private const int MaxLines = 2_000;

    /// <summary>
    /// Compute a unified diff between <paramref name="original"/> and <paramref name="modified"/>.
    /// Returns a human-readable diff string; never throws.
    /// </summary>
    public static string Compute(string original, string modified, string filePath, int contextLines = 3)
    {
        var orig = SplitLines(original);
        var mod = SplitLines(modified);

        if (orig.Length > MaxLines || mod.Length > MaxLines)
            return $"--- a/{filePath}\n+++ b/{filePath}\n@@ diff skipped: file exceeds {MaxLines} lines @@\n";

        if (original == modified)
            return $"(no changes — {filePath})";

        var edits = ComputeEdits(orig, mod);
        return FormatHunks(orig, mod, edits, filePath, contextLines);
    }

    /// <summary>
    /// 计算 <paramref name="original"/> 与 <paramref name="modified"/> 之间的行级变更，
    /// 返回新增行和删除行列表（各限制 <paramref name="maxLines"/> 行，超出截断）。
    /// 用于 TUI 实时 Diff 展示；never throws。
    /// </summary>
    public static (string[] Added, string[] Removed) ComputeLineChanges(
        string original, string modified, int maxLines = 15)
    {
        try
        {
            var orig = SplitLines(original);
            var mod = SplitLines(modified);

            if (orig.Length > MaxLines || mod.Length > MaxLines)
                return (Array.Empty<string>(), Array.Empty<string>());

            if (original == modified)
                return (Array.Empty<string>(), Array.Empty<string>());

            var edits = ComputeEdits(orig, mod);
            var added = new List<string>();
            var removed = new List<string>();

            foreach (var (o, m, op) in edits)
            {
                if (op == '+' && added.Count < maxLines)
                    added.Add(mod[m]);
                else if (op == '-' && removed.Count < maxLines)
                    removed.Add(orig[o]);
            }

            return (added.ToArray(), removed.ToArray());
        }
        catch
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// Standard reverse-LCS DP producing a sequence of (origIdx, modIdx, op) triples,
    /// where op is ' ' (keep), '-' (delete), '+' (insert).
    /// </summary>
    private static List<(int o, int m, char op)> ComputeEdits(string[] orig, string[] mod)
    {
        var rows = orig.Length;
        var cols = mod.Length;

        // Build DP table (reverse so we can walk forward easily)
        var dp = new int[rows + 1, cols + 1];
        for (var i = rows - 1; i >= 0; i--)
            for (var j = cols - 1; j >= 0; j--)
                dp[i, j] = orig[i] == mod[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var result = new List<(int, int, char)>(rows + cols);
        int oi = 0, mi = 0;
        while (oi < rows || mi < cols)
        {
            if (oi < rows && mi < cols && orig[oi] == mod[mi])
            {
                result.Add((oi, mi, ' '));
                oi++; mi++;
            }
            else if (mi < cols && (oi >= rows || dp[oi, mi + 1] >= dp[oi + 1, mi]))
            {
                result.Add((-1, mi, '+'));
                mi++;
            }
            else
            {
                result.Add((oi, -1, '-'));
                oi++;
            }
        }
        return result;
    }

    private static string FormatHunks(
        string[] orig, string[] mod,
        List<(int o, int m, char op)> edits,
        string path, int ctx)
    {
        List<int> changePosns = [];
        for (var i = 0; i < edits.Count; i++)
            if (edits[i].op != ' ')
                changePosns.Add(i);

        if (changePosns.Count == 0)
            return $"(no changes — {path})";

        List<(int start, int end)> hunks = [];
        var hStart = Math.Max(0, changePosns[0] - ctx);
        var hEnd = Math.Min(edits.Count - 1, changePosns[0] + ctx);

        for (var i = 1; i < changePosns.Count; i++)
        {
            var nextStart = Math.Max(0, changePosns[i] - ctx);
            if (nextStart <= hEnd + 1)
                hEnd = Math.Min(edits.Count - 1, changePosns[i] + ctx);
            else
            {
                hunks.Add((hStart, hEnd));
                hStart = nextStart;
                hEnd = Math.Min(edits.Count - 1, changePosns[i] + ctx);
            }
        }
        hunks.Add((hStart, hEnd));

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"--- a/{path}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"+++ b/{path}");

        foreach (var (hs, he) in hunks)
        {
            var hunkEdits = edits.Skip(hs).Take(he - hs + 1).ToList();
            var origStart = hunkEdits.Where(e => e.o >= 0).Select(e => e.o).DefaultIfEmpty(-1).First() + 1;
            var origCount = hunkEdits.Count(e => e.op is ' ' or '-');
            var modStart = hunkEdits.Where(e => e.m >= 0).Select(e => e.m).DefaultIfEmpty(-1).First() + 1;
            var modCount = hunkEdits.Count(e => e.op is ' ' or '+');

            sb.AppendLine(CultureInfo.InvariantCulture, $"@@ -{origStart},{origCount} +{modStart},{modCount} @@");
            foreach (var (o, m, op) in hunkEdits)
            {
                var line = op == '+' ? mod[m] : orig[o];
                sb.AppendLine(CultureInfo.InvariantCulture, $"{op}{line}");
            }
        }

        return sb.ToString();
    }
}
