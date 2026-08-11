using System.Text.Encodings.Web;

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
                    root.RootElement,
                    writeIndented ? IndentedOptions : CompactOptions);
            }
        }

        return DecodeUnicodeEscapes(value);
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

            builder.Append((char)codeUnit);
            i += 5;
        }

        return builder.ToString();
    }

    private static bool TryParseCodeUnit(ReadOnlySpan<char> value, out ushort codeUnit)
        => ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codeUnit);
}
