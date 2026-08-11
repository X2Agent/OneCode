using System.Text;
using OneCode.App.Services.Lsp;
using OneCode.Core.Lsp;

namespace OneCode.App.Commands;

/// <summary>
/// /lsp — manage language packs and LSP servers.
///
/// Subcommands:
///   /lsp list               — list all language packs and their status
///   /lsp install &lt;lang&gt;    — install a language pack's server binary
///   /lsp uninstall &lt;lang&gt;  — stop and remove a language pack's server
///   /lsp status             — show running server status and diagnostics
///   /lsp enable &lt;lang&gt;     — start the LSP server for a language
///   /lsp disable &lt;lang&gt;    — stop the LSP server for a language
/// </summary>
public sealed class LspCommand(
    LanguagePackRegistry registry,
    LanguagePackInstaller installer,
    ILspServerManager serverManager,
    IWorkingDirectoryAccessor workingDirectoryAccessor) : Command
{
    public override string Name => "lsp";
    public override string Description => "Manage language packs and LSP servers";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[list|install <lang>|uninstall <lang>|status|enable <lang>|disable <lang>]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return CommandResult.Text(await ListAsync(ct).ConfigureAwait(false));

        return args[0].ToLowerInvariant() switch
        {
            "list" or "ls" => CommandResult.Text(await ListAsync(ct).ConfigureAwait(false)),
            "install" => await InstallAsync(args[1..], ct).ConfigureAwait(false),
            "uninstall" => await UninstallAsync(args[1..], ct).ConfigureAwait(false),
            "status" => ShowStatus(),
            "enable" => await EnableAsync(args[1..], ct).ConfigureAwait(false),
            "disable" => await DisableAsync(args[1..], ct).ConfigureAwait(false),
            _ => CommandResult.Error($"Unknown subcommand: {args[0]}. Use: list, install, uninstall, status, enable, disable"),
        };
    }

    private async Task<string> ListAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Language Packs:");
        sb.AppendLine();

        var statuses = serverManager.GetStatus();
        var packs = registry.GetAllPacks();

        var installChecks = await Task.WhenAll(
            packs.Select(p => installer.IsInstalledAsync(p.Id, ct))
        ).ConfigureAwait(false);

        for (var i = 0; i < packs.Count; i++)
        {
            var pack = packs[i];
            var isRunning = statuses.Any(s => s.Name == pack.Id && s.IsRunning);
            var isInstalled = installChecks[i];

            var statusText = isRunning ? "running" : (isInstalled ? "installed" : "available");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {pack.Id} ({pack.DisplayName})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Extensions: {string.Join(", ", pack.Extensions)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Status:     {statusText}");
            sb.AppendLine();
        }

        sb.AppendLine("Use '/lsp install <lang>' to install a language pack.");
        sb.AppendLine("Use '/lsp enable <lang>' to start a server.");

        return sb.ToString().TrimEnd();
    }

    private async Task<CommandResult> InstallAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
            return CommandResult.Error("Usage: /lsp install <lang>");

        var packId = args[0];
        var pack = registry.GetPack(packId);
        if (pack is null)
            return CommandResult.Error($"Language pack '{packId}' not found. Use '/lsp list' to see available packs.");

        var result = await installer.InstallAsync(packId, ct).ConfigureAwait(false);
        return result.Success
            ? CommandResult.Text(result.Message)
            : CommandResult.Error(result.Message);
    }

    private async Task<CommandResult> UninstallAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
            return CommandResult.Error("Usage: /lsp uninstall <lang>");

        var packId = args[0];
        var pack = registry.GetPack(packId);
        if (pack is null)
            return CommandResult.Error($"Language pack '{packId}' not found. Use '/lsp list' to see available packs.");

        var result = await installer.UninstallAsync(packId, ct).ConfigureAwait(false);
        return result.Success
            ? CommandResult.Text(result.Message)
            : CommandResult.Error(result.Message);
    }

    private CommandResult ShowStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine("LSP Server Status:");
        sb.AppendLine();

        var statuses = serverManager.GetStatus();
        if (statuses.Count == 0)
        {
            sb.AppendLine("  No servers running.");
            sb.AppendLine();
            sb.AppendLine("Use '/lsp enable <lang>' to start a server.");
            return CommandResult.Text(sb.ToString().TrimEnd());
        }

        foreach (var s in statuses)
        {
            var state = s.IsRunning
                ? (s.IsInitialized ? "running" : "starting")
                : "stopped";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.Name,-15} {state}");
        }

        var diagnostics = serverManager.GetDiagnostics();
        sb.AppendLine();
        sb.AppendLine("Diagnostics:");
        if (diagnostics.Count == 0)
        {
            sb.AppendLine("  No diagnostics reported.");
        }
        else
        {
            foreach (var d in diagnostics)
            {
                var location = d.File is not null
                    ? $" ({d.File}:{d.Line ?? 0}:{d.Column ?? 0})"
                    : "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{d.Severity}] {d.ServerName}: {d.Message}{location}");
            }
        }

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> EnableAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
            return CommandResult.Error("Usage: /lsp enable <lang>");

        var packId = args[0];
        var pack = registry.GetPack(packId);
        if (pack is null)
            return CommandResult.Error($"Language pack '{packId}' not found. Use '/lsp list' to see available packs.");

        var existing = serverManager.GetStatus().FirstOrDefault(s => s.Name == pack.Id);
        if (existing is { IsRunning: true })
            return CommandResult.Text($"LSP server '{pack.Id}' is already running.");

        try
        {
            var sessionWorkingDir = workingDirectoryAccessor.WorkingDirectory;
            if (!LspProjectMatcher.Matches(pack, sessionWorkingDir))
            {
                return CommandResult.Error(
                    $"Cannot start LSP server '{pack.Id}': no project marker files " +
                    $"({string.Join(", ", pack.ProjectFiles ?? [])}) in '{sessionWorkingDir}'. " +
                    "Open a project directory or pass --workspace <path>.");
            }

            var config = pack.ToServerConfig() with { WorkingDirectory = sessionWorkingDir };
            var started = await serverManager.StartServerAsync(config, ct).ConfigureAwait(false);
            return started
                ? CommandResult.Text($"LSP server '{pack.Id}' started successfully.")
                : CommandResult.Error($"Failed to start LSP server '{pack.Id}'. Use '/lsp install {pack.Id}' to install the server binary first.");
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to start LSP server '{pack.Id}': {ex.Message}");
        }
    }

    private async Task<CommandResult> DisableAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
            return CommandResult.Error("Usage: /lsp disable <lang>");

        var packId = args[0];
        var pack = registry.GetPack(packId);
        if (pack is null)
            return CommandResult.Error($"Language pack '{packId}' not found. Use '/lsp list' to see available packs.");

        var stopped = await serverManager.StopServerAsync(pack.Id, ct).ConfigureAwait(false);
        return stopped
            ? CommandResult.Text($"LSP server '{pack.Id}' stopped.")
            : CommandResult.Text($"LSP server '{pack.Id}' was not running.");
    }
}
