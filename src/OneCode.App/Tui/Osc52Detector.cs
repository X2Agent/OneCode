using System.Text.RegularExpressions;

namespace OneCode.App.Tui;

/// <summary>
/// Detects and decodes OSC 52 escape sequences used by terminals
/// for clipboard operations including image paste.
///
/// Format: ESC ] 52 ; Pc ; &lt;base64&gt; ST  (or BEL)
///   Pc  — clipboard selection: c (system), p (primary), q (secondary)
///   Data is base64-encoded; for images it's the raw image bytes.
/// </summary>
public static partial class Osc52Detector
{
    // OSC 52 escape sequence pattern
    // Matches: ESC ] 52 ; [cpqs]? ; [A-Za-z0-9+/=]+ (ST | BEL)
    [GeneratedRegex(
        @"\x1b\]52;[cpqs]?;[A-Za-z0-9+/=]+(?:\x1b\\|\x07)")]
    private static partial Regex Osc52Pattern { get; }

    /// <summary>
    /// Check if the input contains an OSC 52 escape sequence and extract it.
    /// Returns null if no OSC 52 sequence is found.
    /// </summary>
    /// <param name="logger">
    /// Optional logger — when supplied, base64 decode failures are logged at Debug level
    /// so callers can distinguish "no sequence" from "malformed sequence".
    /// </param>
    public static Osc52Data? Detect(string input, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(input))
            return null;

        var match = Osc52Pattern.Match(input);
        if (!match.Success)
            return null;

        var sequence = match.Value;

        // Extract payload: everything between the second ';' and the terminator
        var semicolon2 = sequence.IndexOf(';', sequence.IndexOf(';') + 1);
        if (semicolon2 < 0)
            return null;

        var payload = sequence[(semicolon2 + 1)..];
        // Strip the terminator (ST = ESC \ or BEL)
        if (payload.EndsWith("\x1b\\", StringComparison.Ordinal))
            payload = payload[..^2];
        else if (payload.EndsWith('\x07'))
            payload = payload[..^1];

        try
        {
            var data = Convert.FromBase64String(payload);
            return new Osc52Data(data, GuessContentType(data));
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex,
                "OSC 52 sequence detected but base64 payload could not be decoded (length {Length})", payload.Length);
            return new Osc52Data([], "unknown");
        }
    }

    /// <summary>
    /// Strip OSC 52 sequences from text, returning clean user-readable content.
    /// </summary>
    public static string StripOsc52(string input)
    {
        return Osc52Pattern.Replace(input, "");
    }

    private static string GuessContentType(byte[] data)
    {
        if (data.Length < 4)
            return "unknown";

        // Magic bytes for common image formats
        if (data[0] == 0xFF && data[1] == 0xD8) return "image/jpeg";
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return "image/gif";
        if (data[0] == 0x42 && data[1] == 0x4D) return "image/bmp";
        if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46) return "image/webp";
        if (data[0] == '<' && data[1] == 's' && data[2] == 'v' && data[3] == 'g') return "image/svg+xml";

        // Plain text fallback
        if (data.All(b => b >= 32 || b is 9 or 10 or 13))
            return "text/plain";

        return "application/octet-stream";
    }
}

/// <summary>
/// Decoded OSC 52 data from a terminal clipboard paste.
/// </summary>
/// <param name="Data">Raw bytes from the base64 payload.</param>
/// <param name="ContentType">Detected MIME type (e.g., "image/png", "text/plain").</param>
public sealed record Osc52Data(byte[] Data, string ContentType)
{
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public bool IsText => ContentType == "text/plain";
}
