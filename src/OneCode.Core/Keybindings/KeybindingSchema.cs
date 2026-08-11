namespace OneCode.Core.Keybindings;

/// <summary>
/// JSON Schema 定义，用于 keybindings.json 的验证和自动补全。
/// 生成符合 JSON Schema Draft 2020-12 规范的 Schema 文档。
/// </summary>
public static class KeybindingSchema
{
    /// <summary>
    /// Schema 文件名，写到 ~/.onecode/schemas/ 下。
    /// </summary>
    public const string SchemaFileName = "keybindings.schema.json";

    /// <summary>
    /// 生成 keybindings.json 的 JSON Schema 文档。
    /// </summary>
    public static string GenerateSchema()
    {
        var contexts = KeybindingDefaults.AllContexts;
        var actions = KeybindingDefaults.AllActions;

        var contextEnum = new List<object>();
        foreach (var ctx in contexts)
        {
            contextEnum.Add(ctx);
        }

        var actionEnum = new List<object>();
        foreach (var action in actions)
        {
            actionEnum.Add(action);
        }

        var schema = new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = SchemaFileName,
            ["title"] = "OneCode Keybindings Configuration",
            ["description"] = "Customize keyboard shortcuts by context. User bindings override defaults.",
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["$schema"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "JSON Schema URL for editor validation",
                },
                ["$docs"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "Documentation URL",
                },
                ["bindings"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["description"] = "Array of keybinding blocks by context",
                    ["items"] = CreateBlockSchema(contextEnum, actionEnum),
                },
            },
            ["required"] = new[] { "bindings" },
            ["additionalProperties"] = false,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };

        return JsonSerializer.Serialize(schema, options);
    }

    /// <summary>
    /// 生成带默认绑定的模板文件内容。
    /// </summary>
    /// <param name="schemaFilePath">本地 schema 文件的绝对路径，用于填充 $schema 字段（file:// URI）。为 null 时省略该字段。</param>
    public static string GenerateTemplate(string? schemaFilePath = null)
    {
        var config = new Dictionary<string, object?>();

        if (schemaFilePath is not null)
        {
            config["$schema"] = new Uri(schemaFilePath).AbsoluteUri;
        }

        config["bindings"] = KeybindingDefaults.DefaultBindings
            .Select(b => new Dictionary<string, object?>
            {
                ["context"] = b.Context,
                ["bindings"] = b.Bindings,
            })
            .ToList();

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        return JsonSerializer.Serialize(config, options) + "\n";
    }

    private static Dictionary<string, object> CreateBlockSchema(
        List<object> contextEnum, List<object> actionEnum)
    {
        return new Dictionary<string, object>
        {
            ["type"] = "object",
            ["description"] = "A block of keybindings for a specific context",
            ["properties"] = new Dictionary<string, object>
            {
                ["context"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["enum"] = contextEnum,
                    ["description"] = "UI context where these bindings apply. Global bindings work everywhere.",
                },
                ["bindings"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["description"] = "Map of keystroke patterns to actions",
                    ["additionalProperties"] = new Dictionary<string, object>
                    {
                        ["oneOf"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["enum"] = actionEnum,
                                ["description"] = "Standard action to trigger",
                            },
                            new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["pattern"] = "^command:[a-zA-Z0-9:\\-_]+$",
                                ["description"] = "Command binding (e.g., \"command:help\", \"command:compact\"). Executes the slash command as if typed.",
                            },
                            new Dictionary<string, object>
                            {
                                ["type"] = "null",
                                ["description"] = "Set to null to unbind a default shortcut",
                            },
                        },
                    },
                    ["propertyNames"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "Keystroke pattern (e.g., \"ctrl+k\", \"shift+tab\", \"ctrl+x ctrl+k\")",
                    },
                },
            },
            ["required"] = new[] { "context", "bindings" },
            ["additionalProperties"] = false,
        };
    }
}
