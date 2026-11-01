namespace OneCode.Tui;

/// <summary>
/// 转录行内展示文本的上限控制。
/// </summary>
/// <remarks>
/// 折叠行与展开详情都只展示有限内容（详情视图本身按行数截断），但 <c>ToolLineTag.Result</c> /
/// <c>ThinkingLineTag.Content</c> 会把工具结果与思考全文整份挂在行里，并随 <c>_lines</c> 与
/// 已提交日志闭包长期驻留——单次 150 KB 的工具结果乘以整个会话的工具行数即成为主要内存占用。
/// 这里在构建 tag 时就把原文裁到「详情视图能展示的上限之上留足余量」的长度，
/// 裁剪事实通过 <c>Truncated</c> 标记告知详情渲染，用户不会误以为这是完整内容。
/// </remarks>
internal static class TranscriptTextLimits
{
    /// <summary>
    /// 单个工具结果/思考块在行 tag 中保留的最大字符数。
    /// 详情视图按 60 行截断，每行按最窄 20 列计已足够；取 16K 让常见结果完全无损。
    /// </summary>
    internal const int MaxRetainedChars = 16 * 1024;

    /// <summary>按上限裁剪并返回是否发生了裁剪。</summary>
    internal static string Cap(string? text, out bool truncated)
    {
        if (string.IsNullOrEmpty(text))
        {
            truncated = false;
            return text ?? string.Empty;
        }

        if (text.Length <= MaxRetainedChars)
        {
            truncated = false;
            return text;
        }

        truncated = true;
        return text[..MaxRetainedChars];
    }

    /// <summary>
    /// 文件变更块的行数上限：整份 diff 常达数百上千行，逐行物化成转录行会让
    /// 一次大改写的驻留内存远超其余内容之和。返回保留行与被省略的行数。
    /// </summary>
    internal static (IReadOnlyList<string> Lines, int Hidden) CapLines(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0)
            return ([], 0);

        if (lines.Count <= MaxRetainedDiffLines)
            return (lines, 0);

        return (lines.Take(MaxRetainedDiffLines).ToArray(), lines.Count - MaxRetainedDiffLines);
    }

    /// <summary>单个文件变更块保留的增/删行数上限（每侧），超出部分只显示计数。</summary>
    internal const int MaxRetainedDiffLines = 200;
}
