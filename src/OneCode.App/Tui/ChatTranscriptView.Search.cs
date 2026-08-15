using System.Text.RegularExpressions;

namespace OneCode.App.Tui;

/// <summary>
/// <see cref="ChatTranscriptView"/> 的搜索部分：/find、/find -r、/find next
/// 与搜索高亮。搜索状态跨调用保存，支持基于上一次查询继续跳转。
/// </summary>
public sealed partial class ChatTranscriptView
{
    private string? _lastSearchQuery;
    private bool _lastSearchIsRegex;
    private int _lastSearchMatchIdx;

    /// <summary>
    /// 搜索会话中包含指定文本的行，并滚动到第一个匹配项。
    /// 返回 (匹配总数, 当前匹配索引)。无匹配时返回 (0, -1)。
    /// </summary>
    public (int TotalMatches, int CurrentIndex) SearchAndScroll(string query, int startFrom = 0)
    {
        var matches = _messageView.FindMatches(query);
        if (matches.Count == 0)
        {
            _messageView.SetSearchHighlight(null, null);
            return (0, -1);
        }

        // 找到 startFrom 之后的第一个匹配
        var idx = matches.FindIndex(m => m >= startFrom);
        if (idx < 0) idx = 0; // 回绕到第一个
        var targetLine = matches[idx];

        // 高亮所有匹配行中的关键词
        _messageView.SetSearchHighlight(query, matches);
        _messageView.ScrollToLine(targetLine);

        // 保存搜索状态供 /find next
        _lastSearchQuery = query;
        _lastSearchIsRegex = false;
        _lastSearchMatchIdx = idx;

        return (matches.Count, idx);
    }

    /// <summary>
    /// 使用正则表达式搜索会话中的匹配行。
    /// </summary>
    public (int TotalMatches, int CurrentIndex) SearchAndScrollRegex(string pattern, int startFrom = 0)
    {
        var matches = _messageView.FindMatchesRegex(pattern, out var regex);
        if (matches.Count == 0)
        {
            _messageView.SetSearchHighlight(null, null);
            return (0, -1);
        }

        var idx = matches.FindIndex(m => m >= startFrom);
        if (idx < 0) idx = 0; // 回绕到第一个
        var targetLine = matches[idx];

        _messageView.SetSearchHighlight(pattern, matches, compiledRegex: regex);
        _messageView.ScrollToLine(targetLine);

        _lastSearchQuery = pattern;
        _lastSearchIsRegex = true;
        _lastSearchMatchIdx = idx;

        return (matches.Count, idx);
    }

    /// <summary>
    /// 继续搜索上次的关键词，跳转到下一个匹配项。
    /// 返回 (匹配总数, 当前匹配索引)。无上次搜索或无匹配时返回 (0, -1)。
    /// </summary>
    public (int TotalMatches, int CurrentIndex) FindNext()
    {
        if (string.IsNullOrEmpty(_lastSearchQuery))
            return (0, -1);

        Regex? regex = null;
        var matches = _lastSearchIsRegex
            ? _messageView.FindMatchesRegex(_lastSearchQuery, out regex)
            : _messageView.FindMatches(_lastSearchQuery);
        if (matches.Count == 0)
            return (0, -1);

        // 跳到下一个匹配（回绕到第一个）
        var nextIdx = (_lastSearchMatchIdx + 1) % matches.Count;
        var targetLine = matches[nextIdx];
        _messageView.SetSearchHighlight(
            _lastSearchQuery, matches, compiledRegex: regex);
        _messageView.ScrollToLine(targetLine);
        _lastSearchMatchIdx = nextIdx;

        return (matches.Count, nextIdx);
    }

    /// <summary>清除搜索高亮。</summary>
    public void ClearSearchHighlight()
    {
        _messageView.SetSearchHighlight(null, null);
    }
}
