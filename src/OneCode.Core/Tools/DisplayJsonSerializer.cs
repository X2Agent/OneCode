using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace OneCode.Core.Tools;

/// <summary>
/// JSON formatting for human-facing logs and TUI content.
/// Keeps Unicode characters readable while preserving valid JSON escaping.
/// </summary>
public static class DisplayJsonSerializer
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
            return JsonSerializer.Serialize(
                document.RootElement,
                writeIndented ? IndentedOptions : CompactOptions);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    /// <summary>
    /// Normalizes tool input/result text for human-facing output.
    /// Valid JSON is formatted with readable Unicode. JSON encoded as a string is
    /// unwrapped once, while mixed plain text only decodes valid Unicode escape
    /// sequences and leaves all other backslashes unchanged.
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

                return JsonSerializer.Serialize(
                    RewriteForDisplay(root.RootElement),
                    writeIndented ? IndentedOptions : CompactOptions);
            }
        }

        return DecodeUnicodeEscapes(value);
    }

    /// <summary>
    /// 重建 JSON 树以供人读显示：字符串字段若整体是一个 JSON 文档则解包展开为结构
    /// （如工具把结果 JSON 序列化成字符串塞进 content 字段），否则解码其中的
    /// \uXXXX 转义，保证中文等非 ASCII 字符直接可读。其余值类型保持原样。
    /// </summary>
    private static JsonNode? RewriteForDisplay(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                    obj[property.Name] = RewriteForDisplay(property.Value);
                return obj;

            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    array.Add(RewriteForDisplay(item));
                return array;

            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                var trimmed = text.TrimStart();
                if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[')
                    && TryParseJson(trimmed, out var nested))
                {
                    using (nested)
                        return RewriteForDisplay(nested.RootElement);
                }

                return JsonValue.Create(DecodeUnicodeEscapes(text));

            default:
                return JsonNode.Parse(element.GetRawText());
        }
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
