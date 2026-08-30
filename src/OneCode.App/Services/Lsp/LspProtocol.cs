namespace OneCode.App.Services.Lsp;

/// <summary>
/// LSP 协议的 JSON-RPC 帧编解码与工具函数（Content-Length 帧格式）。
/// 无状态，独立于 <see cref="LspClient"/> 便于复用与单测。
/// </summary>
internal static class LspProtocol
{
    // Cloned JsonElements outlive their parent JsonDocument, so these are safe to
    // reuse as default parameter values without disposing.
    public static readonly JsonElement EmptyObject = CreateEmptyObject();
    public static readonly JsonElement EmptyNull = CreateEmptyNull();

    /// <summary>
    /// 从 LSP 协议头文本中解析 Content-Length 值。
    /// LSP 协议头格式固定为 <c>Content-Length: &lt;digits&gt;\r\n</c>。
    /// 用字符串查找替代正则，更轻量且无 ReDoS 隐患。
    /// </summary>
    /// <returns>解析成功返回 Content-Length 值；找不到或解析失败返回 null。</returns>
    public static int? TryParseContentLength(string headerText)
    {
        const string Marker = "Content-Length:";
        var markerIndex = headerText.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var i = markerIndex + Marker.Length;

        // 跳过标记后的空白字符（空格、制表符）
        while (i < headerText.Length && (headerText[i] == ' ' || headerText[i] == '\t'))
            i++;

        var start = i;
        while (i < headerText.Length && headerText[i] >= '0' && headerText[i] <= '9')
            i++;

        if (i == start)
            return null;  // 标记后没有数字

        var numberSpan = headerText.AsSpan(start, i - start);
        if (int.TryParse(numberSpan, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength))
            return contentLength;

        return null;
    }

    /// <summary>
    /// Normalize a JSON-RPC <c>id</c> value to the key used for pending requests.
    /// String ids must use <see cref="JsonElement.GetString"/> — <see cref="JsonElement.GetRawText"/>
    /// includes surrounding quotes and would never match the unquoted key stored at send time.
    /// </summary>
    public static string ToPendingRequestKey(JsonElement id) =>
        id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? "",
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.Null => "null",
            _ => id.GetRawText(),
        };

    /// <summary>
    /// 将对象序列化为 JSON 并以 Content-Length 帧写入流（LSP 帧格式）。
    /// </summary>
    public static async Task WriteFrameAsync(Stream stream, object message)
    {
        var json = JsonSerializer.Serialize(message);
        var contentBytes = System.Text.Encoding.UTF8.GetBytes(json);

        var header = $"Content-Length: {contentBytes.Length}\r\n\r\n";
        var headerBytes = System.Text.Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes).ConfigureAwait(false);
        await stream.WriteAsync(contentBytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 从流中读取一个完整的 Content-Length 帧，返回 JSON 载荷文本。
    /// EOF 或截断的流返回 null。
    /// </summary>
    public static async Task<string?> ReadFrameAsync(Stream stream)
    {
        var oneByte = new byte[1];
        List<byte> headerBuffer = [];

        while (true)
        {
            var read = await stream.ReadAsync(oneByte, 0, 1).ConfigureAwait(false);
            if (read == 0)
                return null; // EOF

            var b = oneByte[0];

            // Phase 1: accumulate header bytes until \r\n\r\n terminator.
            headerBuffer.Add(b);
            if (headerBuffer.Count >= 4 &&
                headerBuffer[^4] == '\r' && headerBuffer[^3] == '\n' &&
                headerBuffer[^2] == '\r' && headerBuffer[^1] == '\n')
            {
                var headerText = System.Text.Encoding.ASCII.GetString(headerBuffer.ToArray());
                var contentLength = TryParseContentLength(headerText);
                headerBuffer.Clear();
                if (contentLength.HasValue)
                {
                    // Phase 2: read exactly contentLength bytes of JSON payload.
                    var content = new byte[contentLength.Value];
                    var offset = 0;
                    while (offset < contentLength.Value)
                    {
                        var n = await stream.ReadAsync(content, offset, contentLength.Value - offset).ConfigureAwait(false);
                        if (n == 0)
                            break; // EOF mid-content
                        offset += n;
                    }

                    if (offset < contentLength.Value)
                        return null; // truncated stream

                    return System.Text.Encoding.UTF8.GetString(content);
                }
            }
        }
    }

    private static JsonElement CreateEmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static JsonElement CreateEmptyNull()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }
}
