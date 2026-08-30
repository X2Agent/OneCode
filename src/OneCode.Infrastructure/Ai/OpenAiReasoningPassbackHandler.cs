namespace OneCode.Infrastructure.Ai;

/// <summary>
/// 请求体重写 handler：为 assistant 消息注入 <c>reasoning_content</c> 字段，
/// 满足 DeepSeek thinking 模式对工具调用轮次的推理回传校验。
/// 数据源为 <see cref="ReasoningPassbackChatClient"/> 经 AsyncLocal 侧信道设置的快照；
/// 快照不存在或不含推理文本时零开销透传。
/// </summary>
public sealed class OpenAiReasoningPassbackHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (ReasoningPassbackContext.Scope is { HasReasoning: true } scope
            && request.Method == HttpMethod.Post
            && request.Content is not null
            && string.Equals(
                request.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var patched = RewriteRequestBody(body, scope.AssistantReasonings);
            if (patched is not null)
                request.Content = new StringContent(patched, Encoding.UTF8, "application/json");
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按顺序为 <c>messages</c> 中缺失 <c>reasoning_content</c> 的 assistant 消息注入推理文本。
    /// 快照条目少于请求中的 assistant 消息时，超出部分不注入（保守对齐，宁可漏不错配）。
    /// body 非 JSON 对象或无需改动时返回 null（不重建请求体）。
    /// </summary>
    internal static string? RewriteRequestBody(string body, IReadOnlyList<string> assistantReasonings)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                var assistantIndex = 0;
                var patched = false;

                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name != "messages")
                    {
                        property.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    foreach (var message in messages.EnumerateArray())
                    {
                        if (!IsAssistantMessage(message))
                        {
                            message.WriteTo(writer);
                            continue;
                        }

                        var reasoning = assistantIndex < assistantReasonings.Count
                            ? assistantReasonings[assistantIndex]
                            : null;
                        assistantIndex++;

                        if (string.IsNullOrEmpty(reasoning)
                            || message.TryGetProperty("reasoning_content", out _))
                        {
                            message.WriteTo(writer);
                            continue;
                        }

                        patched = true;
                        WriteMessageWithReasoning(writer, message, reasoning);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();

                if (!patched)
                    return null;
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private static bool IsAssistantMessage(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("role", out var role)
        && role.ValueKind == JsonValueKind.String
        && string.Equals(role.GetString(), "assistant", StringComparison.Ordinal);

    private static void WriteMessageWithReasoning(Utf8JsonWriter writer, JsonElement message, string reasoning)
    {
        writer.WriteStartObject();
        var written = false;
        foreach (var property in message.EnumerateObject())
        {
            property.WriteTo(writer);
            // 紧跟 role 之后插入，保持请求体字段顺序与 DeepSeek 示例一致（语义上位置无关）。
            if (!written && property.NameEquals("role"))
            {
                writer.WritePropertyName("reasoning_content");
                writer.WriteStringValue(reasoning);
                written = true;
            }
        }

        if (!written)
        {
            // 异常形态（assistant 消息缺 role——IsAssistantMessage 已保证不会走到）：
            // 兜底追加，保证注入不丢。
            writer.WritePropertyName("reasoning_content");
            writer.WriteStringValue(reasoning);
        }

        writer.WriteEndObject();
    }
}
