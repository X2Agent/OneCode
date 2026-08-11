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
}
