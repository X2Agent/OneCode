using System.Net;
using System.Text.RegularExpressions;
using OneCode.WebUI.Services;

namespace OneCode.Tests;

/// <summary>
/// 流式尾段纯文本判定契约：判定结果决定字级错峰快速路径是否安全——
/// 误判为纯文本会让 Markdown 结构按字面渲染（强调符、链接、列表、代码块原样裸露），
/// 每个 false 用例都是一道会改变渲染结果的触发符守卫。
/// </summary>
public sealed class StreamingTextTokenizerTests
{
    [Theory]
    [InlineData("", true)]                       // 空尾段：无内容即无风险
    [InlineData("正在分析项目结构，请稍候。", true)]  // 中英混排常规增量
    [InlineData("第一行\n第二行", true)]           // 单换行是软换行：两路径渲染一致
    [InlineData("state-of-the-art 方案", true)]     // 连字符在词中：仅行首才是列表
    [InlineData("圆周率是 3.14", true)]             // 数字加点在句中：仅行首才是有序列表
    [InlineData("太棒了! 干得好", true)]             // 半角感叹号：仅 ![ 才是图片
    [InlineData("运行 `dotnet build`", false)]      // 行内代码
    [InlineData("这**很重要**", false)]             // 强调
    [InlineData("snake_case 文件", false)]          // 下划线
    [InlineData("见 [文档](x)", false)]            // 链接
    [InlineData("a < b", false)]                   // 裸 < 有自动链接 / 原始 HTML 风险
    [InlineData("A&B 联合", false)]                 // 实体转义
    [InlineData("@Program.cs 入口", false)]          // 邮箱式自动链接
    [InlineData("第一段\n\n第二段", false)]         // 段落边界
    [InlineData("行尾  \n硬换行", false)]           // 双空格硬换行
    [InlineData("访问 http://example.com", false)]  // 裸 URL
    [InlineData("见 www.example.com", false)]       // www 自动链接
    [InlineData("mailto:a@b.c", false)]             // mailto 自动链接
    [InlineData("# 标题", false)]                   // 行首标题
    [InlineData("> 引用", false)]                   // 行首引用
    [InlineData("- 列表项", false)]                  // 行首无序列表
    [InlineData("1. 步骤", false)]                  // 行首有序列表
    [InlineData("    缩进代码", false)]             // 4 空格缩进代码块
    [InlineData("x^2 上标", false)]                 // EmphasisExtras 上标
    [InlineData("~~删除~~", false)]                 // EmphasisExtras 删除线
    [InlineData("a == b", false)]                   // EmphasisExtras 高亮
    [InlineData("a++ 自增", false)]                  // EmphasisExtras 插入
    [InlineData("甲|乙", false)]                    // 管道表格
    [InlineData("![图](x)", false)]                 // 图片
    [InlineData("带回\r\n换行", false)]             // \r 行尾语义与原样输出不同
    [InlineData("拨打 tel:10086", false)]           // tel 自动链接
    [InlineData("1.", false)]                      // 行尾裸有序标记 = 空列表项
    [InlineData("\n- 列表项", false)]               // 开头空行不吞掉下一行的行首判定
    [InlineData("前\n \n# 标题", false)]            // 仅含空白的行同样是段落边界
    [InlineData("\n你好", true)]                    // 开头空行 Markdown 会忽略：仍是纯文本
    [InlineData("3.14 是圆周率", true)]              // 行首小数：标记后不是空白，不是列表
    public void IsPlainText_ClassifiesMarkdownTriggers(string tail, bool expected)
    {
        StreamingTextTokenizer.IsPlainText(tail).Should().Be(expected);
    }

    [Fact]
    public void Tokenize_CjkPerCharLatinPerWord()
    {
        var tokens = StreamingTextTokenizer.Tokenize("你好 abc 世界");

        tokens.Should().Equal(["你", "好 ", "abc ", "世", "界"]);
    }

    [Fact]
    public void Tokenize_WhitespaceAttachesToAdjacentToken()
    {
        var tokens = StreamingTextTokenizer.Tokenize("  leading and trailing  ");

        // 开头空白自成 token（保证拼回原文），结尾空白附着最后一个词。
        tokens.Should().Equal(["  ", "leading ", "and ", "trailing  "]);
    }

    [Theory]
    [InlineData("你好世界")]
    [InlineData("mixed 混合 text 文本!")]
    [InlineData("  ")]
    [InlineData("word")]
    [InlineData("你好 abc\tdef\r\n尾行")]
    public void Tokenize_ConcatPreservesOriginalText(string run)
    {
        // span 序列拼接必须逐字还原原文：丢空格或丢字会直接出现在聊天界面里。
        string.Concat(StreamingTextTokenizer.Tokenize(run)).Should().Be(run);
    }

    [Fact]
    public void IsPlainText_TrueImpliesRealRendererEmitsOnlyParagraphWithSameText()
    {
        // 漂移守卫：触发符清单与真实 Markdown 管线（Markdig 扩展集 + 消毒）各自演进，
        // 任何一侧变化都会让「判定纯文本」的输入在真实渲染里长出结构（列表 / 标题 / 链接…）。
        var renderer = new MarkdownRenderer();

        var drifted = ProbeSamples()
            .Where(StreamingTextTokenizer.IsPlainText)
            .Select(sample => (Sample: sample, Html: renderer.ToHtml(sample)))
            .Where(item => !IsPlainParagraph(item.Html, item.Sample))
            .Select(item => $"{Show(item.Sample)} => {Show(item.Html)}");

        // 拼成单个字符串断言：失败消息一次列全所有漂移样本，而不是只报第一条。
        string.Join("\n", drifted).Should().BeEmpty("这些输入走纯文本快速路径会按字面渲染，真实管线却会渲染出结构");
    }

    /// <summary>
    /// 探针样本：每个可打印 ASCII / 常见全角标点分别放在行首、句中、行尾、次行行首、空白行之后，
    /// 并叠加「后接空格」「前置数字」——Markdown 结构触发符只在特定位置才生效，单位置探针会漏。
    /// 末尾补多字符前缀（自动链接）这类单字符探针覆盖不到的形态。
    /// </summary>
    private static IEnumerable<string> ProbeSamples()
    {
        var probes = Enumerable.Range(0x20, 0x5F).Select(code => ((char)code).ToString())
            .Concat(["，", "。", "！", "？", "（", "）", "【", "】", "「", "」", "—", "…", "·"]);
        foreach (var probe in probes)
        {
            yield return $"{probe}尾";
            yield return $"{probe} 尾";
            yield return $"前{probe}尾";
            yield return $"前{probe}";
            yield return $"前\n{probe}尾";
            yield return $"前\n{probe} 尾";
            yield return $"\n{probe} 尾";
            yield return $"前\n \n{probe} 尾";
            yield return $"1{probe} 尾";
            yield return $"1{probe}";
            yield return $"1{probe}\n尾";
        }

        yield return "tel:10086";
        yield return "拨打 TEL:10086";
        yield return "ftp://host/file";
        yield return "WWW.EXAMPLE.COM";
    }

    private static bool IsPlainParagraph(string html, string source)
    {
        var tags = Regex.Matches(html, "</?([a-zA-Z][a-zA-Z0-9]*)").Select(match => match.Groups[1].Value);
        if (tags.Any(tag => tag != "p"))
            return false;

        var visible = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
        return CollapseWhitespace(visible) == CollapseWhitespace(source);
    }

    // HTML 折叠空白、Markdown 裁掉段首尾空白：比较「读者看到的文字」而不是空白个数。
    private static string CollapseWhitespace(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Show(string text) => text.Replace("\n", "\\n").Replace("\r", "\\r");
}
