using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace OneCode.Core.Tools;

/// <summary>
/// JSON formatting for human-facing logs and TUI content.
/// Keeps Unicode characters readable while preserving valid JSON escaping.
/// </summary>
public static partial class DisplayJsonSerializer
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>
    /// 终端控制序列：CSI（<c>ESC [ … m</c> 等颜色/光标码）、OSC（<c>ESC ] … BEL/ST</c>）
    /// 及两字符 Fe 转义。工具结果为不可信外部输出（git 彩色 diff、进度条等），
    /// 原样落进 TUI 会渲染成 <c>\u001B[32m</c> 之类的乱码。
    /// </summary>
    [GeneratedRegex(@"\x1B(?:\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiEscapeRegex();

    public static string Serialize(object? value, bool writeIndented = false)
        => JsonSerializer.Serialize(value, writeIndented ? IndentedOptions : CompactOptions);

    public static string FormatIfJson(string value, bool writeIndented = true)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return value;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var builder = new System.Text.StringBuilder();
            WriteForDisplay(builder, document.RootElement, writeIndented, depth: 0);
            return builder.ToString();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    /// <summary>
    /// Normalizes tool input/result text for human-facing output.
    /// Valid JSON is reformatted with readable Unicode and real line breaks; JSON encoded
    /// as a string is unwrapped once; mixed plain text only decodes valid Unicode escape
    /// sequences and leaves all other backslashes unchanged.
    /// Terminal control sequences (ANSI colors) are stripped in every path.
    /// </summary>
    public static string NormalizeForDisplay(string value, bool writeIndented = true)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var trimmed = value.Trim();
        if (TryParseJson(trimmed, out var root))
        {
            using (root)
            {
                if (root.RootElement.ValueKind == JsonValueKind.String)
                {
                    var decoded = root.RootElement.GetString() ?? string.Empty;
                    return FormatIfJson(decoded, writeIndented);
                }

                var builder = new System.Text.StringBuilder();
                WriteForDisplay(builder, root.RootElement, writeIndented, depth: 0);
                return builder.ToString();
            }
        }

        return StripAnsiEscapeSequences(DecodeUnicodeEscapes(value));
    }

    /// <summary>
    /// 重建 JSON 树以供人读显示：字符串字段若整体是一个 JSON 文档则解包展开为结构
    /// （如工具把结果 JSON 序列化成字符串塞进 content 字段），否则解码其中的
    /// \uXXXX 转义并剥离 ANSI 序列。其余值类型原样输出。
    ///
    /// 不使用 <c>JsonSerializer.Serialize</c> 回写：<c>UnsafeRelaxedJsonEscaping</c> 仍会把
    /// 字符串值内的真实换行重新转义成字面量 <c>\n</c>，令展开详情只能显示成单行乱码。
    /// 显示层需要真实换行/<c>Tab</c>，故此处自行写出结构符号与字符串值。
    /// </summary>
    private static void WriteForDisplay(
        System.Text.StringBuilder builder, JsonElement element, bool writeIndented, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (!element.EnumerateObject().Any())
                {
                    builder.Append("{}");
                    return;
                }

                builder.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!firstProperty)
                        builder.Append(',');
                    firstProperty = false;
                    AppendIndent(builder, writeIndented, depth + 1);
                    builder.Append(QuoteForDisplay(property.Name)).Append(':');
                    if (writeIndented)
                        builder.Append(' ');
                    WriteForDisplay(builder, property.Value, writeIndented, depth + 1);
                }
                AppendIndent(builder, writeIndented, depth);
                builder.Append('}');
                return;

            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToArray();
                if (items.Length == 0)
                {
                    builder.Append("[]");
                    return;
                }

                builder.Append('[');
                for (var i = 0; i < items.Length; i++)
                {
                    if (i > 0)
                        builder.Append(',');
                    AppendIndent(builder, writeIndented, depth + 1);
                    WriteForDisplay(builder, items[i], writeIndented, depth + 1);
                }
                AppendIndent(builder, writeIndented, depth);
                builder.Append(']');
                return;

            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                var nested = text.TrimStart();
                if (nested.Length > 0 && (nested[0] == '{' || nested[0] == '[')
                    && TryParseJson(nested, out var inner))
                {
                    using (inner)
                        WriteForDisplay(builder, inner.RootElement, writeIndented, depth);
                    return;
                }

                builder.Append(QuoteForDisplay(text));
                return;

            default:
                builder.Append(element.GetRawText());
                return;
        }
    }

    private static void AppendIndent(System.Text.StringBuilder builder, bool writeIndented, int depth)
    {
        if (!writeIndented)
            return;
        builder.Append('\n').Append(' ', depth * 2);
    }

    /// <summary>
    /// 以显示友好的形式写出 JSON 字符串值：解码 \uXXXX 转义、剥离 ANSI 序列，
    /// 仅保留 JSON 结构必需的反斜杠与引号转义；真实换行/制表符按原文保留，
    /// 供调用方按行渲染。孤立代理项替换为 U+FFFD（非法标量值会让 Terminal.Gui 渲染抛异常）。
    /// </summary>
    private static string QuoteForDisplay(string text)
    {
        var decoded = ReplaceLoneSurrogates(StripAnsiEscapeSequences(DecodeUnicodeEscapes(text)));
        var builder = new System.Text.StringBuilder(decoded.Length + 2);
        builder.Append('"');
        foreach (var ch in decoded)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>移除终端控制序列（ANSI 颜色/样式/光标码、OSC），只保留纯文本。</summary>
    public static string StripAnsiEscapeSequences(string value)
        => string.IsNullOrEmpty(value) || value.IndexOf('\u001b') < 0
            ? value
            : AnsiEscapeRegex().Replace(value, string.Empty);

    /// <summary>
    /// 把不成对的代理项替换为 U+FFFD：孤立代理项不是合法 Unicode 标量值，
    /// 原样进入 Terminal.Gui 的 AddStr 会抛 <see cref="ArgumentException"/> 整屏崩溃。
    /// </summary>
    private static string ReplaceLoneSurrogates(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var needsRewrite = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }
            if (char.IsSurrogate(c))
            {
                needsRewrite = true;
                break;
            }
        }

        if (!needsRewrite)
            return value;

        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[i + 1]);
                i++;
            }
            else
            {
                builder.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
            }
        }
        return builder.ToString();
    }

    private static bool TryParseJson(string value, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static string DecodeUnicodeEscapes(string value)
    {
        var firstEscape = value.IndexOf("\\u", StringComparison.OrdinalIgnoreCase);
        if (firstEscape < 0)
            return value;

        var builder = new System.Text.StringBuilder(value.Length);
        builder.Append(value, 0, firstEscape);

        for (var i = firstEscape; i < value.Length; i++)
        {
            if (value[i] != '\\'
                || i + 5 >= value.Length
                || (value[i + 1] != 'u' && value[i + 1] != 'U')
                || !TryParseCodeUnit(value.AsSpan(i + 2, 4), out var codeUnit))
            {
                builder.Append(value[i]);
                continue;
            }

            if (char.IsHighSurrogate((char)codeUnit)
                && i + 11 < value.Length
                && value[i + 6] == '\\'
                && (value[i + 7] == 'u' || value[i + 7] == 'U')
                && TryParseCodeUnit(value.AsSpan(i + 8, 4), out var lowCodeUnit)
                && char.IsLowSurrogate((char)lowCodeUnit))
            {
                builder.Append((char)codeUnit);
                builder.Append((char)lowCodeUnit);
                i += 11;
                continue;
            }

            // 不成对的代理项转义替换为 U+FFFD：孤立代理项非合法 Unicode 标量值，
            // 原样输出会令 Terminal.Gui 渲染抛 ArgumentException。
            builder.Append(char.IsSurrogate((char)codeUnit) ? '\uFFFD' : (char)codeUnit);
            i += 5;
        }

        return builder.ToString();
    }

    private static bool TryParseCodeUnit(ReadOnlySpan<char> value, out ushort codeUnit)
        => ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codeUnit);
}
