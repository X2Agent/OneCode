using Microsoft.Extensions.AI;

namespace OneCode.App.Services;

/// <summary>
/// 单次结构化 LLM 调用的结果。<see cref="Value"/> 非空表示已取得类型化结果；
/// 否则调用方应使用 <see cref="Text"/> 走自己的文本降级解析器。
/// </summary>
/// <param name="Value">类型化结果；未取得（含降级路径）时为 default。</param>
/// <param name="Text">原始响应文本，供降级解析与诊断使用。</param>
/// <param name="InputTokens">实际产生结果的那次调用的输入 token 数。</param>
/// <param name="OutputTokens">实际产生结果的那次调用的输出 token 数。</param>
/// <param name="ViaSchema">是否经由原生 schema 结构化请求取得 <see cref="Value"/>。</param>
public sealed record StructuredChatResult<T>(
    T? Value,
    string Text,
    long InputTokens,
    long OutputTokens,
    bool ViaSchema);

/// <summary>
/// 结构化请求 + 文本降级的统一 LLM 调用辅助（OneCode 版"结构化优先"范式，
/// 对齐 MAF <c>AIJudgeLoopEvaluator</c>：结构化请求失败时退回文本解析而非硬失败）。
///
/// <para><b>三段式</b>：①带 JSON schema 的结构化请求，<c>TryGetResult</c> 成功即返回类型化结果；
/// ②provider 忽略 schema 或输出带围栏时，用同一响应的文本走调用方降级解析器（不重试）；
/// ③结构化调用抛出非取消异常（已知成因：部分 OpenAI 兼容网关拒绝所有 response_format 变体）时，
/// 去掉 <c>ResponseFormat</c> 重试一次——提示词内已含 JSON 契约，该路径与纯 prompt-only 基线等价。</para>
///
/// <para><b>为何③不用 useJsonSchemaResponseFormat: false</b>：该取值仍会发送
/// <c>response_format: json_object</c>（M.E.AI 源码实证），对拒绝所有变体的网关依然是被拒绝，
/// 因此完整重试必须使用不带任何 ResponseFormat 的裸调用。</para>
/// </summary>
public static class StructuredChatCall
{
    /// <summary>
    /// 执行一次结构化优先、文本降底的 LLM 调用。
    /// </summary>
    /// <typeparam name="T">期望的结构化输出类型。非对象类型（数组等）由框架自动包装对象壳并还原。</typeparam>
    /// <param name="chatClient">聊天客户端。</param>
    /// <param name="messages">请求消息。</param>
    /// <param name="options">聊天选项（ModelId / MaxOutputTokens 等）；内部会克隆，不会改动调用方实例。</param>
    /// <param name="serializerOptions">
    /// JSON 序列化选项（schema 生成与反序列化共用）。<b>必须显式指定
    /// <see cref="JsonSerializerOptions.TypeInfoResolver"/></b>——M.E.AI 的
    /// <c>GetResponseAsync&lt;T&gt;</c> 内部会调用 <c>MakeReadOnly()</c>，
    /// 未指定解析器的实例会在那里抛 <c>InvalidOperationException</c>，
    /// 且该异常会被③的宽捕获吞掉、伪装成"网关拒绝"。守卫在此提前失败。
    /// </param>
    /// <param name="logger">可选日志器；发生③重试时记 Warning。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<StructuredChatResult<T>> CallAsync<T>(
        IChatClient chatClient,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        JsonSerializerOptions serializerOptions,
        ILogger? logger,
        CancellationToken ct = default)
    {
        if (serializerOptions.TypeInfoResolver is null)
        {
            throw new ArgumentException(
                "serializerOptions must specify TypeInfoResolver: GetResponseAsync<T> calls " +
                "MakeReadOnly() internally, and the resulting exception would be swallowed by the " +
                "response-format rejection retry and masquerade as a gateway rejection.",
                nameof(serializerOptions));
        }

        // ① 结构化请求：原生 JSON schema（支持受限解码的模型获得约束）。
        try
        {
            var structured = await chatClient.GetResponseAsync<T>(
                messages, serializerOptions, options, useJsonSchemaResponseFormat: true, ct)
                .ConfigureAwait(false);

            if (structured.TryGetResult(out var typed))
            {
                return new StructuredChatResult<T>(
                    typed, structured.Text, InputTokens(structured), OutputTokens(structured), ViaSchema: true);
            }

            // ② 同响应文本降级：provider 忽略了 schema，或输出带 markdown 围栏。
            //    不重试——响应已在手，交给调用方的围栏/括号容错解析器。
            return new StructuredChatResult<T>(
                default, structured.Text, InputTokens(structured), OutputTokens(structured), ViaSchema: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ③ 硬拒绝重试：异常过滤取"所有非取消异常"——各 provider 的 400 拒绝没有统一异常类型
            //    （OpenAI 为 ClientResultException，兼容网关各异），窄过滤会把陌生错误码的网关重新推回
            //    硬失败。宽过滤的最坏后果是多一次 doomed 请求后落入既有失败路径，与纯 prompt-only 行为一致。
            //    取消传播给调用方，不过滤、不重试。
            logger?.LogWarning(
                ex,
                "Structured output request (response_format) was rejected; retrying without it " +
                "on the prompt-only JSON baseline.");
        }

        var plainOptions = options?.Clone() ?? new ChatOptions();
        plainOptions.ResponseFormat = null;
        var plain = await chatClient.GetResponseAsync(messages, plainOptions, ct).ConfigureAwait(false);

        // 无 schema 时模型不知道对象壳约定，输出即目标形状本身，直接解析（不带 IsWrappedInObject）。
        new ChatResponse<T>(plain, serializerOptions).TryGetResult(out var parsed);

        return new StructuredChatResult<T>(
            parsed, plain.Text, InputTokens(plain), OutputTokens(plain), ViaSchema: false);
    }

    private static long InputTokens(ChatResponse response) => (long)(response.Usage?.InputTokenCount ?? 0);

    private static long OutputTokens(ChatResponse response) => (long)(response.Usage?.OutputTokenCount ?? 0);
}
