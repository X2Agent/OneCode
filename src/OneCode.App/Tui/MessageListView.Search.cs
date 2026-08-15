using System.Text.RegularExpressions;

namespace OneCode.App.Tui;

/// <summary>
/// Search and in-line highlight for <see cref="MessageListView"/>
/// (supports /find keyword and regex queries).
/// </summary>
public sealed partial class MessageListView
{
    private string? _highlightQuery;
    private bool _highlightIsRegex;
    private Regex? _highlightRegex;
    private HashSet<int>? _highlightedLineIndices;

    /// <summary>
    /// 搜索包含指定文本的行，返回所有匹配行的索引列表。
    /// </summary>
    public List<int> FindMatches(string query, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var results = new List<int>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Text.Contains(query, comparison))
                results.Add(i);
        }
        return results;
    }

    /// <summary>
    /// 使用正则表达式搜索匹配行。
    /// <paramref name="compiled"/> 返回本次编译的正则，供 <see cref="SetSearchHighlight"/> 复用，避免二次编译。
    /// </summary>
    public List<int> FindMatchesRegex(
        string pattern, out Regex? compiled, RegexOptions options = RegexOptions.IgnoreCase)
    {
        compiled = null;
        var results = new List<int>();
        if (string.IsNullOrWhiteSpace(pattern)) return results;
        compiled = new Regex(pattern, options, TimeSpan.FromSeconds(1));
        for (var i = 0; i < _lines.Count; i++)
        {
            if (compiled.IsMatch(_lines[i].Text))
                results.Add(i);
        }
        return results;
    }

    /// <summary>
    /// 设置搜索高亮：在指定行中高亮匹配的关键词。
    /// 传 null / 空列表清除高亮。
    /// 优先使用 <paramref name="compiledRegex"/>（与搜索侧共用同一实例）；
    /// 若仅设 <paramref name="isRegex"/> 则就地编译，失败时跳过行内高亮（不抛、也不回退为字面量匹配）。
    /// </summary>
    public void SetSearchHighlight(
        string? query,
        IReadOnlyList<int>? matchedLineIndices,
        bool isRegex = false,
        Regex? compiledRegex = null)
    {
        _highlightedLineIndices = matchedLineIndices is { Count: > 0 }
            ? new HashSet<int>(matchedLineIndices)
            : null;

        if (compiledRegex is not null)
        {
            _highlightQuery = query;
            _highlightRegex = compiledRegex;
            _highlightIsRegex = true;
        }
        else if (isRegex && !string.IsNullOrEmpty(query))
        {
            try
            {
                _highlightRegex = new Regex(query, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                _highlightQuery = query;
                _highlightIsRegex = true;
            }
            catch (RegexParseException)
            {
                // 无效 pattern：保留匹配行索引用于滚动，但不做行内高亮（避免字面量 IndexOf 误匹配）
                _highlightRegex = null;
                _highlightQuery = null;
                _highlightIsRegex = false;
            }
        }
        else
        {
            _highlightQuery = query;
            _highlightRegex = null;
            _highlightIsRegex = false;
        }

        SetNeedsDraw();
    }
}
