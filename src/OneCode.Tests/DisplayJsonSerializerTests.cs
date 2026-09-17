using OneCode.Core.Tools;

namespace OneCode.Tests;

public sealed class DisplayJsonSerializerTests
{
    [Fact]
    public void Serialize_ChineseText_KeepsReadableUnicode()
    {
        var result = DisplayJsonSerializer.Serialize(new { question = "请选择产品形态" });

        result.Should().Contain("请选择产品形态");
        result.Should().NotContainEquivalentOf("\\u8BF7");
    }

    [Fact]
    public void FormatIfJson_EscapedChinese_DecodesForDisplay()
    {
        const string json = "{\"question\":\"\\u8bf7\\u9009\\u62e9\\u4ea7\\u54c1\\u5f62\\u6001\"}";

        var result = DisplayJsonSerializer.FormatIfJson(json);

        result.Should().Contain("请选择产品形态");
        result.Should().NotContainEquivalentOf("\\u8bf7");
    }

    [Fact]
    public void FormatIfJson_NonJson_ReturnsOriginalText()
    {
        const string text = "普通中文结果";

        DisplayJsonSerializer.FormatIfJson(text).Should().Be(text);
    }

    [Fact]
    public void NormalizeForDisplay_MixedText_DecodesUnicodeEscapes()
    {
        const string text = "错误：\\u8bf7\\u91cd\\u8bd5，path=C:\\\\temp";

        var result = DisplayJsonSerializer.NormalizeForDisplay(text, writeIndented: false);

        result.Should().Be("错误：请重试，path=C:\\\\temp");
    }

    [Fact]
    public void NormalizeForDisplay_EncodedJsonString_UnwrapsAndFormatsJson()
    {
        const string value = "\"{\\\"message\\\":\\\"\\u6210\\u529f\\\"}\"";

        var result = DisplayJsonSerializer.NormalizeForDisplay(value, writeIndented: false);

        result.Should().Be("{\"message\":\"成功\"}");
    }

    [Fact]
    public void NormalizeForDisplay_NestedJsonStringField_UnwrapsAndDecodesChinese()
    {
        // 复刻 WebSearch 工具结果形态：外层 JSON 的 content 字段是内层 JSON 的
        // 序列化字符串，中文以 \uXXXX 转义存在——展开后中文必须直接可读。
        const string value = """{"content":"{\"provider\":\"duckduckgo\",\"query\":\"\u5B59\u5B87\u6668\",\"results\":[]}","isError":false}""";

        var result = DisplayJsonSerializer.NormalizeForDisplay(value, writeIndented: false);

        result.Should().Contain("孙宇晨");
        result.Should().NotContainEquivalentOf("\\u5B59");
    }

    [Fact]
    public void NormalizeForDisplay_JsonObjectWithEscapedChineseField_DecodesInPlace()
    {
        // 字符串字段不是合法 JSON（无法解包结构）时，仅解码 \uXXXX 转义。
        const string value = """{"query":"\u5B59\u5B87\u6668 的搜索结果"}""";

        var result = DisplayJsonSerializer.NormalizeForDisplay(value, writeIndented: false);

        result.Should().Contain("孙宇晨");
    }

    [Fact]
    public void NormalizeForDisplay_NonJsonValueTypes_Preserved()
    {
        const string value = """{"count":3,"ratio":0.5,"ok":true,"empty":null,"list":[1,"two"]}""";

        var result = DisplayJsonSerializer.NormalizeForDisplay(value, writeIndented: false);

        result.Should().Be("""{"count":3,"ratio":0.5,"ok":true,"empty":null,"list":[1,"two"]}""");
    }

    [Fact]
    public void NormalizeForDisplay_PairedSurrogateEscapes_CombineToScalar()
    {
        // 成对的 \uD83D\uDE00 转义必须合并为合法标量值（😀），不得拆开输出。
        const string text = "emoji：\\uD83D\\uDE00";

        var result = DisplayJsonSerializer.NormalizeForDisplay(text, writeIndented: false);

        result.Should().Be("emoji：\U0001F600");
    }

    [Fact]
    public void NormalizeForDisplay_UnpairedSurrogateEscape_ReplacedNotEmitted()
    {
        // 回归：工具结果含不成对 \uD83D 转义时，旧实现原样输出孤立代理项，
        // 进入 MessageListView 渲染令 Terminal.Gui 抛 ArgumentException 整屏崩溃。
        const string text = "错误：\\uD83D\\uDE00\\uD83D 加载失败";

        var result = DisplayJsonSerializer.NormalizeForDisplay(text, writeIndented: false);

        result.Should().Be("错误：\U0001F600\uFFFD 加载失败");
    }

    [Fact]
    public void NormalizeForDisplay_EscapedNewline_DecodesToRealLineBreak()
    {
        // 展开详情按真实换行拆分渲染；JSON 字符串值里的 \n 必须解码为真实换行，
        // 否则 Bash 多行命令 / git diff 输出在展开时挤成一行乱码。
        const string value = """{"command":"git status --short\n git log --oneline"}""";

        var result = DisplayJsonSerializer.NormalizeForDisplay(value, writeIndented: false);

        result.Should().Contain("git status --short\n git log --oneline");
        result.Should().NotContain("\\n");
    }

    [Fact]
    public void NormalizeForDisplay_AnsiColorEscapes_StrippedToPlainText()
    {
        // git 彩色 diff / 进度条等外部输出带 \u001B[…m 控制序列，展开时必须剥离，
        // 否则渲染成 "\u001B[32m" 之类的乱码并干扰换行布局。
        const string value = """{"output":"\u001b[32m+ added\u001b[0m\n\u001b[31m- removed\u001b[0m"}""";

        var result = DisplayJsonSerializer.NormalizeForDisplay(value, writeIndented: false);

        result.Should().Contain("+ added");
        result.Should().Contain("- removed");
        result.Should().NotContain("\u001b");
    }

    [Fact]
    public void NormalizeForDisplay_EscapedBackslashBeforeLetterN_KeptAsLiteral()
    {
        // 只展开 JSON 解码出的真实换行；\\n（转义反斜杠 + 字母 n）必须保持字面量，
        // 否则正则/代码片段里的 "\\n" 会被错误地改成换行。
        const string value = """{"pattern":"a\\nb"}""";

        var result = DisplayJsonSerializer.NormalizeForDisplay(value, writeIndented: false);

        result.Should().Be("""{"pattern":"a\\nb"}""");
        result.Should().NotContain("\n");
    }
}
