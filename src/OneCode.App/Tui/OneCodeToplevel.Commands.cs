using System.Text.RegularExpressions;

namespace OneCode.App.Tui;

/// <summary>
/// In-transcript immediate commands for <see cref="OneCodeToplevel"/>:
/// /find（关键词 / 正则 / next 搜索跳转）与 /diff（变更审查 overlay）。
/// 由 <c>HandleImmediateCommandAsync</c> 在 UI 线程内调用，不经查询队列。
/// </summary>
public sealed partial class OneCodeToplevel
{
    /// <summary>
    /// Handles /find (and /search alias) by scrolling the transcript to the first match.
    /// Supports /find next to jump to the next match of the previous query.
    /// Returns true when the input was a find command (handled or usage-only).
    /// </summary>
    private bool TryHandleFindInTranscript(string text)
    {
        if (!text.StartsWith('/')) return false;
        var trimmed = text.TrimStart('/');
        var spaceIdx = trimmed.IndexOf(' ');
        var name = (spaceIdx < 0 ? trimmed : trimmed[..spaceIdx]).ToLowerInvariant();
        if (name is not ("find" or "search")) return false;

        var query = spaceIdx < 0 ? string.Empty : trimmed[(spaceIdx + 1)..].Trim();
        Invoke(() =>
        {
            // /find next — continue previous search
            if (query.Equals("next", StringComparison.OrdinalIgnoreCase))
            {
                var (total, idx) = _shell.Transcript.FindNext();
                if (total == 0)
                    _shell.Transcript.AddSystem("没有上一次搜索记录。先用 /find <关键词> 搜索。");
                else
                    _shell.Transcript.AddSystem($"第 {idx + 1}/{total} 处匹配");
                return;
            }

            if (string.IsNullOrEmpty(query))
            {
                _shell.Transcript.AddSystem("用法: /find <关键词> — 搜索 · /find -r <正则> — 正则搜索 · /find next — 下一个匹配");
                return;
            }

            // /find -r <regex> — regex search
            if (query.StartsWith("-r ", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = query[3..].Trim();
                if (string.IsNullOrEmpty(pattern))
                {
                    _shell.Transcript.AddSystem("用法: /find -r <正则表达式>");
                    return;
                }
                try
                {
                    var (matchTotal, matchIdx) = _shell.Transcript.SearchAndScrollRegex(pattern);
                    if (matchTotal == 0)
                    {
                        _shell.Transcript.AddSystem($"未找到匹配 /{pattern}/ 的内容");
                        _shell.Transcript.ClearSearchHighlight();
                    }
                    else
                        _shell.Transcript.AddSystem($"找到 {matchTotal} 处匹配，已跳转到第 {matchIdx + 1} 处 · /find next 下一个");
                }
                catch (RegexParseException)
                {
                    _shell.Transcript.AddSystem($"无效的正则表达式: {pattern}");
                }
                return;
            }

            var (matchTotal2, matchIdx2) = _shell.Transcript.SearchAndScroll(query);
            if (matchTotal2 == 0)
            {
                _shell.Transcript.AddSystem($"未找到匹配 \"{query}\" 的内容");
                _shell.Transcript.ClearSearchHighlight();
            }
            else
                _shell.Transcript.AddSystem($"找到 {matchTotal2} 处匹配，已跳转到第 {matchIdx2 + 1} 处 · /find next 下一个");
        });
        return true;
    }

    /// <summary>
    /// Handles /diff (no args) by opening the Review overlay.
    /// Returns true when the input was a bare /diff command.
    /// </summary>
    private bool TryHandleDiffOverlay(string text)
    {
        if (!text.StartsWith('/')) return false;
        var trimmed = text.TrimStart('/');
        var spaceIdx = trimmed.IndexOf(' ');
        var name = (spaceIdx < 0 ? trimmed : trimmed[..spaceIdx]).ToLowerInvariant();
        if (name != "diff") return false;

        // /diff --staged <file> or /diff <file> → text output fallback (non-overlay)
        var hasFileOrFlag = spaceIdx > 0 && trimmed[(spaceIdx + 1)..].Trim().Length > 0;
        if (hasFileOrFlag) return false;

        Invoke(() =>
        {
            _shell.Transcript.AddSystem("打开变更审查");
            _ = _shell.ShowReviewOverlayAsync();
        });
        return true;
    }
}
