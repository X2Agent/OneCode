using System.Text;
using OneCode.Core.Keybindings;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Keybindings;

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
/// Subcommands:
/// - list: show the effective bindings (defaults + user overrides; also the bare-command default)
/// - open: open keybindings.json in the default editor
/// - reset: restore the default template
///
/// Configuration is validated automatically on every load / hot-reload by
/// <see cref="KeybindingLoader"/>; warnings are surfaced by the list output.
/// On first run, writes a JSON Schema file and creates a template keybindings.json
/// with a $schema reference pointing to the local schema file for editor IntelliSense.
/// </summary>
public sealed class KeybindingsCommand(KeybindingLoader keybindingLoader) : Command
{
    public override string Name => "keybindings";
    public override string Description => "View or customize keyboard shortcuts by editing keybindings.json";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[list|open|reset]";

    /// <summary>本地文件操作，立即执行（TUI 中 /keybindings list 由此进入 overlay 拦截路径）。</summary>
    public override bool Immediate => true;

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
                    "list" => ListEffectiveBindings(),
                    "open" or "edit" => await OpenInEditorAsync(keybindingsPath, schemaPath, ct),
                    "reset" => await ResetKeybindings(keybindingsPath, schemaPath, ct),
                    _ => CommandResult.Text($"Unknown keybindings subcommand: {args[0]}\nUse: list, open, or reset")
                };
            }

            // 裸命令默认查看生效绑定（TUI 中被拦截弹 overlay，此处为文本兜底）。
            // 编辑配置走 /keybindings open。
            return ListEffectiveBindings();
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
    /// 刷新 schema 并确保 keybindings.json 存在（首次生成模板），然后打开编辑器。
    /// </summary>
    private static async Task<CommandResult> OpenInEditorAsync(string keybindingsPath, string schemaPath, CancellationToken ct)
    {
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
    /// Lists the effective bindings (defaults merged with user overrides) derived from
    /// <see cref="KeybindingLoader"/>, plus hardcoded TUI keys and validation warnings.
    /// Must stay honest — no unimplemented shortcuts.
    /// </summary>
    private CommandResult ListEffectiveBindings()
    {
        var loadResult = keybindingLoader.LoadSync();
        var views = KeybindingViewBuilder.Build(loadResult.Bindings);

        var sb = new StringBuilder();
        var currentContext = string.Empty;
        foreach (var view in views)
        {
            if (view.Context != currentContext)
            {
                currentContext = view.Context;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{currentContext} Bindings:");
            }

            var action = view.Source == KeybindingSource.Unbound
                ? "(unbound)"
                : view.Action ?? string.Empty;
            var mark = view.Source == KeybindingSource.Custom ? "  ★custom" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {view.KeyDisplay,-18} {action}{mark}");
        }

        sb.AppendLine();
        sb.AppendLine("Hardcoded (not remappable via keybindings.json):");
        sb.AppendLine("  Tab              Accept completion / cycle mode");
        sb.AppendLine("  Ctrl+Left/Right  Cycle placeholder suggestions");
        sb.AppendLine("  /find <keyword>  Search transcript (slash command)");
        sb.AppendLine("  /diff            Review git changes overlay (slash command)");

        if (loadResult.Warnings.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Warnings ({loadResult.Warnings.Length}):");
            sb.Append(KeybindingValidator.FormatWarnings(loadResult.Warnings));
        }

        sb.AppendLine();
        sb.AppendLine("Use '/keybindings open' to edit custom bindings.");

        return CommandResult.Text(sb.ToString().TrimEnd());
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
