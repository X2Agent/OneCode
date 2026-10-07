
namespace OneCode.Infrastructure.Ai;

/// <summary>
/// LLM 请求诊断：Debug 构建下把发往 OpenAI 兼容端点的请求行与请求体转储到 debug 日志
/// （超长截断）。管线位置在 ReasoningPassback 之后、Sanitizing 之前——转储的是
/// passback 注入后的最终请求体，供应商排障（空响应/畸形拒绝）时据此对照其他客户端
/// （opencode/Cline 等）的请求差异。
/// </summary>
/// <remarks>
/// 请求体包含系统提示、工具 schema 与对话内容，属敏感负载：仅在 Debug 级日志启用时
/// 输出（Release 构建不记录），且不入库不外传。
/// </remarks>
public sealed class OpenAiRequestDiagnosticsHandler(ILogger<OpenAiRequestDiagnosticsHandler> logger) : DelegatingHandler
{
    /// <summary>请求体转储上限（字符）。工具 schema 占大头，24KB 足够还原全部消息形状。</summary>
    internal const int MaxBodyChars = 24_576;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Debug)
            && request.Method == HttpMethod.Post
            && request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            logger.LogDebug(
                "LLM request {Method} {Url} (body {Chars} chars)\n{Body}",
                request.Method,
                request.RequestUri,
                body.Length,
                body.Length > MaxBodyChars ? body[..MaxBodyChars] + "\n...(truncated)" : body);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
