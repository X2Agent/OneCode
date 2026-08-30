using System.Text.Json;
using OneCode.App.Tools;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// 工具结果编码回归守护：
/// 1. 工具结果的 JSON 必须经 relaxed encoder 生成——中文以原文发给模型与 TUI，
///    旧实现（WebFetch 的 ApplyPromptAndReturn / AskUserQuestion 的 answers）用默认
///    encoder，中文全部变成 \uXXXX，展开详情里呈现为一串反斜杠乱码。
/// 2. TUI 的 NormalizeForDisplay 能把 MAF 二次编码的包装（camelCase + 默认 encoder）
///    解包为可读的缩进 JSON——任何一层换成会转义中文的 encoder，此测试立即失败。
/// </summary>
public sealed class ToolResultEncodingTests
{
    private const string Prompt = "提取`这篇文章`中关于孙宇晨小作文事件的完整细节";

    [Fact]
    public void ApplyPromptAndReturn_KeepsCjkReadable()
    {
        var result = WebFetchTool.ApplyPromptAndReturn("正文内容", Prompt, startMs: 0);

        result.Content.Should().Contain("孙宇晨");
        result.Content.Should().NotContain("\\u", "默认 encoder 会把中文转成 \\uXXXX");
    }

    [Fact]
    public void MafWrappedToolResult_DisplaysReadableCjk()
    {
        // 模拟 MAF 链路：工具返回 relaxed JSON → MAF 以 Web 默认（camelCase + 默认
        // encoder）把 ToolResult 序列化进 FunctionResultContent.Result → TUI 展示。
        var inner = DisplayJsonSerializer.Serialize(new
        {
            bytes = 3643,
            code = 200,
            codeText = "OK",
            result = $"Content from URL (prompt: {Prompt}):\n\n## 标题",
        });
        var wrapped = JsonSerializer.Serialize(
            ToolResult.Success(inner),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var display = DisplayJsonSerializer.NormalizeForDisplay(wrapped);

        display.Should().Contain("孙宇晨");
        display.Should().NotContain("\\u");
    }

    [Fact]
    public void MafWrappedToolResult_DefaultEncoderWrapper_DisplaysReadableCjk()
    {
        // 变体：camelCase + 默认 encoder 的包装（引号转义为 \" 而非 \u0022）。
        var inner = DisplayJsonSerializer.Serialize(new
        {
            bytes = 3643,
            code = 200,
            codeText = "OK",
            result = $"Content from URL (prompt: {Prompt}):\n\n## 标题",
        });
        var wrapped = JsonSerializer.Serialize(
            new { content = inner, isError = false, severity = "info" },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var display = DisplayJsonSerializer.NormalizeForDisplay(wrapped);

        display.Should().Contain("孙宇晨");
        display.Should().NotContain("\\u");
    }
}
