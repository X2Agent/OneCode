using System.Text;
using OneCode.Core.Keybindings;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Commands;

/// <summary>
/// Manages custom keybindings for CLI interactions.
///
/// Allows users to customize keyboard shortcuts by editing ~/.onecode/keybindings.json.
/// The configuration supports:
/// - Global bindings (apply everywhere)
/// - Context-specific bindings (Chat, Autocomplete)
/// - Override of default keybindings
/// - Chord support (e.g., Ctrl+X Ctrl+K)
///
/// On first run, writes a JSON Schema file and creates a template keybindings.json
/// with a $schema reference pointing to the local schema file for editor IntelliSense.
/// </summary>
public sealed class KeybindingsCommand : Command
{
    public override string Name => "keybindings";
    public override string Description => "View or customize keyboard shortcuts by editing keybindings.json";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[list|validate|open|reset]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var keybindingsPath = GetKeybindingsPath();
        var schemaPath = GetSchemaPath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keybindingsPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(schemaPath)!);

            // Handle subcommands
            if (args.Length > 0)
            {
                return args[0].ToLowerInvariant() switch
                {
                    "list" => await ListDefaultBindingsAsync(),
                    "validate" => await ValidateKeybindingsAsync(keybindingsPath, ct),
                    "open" or "edit" => OpenInEditor(keybindingsPath),
                    "reset" => await ResetKeybindings(keybindingsPath, schemaPath, ct),
                    _ => CommandResult.Text($"Unknown keybindings subcommand: {args[0]}\nUse: list, validate, open, or reset")
                };
            }

            // Always refresh the schema file so it stays in sync with the current build.
            await WriteSchemaFileAsync(schemaPath, ct).ConfigureAwait(false);

            var fileExists = File.Exists(keybindingsPath);

            if (!fileExists)
            {
                var template = KeybindingSchema.GenerateTemplate(schemaPath);
                await File.WriteAllTextAsync(keybindingsPath, template, ct).ConfigureAwait(false);
            }

            return OpenInEditor(keybindingsPath, fileExists);
        }
        catch (Exception ex)
        {
            return CommandResult.Text(
                $"Keybindings error: {ex.Message}\n\n" +
                $"File: {keybindingsPath}\n\n" +
                "Common keybindings:\n" +
                "  Ctrl+C   Copy selected text\n" +
                "  Ctrl+D   Exit\n" +
                "  Ctrl+Shift+D  LSP diagnostics\n" +
                "  Ctrl+X Ctrl+K  Interrupt running query\n" +
                "  Up/Down  Navigate history\n" +
                "  Tab      Autocomplete or cycle mode\n" +
                "  Escape   Dismiss / clear input");
        }
    }

    /// <summary>
    /// Opens keybindings.json in the default editor.
    /// </summary>
    private static CommandResult OpenInEditor(string keybindingsPath, bool fileExists = true)
    {
        var editorOpened = TryOpenInEditor(keybindingsPath);

        var fileState = fileExists ? "Opened" : "Created";
        var message = editorOpened
            ? $"{fileState} {keybindingsPath}\nEdit and save to customize keybindings. Changes will reload automatically."
            : $"{fileState} {keybindingsPath}\n\nCould not open editor automatically. Open this file in your text editor to customize keybindings.";

        return CommandResult.Text(message);
    }

    /// <summary>
    /// Attempts to open file in default editor. Returns true if successful.
    /// </summary>
    private static bool TryOpenInEditor(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch
        {
            // Silently fail - user gets path info to open manually
            return false;
        }
    }

    /// <summary>
    /// Validates keybindings.json for common issues.
    /// </summary>
    private static async Task<CommandResult> ValidateKeybindingsAsync(string keybindingsPath, CancellationToken ct)
    {
        if (!File.Exists(keybindingsPath))
        {
            return CommandResult.Text($"Keybindings file not found: {keybindingsPath}");
        }

        try
        {
            var content = await File.ReadAllTextAsync(keybindingsPath, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Check for required "bindings" property
            if (!root.TryGetProperty("bindings", out var bindingsElement) ||
                bindingsElement.ValueKind != JsonValueKind.Array)
            {
                return CommandResult.Text("❌ Invalid format: Missing 'bindings' array\n\n" +
                    "Expected format:\n" +
                    "{\n" +
                    "  \"bindings\": [\n" +
                    "    {\n" +
                    "      \"context\": \"Global\",\n" +
                    "      \"bindings\": { \"ctrl+k\": \"action:name\" }\n" +
                    "    }\n" +
                    "  ]\n" +
                    "}");
            }

            var bindingCount = 0;
            List<string> issues = [];

            foreach (var block in bindingsElement.EnumerateArray())
            {
                if (!block.TryGetProperty("context", out _))
                {
                    issues.Add("  ⚠️  Binding block missing 'context' property");
                    continue;
                }

                if (block.TryGetProperty("bindings", out var bindings) &&
                    bindings.ValueKind == JsonValueKind.Object)
                {
                    bindingCount += bindings.EnumerateObject().Count();
                }
            }

            var result = $"✓ Valid keybindings file\n" +
                $"  Binding blocks: {bindingsElement.GetArrayLength()}\n" +
                $"  Total bindings: {bindingCount}";

            if (issues.Count > 0)
            {
                result += "\n\nWarnings:\n" + string.Join("\n", issues);
            }

            return CommandResult.Text(result);
        }
        catch (JsonException ex)
        {
            return CommandResult.Text($"❌ JSON parsing error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CommandResult.Text($"❌ Validation error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resets keybindings.json to default bindings, discarding all user customizations.
    /// Overwrites the existing file (or creates a new one) with the default template.
    /// FileSystemWatcher in KeybindingLoader picks up the change and hot-reloads.
    /// </summary>
    private static async Task<CommandResult> ResetKeybindings(string keybindingsPath, string schemaPath, CancellationToken ct)
    {
        // Ensure schema file is up to date so the $schema reference resolves.
        await WriteSchemaFileAsync(schemaPath, ct).ConfigureAwait(false);

        var template = KeybindingSchema.GenerateTemplate(schemaPath);
        await File.WriteAllTextAsync(keybindingsPath, template, ct).ConfigureAwait(false);

        return CommandResult.Text(
            $"✓ Reset to default keybindings\n" +
            $"  File: {keybindingsPath}\n" +
            "  All custom bindings discarded. Edit the file to customize again.");
    }

    /// <summary>
    /// Lists default keybindings derived from <see cref="KeybindingDefaults"/> plus
    /// hardcoded TUI keys. Must stay honest — no unimplemented shortcuts.
    /// </summary>
    private static Task<CommandResult> ListDefaultBindingsAsync()
    {
        var sb = new StringBuilder();

        foreach (var block in KeybindingDefaults.DefaultBindings)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{block.Context} Bindings:");
            foreach (var (key, action) in block.Bindings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (action is null) continue;
                var display = FormatBindingKey(key);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {display,-16} {action}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Hardcoded (not remappable via keybindings.json):");
        sb.AppendLine("  Tab              Accept completion / cycle mode");
        sb.AppendLine("  Ctrl+Left/Right  Cycle placeholder suggestions");
        sb.AppendLine("  /find <keyword>  Search transcript (slash command)");
        sb.AppendLine("  /diff            Review git changes overlay (slash command)");
        sb.AppendLine();
        sb.AppendLine("Use '/keybindings open' to edit custom bindings.");

        return Task.FromResult(CommandResult.Text(sb.ToString().TrimEnd()));
    }

    private static string FormatBindingKey(string key)
    {
        // "ctrl+shift+d" → "Ctrl+Shift+D"; chords keep space separation.
        return string.Join(' ', key.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => string.Join('+', part.Split('+')
                .Select(p => p.Length <= 1
                    ? p.ToUpperInvariant()
                    : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()))));
    }

    /// <summary>
    /// Writes the JSON Schema to disk so editors can provide IntelliSense for keybindings.json.
    /// </summary>
    private static async Task WriteSchemaFileAsync(string schemaPath, CancellationToken ct)
    {
        var schema = KeybindingSchema.GenerateSchema();
        await File.WriteAllTextAsync(schemaPath, schema, ct).ConfigureAwait(false);
    }

    private static string GetKeybindingsPath()
    {
        var home = PathsHelper.UserHome;
        return Path.Combine(home, Constants.App.ConfigDirName, "keybindings.json");
    }

    private static string GetSchemaPath()
    {
        var home = PathsHelper.UserHome;
        return Path.Combine(home, Constants.App.ConfigDirName, "schemas", KeybindingSchema.SchemaFileName);
    }
}
