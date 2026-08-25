namespace OneCode.Infrastructure.Ai;

/// <summary>
/// Provider 返回 HTTP 200 但 <c>choices</c> 为空（或缺失）时抛出。
/// OpenAI 官方 SDK 在反序列化这种响应时会于 <c>ChatCompletion.get_Role()</c>
/// 内部抛出 <c>ArgumentOutOfRangeException</c>（空 choices 列表索引越界）——
/// OpenRouter 免费模型在上游过载/内容过滤时经常返回这种"成功"响应。
/// 本异常被 <see cref="RetryOnOverloadChatClient"/> 视为可重试的瞬时上游错误。
/// </summary>
public sealed class EmptyChoicesResponseException(string message) : Exception(message);