using System.Text.Json;
using System.Text.Json.Serialization;
using OneCode.Core.Domain;

namespace OneCode.App.Session;

internal sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
    private static readonly JsonSerializerOptions InnerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public override ContentBlock? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        return type switch
        {
            "text" => JsonSerializer.Deserialize<TextBlock>(root.GetRawText(), InnerOptions),
            "tool_use" => JsonSerializer.Deserialize<ToolUseBlock>(root.GetRawText(), InnerOptions),
            "thinking" => JsonSerializer.Deserialize<ThinkingBlock>(root.GetRawText(), InnerOptions),
            "redacted_thinking" => JsonSerializer.Deserialize<RedactedThinkingBlock>(root.GetRawText(), InnerOptions),
            _ => InferFromProperties(root),
        };
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value switch
        {
            TextBlock => "text",
            ToolUseBlock => "tool_use",
            ThinkingBlock => "thinking",
            RedactedThinkingBlock => "redacted_thinking",
            _ => "unknown",
        });

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, value.GetType(), InnerOptions));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("type"))
                continue;
            prop.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static ContentBlock InferFromProperties(JsonElement root)
    {
        if (root.TryGetProperty("text", out _) && !root.TryGetProperty("name", out _))
            return JsonSerializer.Deserialize<TextBlock>(root.GetRawText(), InnerOptions)
                ?? throw new JsonException("Text block payload is null.");
        if (root.TryGetProperty("name", out _))
            return JsonSerializer.Deserialize<ToolUseBlock>(root.GetRawText(), InnerOptions)
                ?? throw new JsonException("Tool use block payload is null.");
        if (root.TryGetProperty("thinking", out _))
            return JsonSerializer.Deserialize<ThinkingBlock>(root.GetRawText(), InnerOptions)
                ?? throw new JsonException("Thinking block payload is null.");
        if (root.TryGetProperty("data", out _))
            return JsonSerializer.Deserialize<RedactedThinkingBlock>(root.GetRawText(), InnerOptions)
                ?? throw new JsonException("Redacted thinking block payload is null.");
        throw new JsonException("Unknown content block shape.");
    }
}
