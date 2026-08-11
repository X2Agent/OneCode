namespace OneCode.App.Tui;

/// <summary>
/// Result of parsing tool-input JSON for approval / LSP display.
/// Distinguishes empty input (valid) from malformed JSON (fail closed).
/// </summary>
internal readonly record struct ToolInputParseResult(bool Ok, JsonElement Input, string? Error);

/// <summary>
/// Parses raw tool-input text without forging a successful empty object on JSON errors.
/// </summary>
internal static class ToolInputParser
{
    private const int ErrorPreviewMaxChars = 200;

    public static ToolInputParseResult Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new ToolInputParseResult(true, JsonSerializer.SerializeToElement(new { }), null);

        try
        {
            using var doc = JsonDocument.Parse(rawText);
            return new ToolInputParseResult(true, doc.RootElement.Clone(), null);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"ToolInputParser: invalid JSON: {ex.Message}");
            var preview = rawText.Length <= ErrorPreviewMaxChars
                ? rawText
                : rawText[..ErrorPreviewMaxChars] + "\u2026";
            return new ToolInputParseResult(
                false,
                JsonSerializer.SerializeToElement(new { }),
                preview);
        }
    }
}
