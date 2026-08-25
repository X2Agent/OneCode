using System.Text;
using OneCode.App.Services.Compact;
using OneCode.App.Session;

namespace OneCode.App.Commands;

/// <summary>
/// /checkpoint — 会话级 checkpoint（持久化快照）管理。
///
///   /checkpoint save [name]     — 对当前消息索引做快照（默认名：时间戳）
///   /checkpoint list            — 列出当前会话的所有快照
///   /checkpoint restore [name]  — 回退到快照时的消息索引
///   /checkpoint delete [name]   — 删除指定快照
///   数据存储：conv.Metadata["checkpoints"] JSON 数组，持久化到 session JSONL。
///
/// 工作流级恢复（Goal/Team 中断续跑）见 /resume。
/// </summary>
public sealed class CheckpointCommand(ISessionManager sessionManager) : Command
{
    private const string MetadataKey = "checkpoints";

    public override string Name => "checkpoint";
    public override string Description => "Manage conversation snapshots (save, list, restore, delete)";
    public override CommandCategory Category => CommandCategory.Session;
    public override string? ArgumentHint => "save [name] | list | restore [name] | delete [name]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        // 会话级子命令需要活跃会话
        var conv = sessionManager.ForegroundConversation;
        if (conv is null)
            return CommandResult.Error("No active conversation. Use '/resume' to resume interrupted Goal/Team tasks.");

        return sub switch
        {
            "save" => await SaveCheckpointAsync(conv, args.Length > 1 ? string.Join(" ", args[1..]) : null, ct),
            "list" => ListCheckpoints(conv),
            "restore" => await RestoreCheckpointAsync(conv, args.Length > 1 ? string.Join(" ", args[1..]) : null, ct),
            "delete" => await DeleteCheckpointAsync(conv, args.Length > 1 ? string.Join(" ", args[1..]) : null, ct),
            _ => CommandResult.Error("Usage: /checkpoint save [name] | list | restore [name] | delete [name]"),
        };
    }

    // 会话级 checkpoint（持久化快照）

    private async Task<CommandResult> SaveCheckpointAsync(
        OneCode.Core.Domain.Conversation conv, string? name, CancellationToken ct)
    {
        var checkpoints = LoadCheckpoints(conv);
        var cpName = string.IsNullOrWhiteSpace(name)
            ? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            : name.Trim();

        if (checkpoints.Exists(c => c.Name == cpName))
            return CommandResult.Error($"Checkpoint '{cpName}' already exists. Use a different name or /checkpoint delete first.");

        checkpoints.Add(new CheckpointEntry(cpName, DateTimeOffset.UtcNow, conv.Messages.Count));
        SaveCheckpoints(conv, checkpoints);
        await sessionManager.SaveAsync(ct).ConfigureAwait(false);

        return CommandResult.Text(
            $"Checkpoint '{cpName}' saved at message index {conv.Messages.Count}.");
    }

    private CommandResult ListCheckpoints(OneCode.Core.Domain.Conversation conv)
    {
        var checkpoints = LoadCheckpoints(conv);
        if (checkpoints.Count == 0)
            return CommandResult.Text("No checkpoints saved. Use /checkpoint save [name] to create one.");

        var sb = new StringBuilder("Checkpoints:\n");
        foreach (var cp in checkpoints)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  • {cp.Name} (idx {cp.MessageIndex}, {FormatSavedAt(cp.SavedAt)})");

        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> RestoreCheckpointAsync(
        OneCode.Core.Domain.Conversation conv, string? name, CancellationToken ct)
    {
        var checkpoints = LoadCheckpoints(conv);
        if (checkpoints.Count == 0)
            return CommandResult.Error("No checkpoints saved.");

        CheckpointEntry? cp;
        if (string.IsNullOrWhiteSpace(name))
        {
            cp = checkpoints[^1];
        }
        else
        {
            cp = checkpoints.Find(c => c.Name == name!.Trim());
            if (cp is null)
                return CommandResult.Error($"Checkpoint '{name}' not found.");
        }

        // Truncate messages to the checkpoint's snapshot index
        while (conv.Messages.Count > cp.MessageIndex)
            conv.Messages.RemoveAt(conv.Messages.Count - 1);

        // 结构性删除消息后必须失效 mafSession（否则恢复的 MAF session 仍引用已删消息），
        // 并持久化会话（否则重启后从 JSONL 回滚出完整历史，restore 失效）。
        MafSessionInvalidator.Invalidate(conv, "checkpoint-restore");
        await sessionManager.SaveAsync(ct).ConfigureAwait(false);

        return CommandResult.Text(
            $"Restored to checkpoint '{cp.Name}' (message index {cp.MessageIndex}).");
    }

    private async Task<CommandResult> DeleteCheckpointAsync(
        OneCode.Core.Domain.Conversation conv, string? name, CancellationToken ct)
    {
        var checkpoints = LoadCheckpoints(conv);
        if (checkpoints.Count == 0)
            return CommandResult.Error("No checkpoints saved.");

        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Error("Usage: /checkpoint delete <name>");

        var removed = checkpoints.RemoveAll(c => c.Name == name!.Trim());
        if (removed == 0)
            return CommandResult.Error($"Checkpoint '{name}' not found.");

        SaveCheckpoints(conv, checkpoints);
        // 元数据变更需持久化（此前仅 save 子命令持久化，delete 的变更重启后丢失）。
        await sessionManager.SaveAsync(ct).ConfigureAwait(false);
        return CommandResult.Text($"Deleted checkpoint '{name}'.");
    }

    private static List<CheckpointEntry> LoadCheckpoints(OneCode.Core.Domain.Conversation conv)
    {
        if (conv.Metadata.TryGetValue(MetadataKey, out var raw) && raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            return el.Deserialize<List<CheckpointEntry>>() ?? [];
        }
        return [];
    }

    private static void SaveCheckpoints(OneCode.Core.Domain.Conversation conv, List<CheckpointEntry> checkpoints)
    {
        conv.Metadata[MetadataKey] = checkpoints;
    }

    private static string FormatSavedAt(DateTimeOffset savedAt)
    {
        var delta = DateTimeOffset.UtcNow - savedAt;
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    private sealed record CheckpointEntry(string Name, DateTimeOffset SavedAt, int MessageIndex);
}
