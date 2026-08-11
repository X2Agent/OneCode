namespace OneCode.Infrastructure.Text;

/// <summary>
/// 文件编码和行尾统一处理——EditTool、WriteTool、ApplyWorkspaceEditTool 的共享实现。
/// 避免三处工具各自维护 DetectEncoding / DetectLineEndingStyle / NormalizeLineEndings 导致行为漂移。
/// </summary>
public static class FileEncodingHelper
{
    public enum LineEndingStyle
    {
        Preserve,
        Lf,
        Crlf,
    }

    /// <summary>
    /// 从原始字节检测文件编码（UTF-8 BOM / UTF-16 LE / UTF-16 BE / 默认 UTF-8 无 BOM）。
    /// </summary>
    public static Encoding DetectEncoding(byte[] bytes, out int bomLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        { bomLength = 3; return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true); }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        { bomLength = 2; return Encoding.Unicode; }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        { bomLength = 2; return Encoding.BigEndianUnicode; }

        bomLength = 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    /// <summary>
    /// 检测文本内容的行尾风格。无换行返回 Preserve；CRLF 数 >= LF 数返回 Crlf。
    /// </summary>
    public static LineEndingStyle DetectLineEndingStyle(string content)
    {
        var crlfCount = 0;
        var lfCount = 0;

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            if (i > 0 && content[i - 1] == '\r')
                crlfCount++;
            else
                lfCount++;
        }

        if (crlfCount == 0 && lfCount == 0)
            return LineEndingStyle.Preserve;

        return crlfCount >= lfCount ? LineEndingStyle.Crlf : LineEndingStyle.Lf;
    }

    /// <summary>
    /// 将文本行尾标准化为目标风格。Preserve 保持原样。
    /// </summary>
    public static string NormalizeLineEndings(string text, LineEndingStyle style)
    {
        if (style == LineEndingStyle.Preserve)
            return text;
        var lf = text.Replace("\r\n", "\n");
        return style == LineEndingStyle.Crlf ? lf.Replace("\n", "\r\n") : lf;
    }

    /// <summary>
    /// 读取文件字节，返回解码后的内容、检测到的编码和行尾风格。
    /// </summary>
    public static async Task<(string Content, Encoding Encoding, LineEndingStyle LineEndings)>
        ReadWithEncodingAsync(string path, CancellationToken ct = default)
    {
        var rawBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var encoding = DetectEncoding(rawBytes, out var bomLength);
        var content = encoding.GetString(rawBytes, bomLength, rawBytes.Length - bomLength);
        var lineEndings = DetectLineEndingStyle(content);
        return (content, encoding, lineEndings);
    }

    /// <summary>
    /// 以原始编码和 BOM 写回文件。
    /// </summary>
    public static async Task WriteWithEncodingAsync(
        string path, string content, Encoding encoding, CancellationToken ct = default)
    {
        var preamble = encoding.GetPreamble();
        var bytes = encoding.GetBytes(content);
        var combined = new byte[preamble.Length + bytes.Length];
        preamble.CopyTo(combined, 0);
        bytes.CopyTo(combined, preamble.Length);
        await File.WriteAllBytesAsync(path, combined, ct).ConfigureAwait(false);
    }
}
