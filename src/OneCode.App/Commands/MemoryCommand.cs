using System.Text;
using OneCode.App.Session;
using OneCode.App.Services.AutoDream;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;

namespace OneCode.App.Commands;

/// <summary>
/// /memory — manage the searchable memory subsystem (session facts + MEMORY.md entries).
/// Project coding rules belong in <c>AGENTS.md</c> via <c>/remember</c>, not here.
/// </summary>
public sealed class MemoryCommand(
    ISessionManager sessionManager,
    IMemoryService memoryService,
    IMemoryEntryStore entryStore,
    ISessionMemoryService sessionMemoryService,
    AutoDreamService autoDreamService) : Command
{
    public override string Name => "memory";
    public override string Description =>
        "Manage searchable memory (MEMORY.md + session facts). For project rules use /remember → AGENTS.md";
    public override CommandCategory Category => CommandCategory.Session;
    public override string? ArgumentHint => "[list|add|remove|clear|autodream]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var conv = sessionManager.ForegroundConversation;
        if (conv is null)
            return CommandResult.Error("/memory requires an active session.");

        if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Text(await RenderListAsync(conv, ct));

        return args[0].ToLowerInvariant() switch
        {
            "add" => await AddAsync(conv, args, ct),
            "remove" or "delete" => await RemoveAsync(args, ct),
            "clear" => await ClearAsync(args, ct),
            "autodream" => await AutoDreamAsync(args, ct),
            _ => CommandResult.Error("Usage: /memory [list|add|remove|clear|autodream]"),
        };
    }

    // List

    private async Task<string> RenderListAsync(Core.Domain.Conversation conv, CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Searchable memory (not AGENTS.md — use /remember for project rules):");
        sb.AppendLine();

        // Session memories (existing behavior)
        var sessionMemories = sessionMemoryService.GetMemories(conv);
        sb.AppendLine("Session memories (shown for reference; /memory remove targets persistent entries only):");
        if (sessionMemories.Count == 0) sb.AppendLine("  (none)");
        else foreach (var m in sessionMemories)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  • [{m.Source}] {m.Content}");

        // Persistent memory entries from MEMORY.md
        var entries = await memoryService.ListMemoryEntriesAsync(conv.WorkingDirectory, ct).ConfigureAwait(false);
        sb.AppendLine("\nPersistent entries (MEMORY.md):");
        if (entries.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var e in entries)
            {
                var expired = e.Entry.IsExpired ? " [EXPIRED]" : "";
                var valuePreview = e.Entry.Value.Trim().Split('\n')[0];
                if (valuePreview.Length > 60) valuePreview = valuePreview[..60] + "...";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {e.Index}. [{e.ScopeLabel}/{e.Entry.Source}] {e.Entry.Key}{expired}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"       {valuePreview}");
            }
        }

        sb.AppendLine("\nUsage:");
        sb.AppendLine("  /memory add [--user] <text>   Add a MEMORY.md fact/preference (searchable)");
        sb.AppendLine("  /memory remove <n>           Remove persistent entry #n (session memories are not removable)");
        sb.AppendLine("  /memory clear [--all]        Clear project (or --all for user+project)");
        sb.AppendLine("  /memory autodream trigger    Trigger AutoDream consolidation");
        sb.AppendLine("  /memory autodream status     Show AutoDream status");
        sb.AppendLine();
        sb.AppendLine("Not for project coding rules — use /remember <rule> to update AGENTS.md.");
        return sb.ToString().TrimEnd();
    }

    private async Task<CommandResult> AddAsync(Core.Domain.Conversation conv, string[] args, CancellationToken ct)
    {
        // /memory add [--user] <text>
        var useUserScope = false;
        var textArgs = args.Skip(1).ToList();

        if (textArgs.Count > 0 && textArgs[0].Equals("--user", StringComparison.OrdinalIgnoreCase))
        {
            useUserScope = true;
            textArgs.RemoveAt(0);
        }

        if (textArgs.Count == 0)
            return CommandResult.Error("Usage: /memory add [--user] <text>");

        var text = string.Join(" ", textArgs);
        var now = DateTimeOffset.UtcNow;
        var key = $"manual:{now:yyyyMMdd-HHmmss}";

        var entry = new MemoryEntry
        {
            Key = key,
            Value = text,
            Source = "manual",
            Category = "manual",
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = null,  // manual entries never expire
        };

        var scope = useUserScope ? MemoryScope.User : MemoryScope.Project;

        await entryStore.UpsertAsync(scope, [entry], ct).ConfigureAwait(false);
        var scopeLabel = useUserScope ? "user" : "project";
        return CommandResult.Text(
            $"Added MEMORY.md entry [{scopeLabel}]: {text}\n" +
            "(Searchable via memory index / search_memories. For AGENTS.md project rules use /remember.)");
    }

    private async Task<CommandResult> RemoveAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var idx) || idx < 1)
            return CommandResult.Error("Usage: /memory remove <number>");

        var conv = sessionManager.ForegroundConversation!;
        var entries = await memoryService.ListMemoryEntriesAsync(conv.WorkingDirectory, ct).ConfigureAwait(false);

        var target = entries.FirstOrDefault(e => e.Index == idx);
        if (target is null)
            return CommandResult.Error($"Memory #{idx} not found.");

        var scope = target.ScopeLabel == "global" ? MemoryScope.User : MemoryScope.Project;

        var removed = await entryStore.RemoveAsync(scope, target.Entry.Key, ct).ConfigureAwait(false);
        return removed
            ? CommandResult.Text($"Removed memory #{idx}: {target.Entry.Key}")
            : CommandResult.Error($"Failed to remove memory #{idx}.");
    }

    private async Task<CommandResult> ClearAsync(string[] args, CancellationToken ct)
    {
        var clearAll = args.Length > 1 && args[1].Equals("--all", StringComparison.OrdinalIgnoreCase);

        await entryStore.ClearAsync(MemoryScope.Project, ct).ConfigureAwait(false);

        if (clearAll)
        {
            await entryStore.ClearAsync(MemoryScope.User, ct).ConfigureAwait(false);
            return CommandResult.Text("Cleared all persistent memory entries (project + user).");
        }

        return CommandResult.Text("Cleared project-level persistent memory entries. Use /memory clear --all to also clear user-level.");
    }

    // AutoDream subcommands

    private Task<CommandResult> AutoDreamAsync(string[] args, CancellationToken ct)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        return sub switch
        {
            "trigger" => AutoDreamTriggerAsync(ct),
            "status" => AutoDreamStatusAsync(),
            _ => Task.FromResult(CommandResult.Error("Usage: /memory autodream [trigger|status]")),
        };
    }

    private async Task<CommandResult> AutoDreamTriggerAsync(CancellationToken ct)
    {
        autoDreamService.Trigger();
        // Give the background service a moment to start
        await Task.Delay(500, ct).ConfigureAwait(false);
        return CommandResult.Text("AutoDream trigger signaled. Check /memory autodream status for results.");
    }

    private Task<CommandResult> AutoDreamStatusAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("AutoDream Status:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Enabled: {autoDreamService.IsEnabled()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Last consolidated: {autoDreamService.GetLastConsolidatedAt():O}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Min hours: {autoDreamService.GetMinHours()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Min sessions: {autoDreamService.GetMinSessions()}");
        var lastScan = autoDreamService.GetLastSessionScanAt();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Last session scan: {(lastScan == DateTimeOffset.MinValue ? "never" : lastScan.ToString("O"))}");
        return Task.FromResult(CommandResult.Text(sb.ToString().TrimEnd()));
    }
}
