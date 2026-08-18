using System.Text;
using OneCode.App.Services.Compact;
using OneCode.App.Session;
using OneCode.Core.Coordinator;
using OneCode.Core.Goals;

namespace OneCode.App.Commands;

/// <summary>
/// /checkpoint — checkpoint 管理命令，统一入口管理两类 checkpoint。
///
/// 会话级 checkpoint（持久化）:
///   /checkpoint save [name]     — 对当前消息索引做快照（默认名：时间戳）
///   /checkpoint list            — 列出当前会话的所有快照
///   /checkpoint restore [name]  — 回退到快照时的消息索引
///   /checkpoint delete [name]   — 删除指定快照
///   数据存储：conv.Metadata["checkpoints"] JSON 数组，持久化到 session JSONL。
///
/// 工作流级 checkpoint（Goal/Team 跨进程持久）:
///   /checkpoint resume [sessionId]  — 从中断点继续 Goal/Team 任务执行
///   /checkpoint resume              — 列出所有可恢复的 Goal/Team 会话
///   数据存储：Goal 使用 GoalRun 聚合 + MAF Checkpoint（DurableWorkflowHost）；
///   Team 使用 TeamRun 聚合 + 共享 Durable Workflow Host（新执行世代重启，跨进程持久）。
///   适用场景：Goal/Team 执行被 Ctrl+C 中断后，凭 sessionId 恢复。
///   sessionId 类型（Goal/Team）自动判断，用户无需关心。
///
/// 路由说明：
/// resume 子命令返回 <see cref="CommandResult.ResumeWorkflowResult"/>，
/// TUI dispatch 层据此直接调用对应的工作流 resume 流，不经过 LLM 查询管线。
/// </summary>
public sealed class CheckpointCommand(
    ISessionManager sessionManager,
    IGoalRunStore? goalRunStore,
    ITeamRunStore? teamRunStore = null) : Command
{
    private const string MetadataKey = "checkpoints";

    public override string Name => "checkpoint";
    public override string Description => "Manage conversation snapshots and resume interrupted Goal/Team tasks";
    public override CommandCategory Category => CommandCategory.Session;
    public override string? ArgumentHint => "save [name] | list | restore [name] | delete [name] | resume [sessionId]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        // resume 子命令不需要活跃会话，提前处理
        if (sub == "resume")
            return await HandleResumeAsync(args.Length > 1 ? args[1..] : Array.Empty<string>(), ct).ConfigureAwait(false);

        // 会话级子命令需要活跃会话
        var conv = sessionManager.ForegroundConversation;
        if (conv is null)
            return CommandResult.Error("No active conversation. Use '/checkpoint resume' to resume interrupted Goal/Team tasks.");

        return sub switch
        {
            "save" => await SaveCheckpointAsync(conv, args.Length > 1 ? string.Join(" ", args[1..]) : null, ct),
            "list" => ListCheckpoints(conv),
            "restore" => await RestoreCheckpointAsync(conv, args.Length > 1 ? string.Join(" ", args[1..]) : null, ct),
            "delete" => await DeleteCheckpointAsync(conv, args.Length > 1 ? string.Join(" ", args[1..]) : null, ct),
            _ => CommandResult.Error("Usage: /checkpoint save [name] | list | restore [name] | delete [name] | resume [sessionId]"),
        };
    }

    // 工作流级 checkpoint 恢复

    private async Task<CommandResult> HandleResumeAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            return await ListAllResumableWorkflowSessionsAsync(ct).ConfigureAwait(false);

        SessionId sessionId = args[0];

        if (goalRunStore is not null)
        {
            var goalRun = await goalRunStore.LoadBySessionAsync(sessionId, ct).ConfigureAwait(false);
            if (goalRun is not null && !goalRun.IsTerminal)
                return CommandResult.ResumeWorkflow(sessionId, WorkflowResumeKind.Goal);
        }

        if (teamRunStore is not null
            && (await teamRunStore.ListActiveAsync(ct).ConfigureAwait(false))
                .Any(run => run.SessionId == sessionId
                    && run.Status is TeamRunStatus.Running
                        or TeamRunStatus.Blocked
                        or TeamRunStatus.WaitingForUser))
            return CommandResult.ResumeWorkflow(sessionId, WorkflowResumeKind.Team);

        return CommandResult.Error(await BuildNotFoundMessageAsync(sessionId, ct).ConfigureAwait(false));
    }

    private async Task<CommandResult> ListAllResumableWorkflowSessionsAsync(CancellationToken ct)
    {
        var goalRuns = goalRunStore is not null
            ? (await goalRunStore.ListActiveAsync(ct).ConfigureAwait(false)).ToArray()
            : Array.Empty<GoalRun>();
        var teamSessions = teamRunStore is not null
            ? (await teamRunStore.ListActiveAsync(ct).ConfigureAwait(false))
                .Where(run => run.SessionId is not null)
                .Select(run => run.SessionId!.Value)
                .Distinct()
                .ToArray()
            : Array.Empty<SessionId>();

        if (goalRuns.Length == 0 && teamSessions.Length == 0)
        {
            return CommandResult.Text(
                "No interrupted tasks to resume.\n\n" +
                "Goal runs use the durable Workflow Registry and survive process restarts.\n" +
                "Team runs use the durable TeamRun aggregate / Workflow Registry and survive restarts.\n" +
                "Use /checkpoint resume <sessionId> to continue an interrupted task.");
        }

        var sb = new StringBuilder("Resumable tasks:\n");

        if (goalRuns.Length > 0)
        {
            sb.AppendLine("\n  Goal tasks (durable Workflow Registry):");
            foreach (var run in goalRuns)
            {
                var completed = run.Plan.Count(step => step.State == GoalStepState.Completed);
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    • {run.SessionId}  ({completed}/{run.Plan.Count} completed, {FormatSavedAt(run.UpdatedAt)}) — {run.Goal}");
            }
        }

        if (teamSessions.Length > 0)
        {
            sb.AppendLine("\n  Team tasks (durable Workflow Registry):");
            foreach (var sid in teamSessions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    • {sid}");
            }
        }

        sb.AppendLine("\nUse /checkpoint resume <sessionId> to continue.");
        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private async Task<string> BuildNotFoundMessageAsync(string sessionId, CancellationToken ct)
    {
        var goalRuns = goalRunStore is not null
            ? (await goalRunStore.ListActiveAsync(ct).ConfigureAwait(false)).ToArray()
            : Array.Empty<GoalRun>();
        var teamSessions = teamRunStore is not null
            ? (await teamRunStore.ListActiveAsync(ct).ConfigureAwait(false))
                .Where(run => run.SessionId is not null)
                .Select(run => run.SessionId!.Value)
                .Distinct()
                .ToArray()
            : Array.Empty<SessionId>();

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Session '{sessionId}' not found.");
        sb.AppendLine("Goal runs use durable registry/checkpoints; team runs use the durable TeamRun aggregate.");

        if (goalRuns.Length > 0 || teamSessions.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Available sessions:");
            foreach (var run in goalRuns)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  • {run.SessionId}");
            foreach (var sid in teamSessions)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  • {sid}");
        }
        else
        {
            sb.AppendLine("No resumable sessions available.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSavedAt(DateTimeOffset savedAt)
    {
        var delta = DateTimeOffset.UtcNow - savedAt;
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
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

    private sealed record CheckpointEntry(string Name, DateTimeOffset SavedAt, int MessageIndex);
}
