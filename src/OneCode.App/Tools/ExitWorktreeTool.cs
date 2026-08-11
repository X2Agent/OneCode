using System.ComponentModel;
using OneCode.App.Session;

namespace OneCode.App.Tools;

/// <summary>ExitWorktree tool — exits and optionally removes a git worktree. Restores the session working directory.</summary>
public sealed class ExitWorktreeTool
{
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly ILogger<ExitWorktreeTool>? _logger;
    private readonly ISessionWorkingDirectory _sessionManager;
    private readonly IGitHelper _gitHelper;

    public ExitWorktreeTool(
        IWorkingDirectoryAccessor wd,
        ISessionWorkingDirectory sessionManager,
        IGitHelper gitHelper,
        ILogger<ExitWorktreeTool>? logger = null)
        => (_wd, _logger, _sessionManager, _gitHelper) = (wd, logger, sessionManager, gitHelper);

    [Description("Exit a git worktree session. Optionally remove the worktree. Restores the original working directory.")]
    public async Task<ToolResult> ExitAsync(
        [Description("keep or remove the worktree.")] string action = "keep",
        [Description("Set true to confirm removal when there are uncommitted changes.")] bool discardChanges = false,
        CancellationToken ct = default)
    {
        var statePath = EnterWorktreeTool.GetWorktreeStatePath();
        if (!File.Exists(statePath)) return ToolResult.Success("{\"message\":\"No active worktree session\"}");

        var stateJson = await File.ReadAllTextAsync(statePath, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(stateJson);
        var root = doc.RootElement;
        var worktreePath = root.TryGetProperty("worktreePath", out var wp) ? wp.GetString() : "";
        var worktreeBranch = root.TryGetProperty("worktreeBranch", out var wb) ? wb.GetString() : "";
        var originalCwd = root.TryGetProperty("originalCwd", out var oc) ? oc.GetString() : "";

        if (action == "remove")
        {
            if (!discardChanges && Directory.Exists(worktreePath))
            {
                var changes = await _gitHelper.CountPorcelainChangesAsync(worktreePath!, ct)
                    .ConfigureAwait(false);
                if (changes is null)
                {
                    return ToolResult.Error(
                        "Failed to verify uncommitted changes (git status error). " +
                        "Worktree NOT removed. Inspect it manually, or set discardChanges=true to force removal.");
                }
                if (changes > 0)
                    return ToolResult.Error($"{changes} uncommitted file(s). Set discardChanges=true");
            }
            if (Directory.Exists(worktreePath))
            {
                await _gitHelper.RunAsync(
                    ["worktree", "remove", worktreePath!, "--force"],
                    _wd.WorkingDirectory,
                    ct).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(worktreeBranch))
            {
                await _gitHelper.RunAsync(
                    ["branch", "-D", worktreeBranch],
                    _wd.WorkingDirectory,
                    ct).ConfigureAwait(false);
            }
        }

        File.Delete(statePath);

        if (_sessionManager is not null && !string.IsNullOrEmpty(originalCwd))
            await _sessionManager.ChangeWorkingDirectoryAsync(originalCwd, ct).ConfigureAwait(false);

        return ToolResult.JsonSuccess(new
        {
            action,
            worktreePath,
            worktreeBranch,
            message = action == "remove" ? "Worktree removed." : "Worktree kept."
        });
    }
}
