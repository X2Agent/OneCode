namespace OneCode.Core.Text;

/// <summary>
/// String similarity and distance metrics used across fuzzy matching scenarios:
/// command suggestion (Jaro-Winkler), path suggestions (Levenshtein case-insensitive),
/// and symbol search (Levenshtein case-sensitive).
/// </summary>
public static class StringDistance
{
    /// <summary>
    /// Case-insensitive Levenshtein distance.
    /// Used by EditTool for fuzzy path suggestions.
    /// </summary>
    public static int LevenshteinIgnoreCase(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    /// <summary>
    /// Case-sensitive Levenshtein distance.
    /// Used by CodeIndexService for fuzzy symbol search.
    /// </summary>
    public static int Levenshtein(string s, string t)
    {
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;

        var prev = new int[t.Length + 1];
        var curr = new int[t.Length + 1];
        for (var j = 0; j <= t.Length; j++) prev[j] = j;

        for (var i = 1; i <= s.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            Array.Copy(curr, prev, curr.Length);
        }
        return prev[t.Length];
    }

    /// <summary>
    /// Jaro-Winkler similarity — returns a value in [0, 1] where 1 is an exact match.
    /// Gives a prefix bonus for strings that share a common prefix (up to 4 chars).
    /// </summary>
    public static double JaroWinkler(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        const double boostThreshold = 0.7;
        const int maxPrefix = 4;

        var jaro = Jaro(a, b);
        if (jaro <= boostThreshold) return jaro;

        var prefixLen = Math.Min(maxPrefix, Math.Min(a.Length, b.Length));
        var prefixMatch = 0;
        for (var i = 0; i < prefixLen; i++)
        {
            if (char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
                prefixMatch++;
            else
                break;
        }

        return jaro + 0.1 * prefixMatch * (1.0 - jaro);
    }

    /// <summary>
    /// Jaro similarity — returns a value in [0, 1] where 1 is an exact match.
    /// </summary>
    public static double Jaro(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        var matchWindow = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);

        Span<bool> aMatched = stackalloc bool[a.Length];
        Span<bool> bMatched = stackalloc bool[b.Length];
        aMatched.Clear();
        bMatched.Clear();

        var matches = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - matchWindow);
            var end = Math.Min(b.Length - 1, i + matchWindow);
            for (var j = start; j <= end; j++)
            {
                if (bMatched[j] || char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[j]))
                    continue;
                aMatched[i] = true;
                bMatched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0.0;

        var transpositions = 0.0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatched[i]) continue;
            while (!bMatched[k]) k++;
            if (char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[k]))
                transpositions++;
            k++;
        }
        transpositions /= 2;

        return (matches / a.Length + matches / b.Length
                + (matches - transpositions) / matches) / 3.0;
    }
}
