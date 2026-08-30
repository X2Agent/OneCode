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
}
