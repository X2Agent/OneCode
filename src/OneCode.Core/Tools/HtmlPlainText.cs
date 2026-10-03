using System.Net;
using System.Text.RegularExpressions;

namespace OneCode.Core.Tools;

/// <summary>
/// HTML 片段转纯文本：剥标签 + 解码实体 + 空白折叠。
/// 搜索源（Tavily 等）的 snippet 常夹带站点页脚的原始 HTML
/// （&lt;footer&gt;、&lt;p style=...&gt;），原样进 TUI 详情是一片标签噪音；
/// 展示层与数据入口共用本剥离，保证历史回放的旧数据同样干净。
/// </summary>
public static partial class HtmlPlainText
{
    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    /// <summary>剥离标签、解码 HTML 实体并折叠空白；null/空白输入返回空串。</summary>
    public static string Strip(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = TagRegex().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        return WhitespaceRunRegex().Replace(text, " ").Trim();
    }
}
