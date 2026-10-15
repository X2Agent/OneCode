using System.Net;

namespace OneCode.Infrastructure.Middleware;

/// <summary>
/// PromptTooLong 检测器 — 统一 Run 级的 prompt-too-long 异常/错误检测逻辑。
///
/// <para>
/// <b>层次定位</b>：PromptTooLong 是<b>模型推理阶段</b>的错误，异常直接
/// 冒泡到 <c>agent.RunAsync</c>/<c>agent.RunStreamingAsync</c>，属于 <b>Run 级</b>关注点。
/// 工具层不经过模型推理异常，故无需在工具层检测 prompt_too_long。
/// </para>
///
/// <para>
/// <b>检测维度</b>：
/// <list type="bullet">
///   <item>HTTP 413 (RequestEntityTooLarge) — 部分 provider 返回此状态码</item>
///   <item>异常消息关键词：<c>prompt is too long</c> / <c>prompt_too_long</c> /
///   <c>context_length_exceeded</c></item>
/// </list>
/// </para>
/// </summary>
public static class PromptTooLongDetector
{
    private static readonly string[] s_keywords =
    [
        "prompt is too long",
        "prompt_too_long",
        "context_length_exceeded",
    ];

    /// <summary>
    /// 判断异常是否为 prompt-too-long 错误。
    /// 覆盖两个维度：HTTP 413 (RequestEntityTooLarge) 与异常消息关键词。
    /// 单一入口按运行时类型分派，避免调用方因静态类型为 <see cref="Exception"/>
    /// 而漏掉状态码维度（413 无关键词的失败同样可经 /compact 自愈）。
    /// </summary>
    public static bool IsPromptTooLong(Exception ex)
        => ex is HttpRequestException { StatusCode: HttpStatusCode.RequestEntityTooLarge }
            || ContainsKeyword(ex.Message);

    /// <summary>
    /// 判断文本是否包含 prompt-too-long 关键词。
    /// </summary>
    public static bool ContainsKeyword(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var keyword in s_keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
