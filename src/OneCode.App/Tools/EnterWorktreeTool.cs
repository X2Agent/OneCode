using System.ComponentModel;
using OneCode.App.Session;
using OneCode.Infrastructure;

namespace OneCode.App.Tools;

/// <summary>EnterWorktree tool — enters or creates a git worktree for isolated development.</summary>
public sealed class EnterWorktreeTool
{
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly ISessionWorkingDirectory _sessionManager;
    private readonly IGitHelper _gitHelper;

    public EnterWorktreeTool(
        IWorkingDirectoryAccessor wd,
        ISessionWorkingDirectory sessionManager,
        IGitHelper gitHelper)
        => (_wd, _sessionManager, _gitHelper) = (wd, sessionManager, gitHelper);

    [Description("Enter or create a git worktree. Creates a new branch and isolated working directory. Switches the session working directory to the worktree path.")]
    public async Task<ToolResult> EnterAsync(
        [Description("Optional slug for the worktree name.")] string? name = null,
        CancellationToken ct = default)
    {
        var cwd = _wd.WorkingDirectory;
        var statePath = GetWorktreeStatePath();
        if (File.Exists(statePath)) return ToolResult.Error("Already in a worktree session");

        var gitRoot = await _gitHelper.GetRepositoryRootAsync(cwd, ct).ConfigureAwait(false);
        if (gitRoot == null) return ToolResult.Error("Not in a git repository");

        var slug = !string.IsNullOrWhiteSpace(name) ? SanitizeSlug(name!) : GenerateSlug();
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var worktreePath = Path.Combine(gitRoot, ".git-worktrees", sessionId, slug);
        var branchName = $"worktree/{slug}";

        var result = await _gitHelper.RunAsync(
            ["worktree", "add", "-b", branchName, worktreePath],
            gitRoot,
            ct).ConfigureAwait(false);
        if (result is null)
            return ToolResult.Error("Failed to start git process");
        if (!result.Success)
            return ToolResult.Error($"Failed to create worktree: {result.Stderr}");

        var state = JsonSerializer.Serialize(new
        {
            worktreePath,
            worktreeBranch = branchName,
            originalCwd = cwd,
            sessionId,
            createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        await File.WriteAllTextAsync(statePath, state, ct).ConfigureAwait(false);

        await _sessionManager.ChangeWorkingDirectoryAsync(worktreePath, ct).ConfigureAwait(false);

        return ToolResult.JsonSuccess(new { worktreePath, worktreeBranch = branchName });
    }

    public static string GetWorktreeStatePath() => Path.Combine(
        PathsHelper.GetUserConfigDir(), "worktree_state.json");

    private static string SanitizeSlug(string n) => new(n.Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
    private static string GenerateSlug() { var b = new byte[4]; System.Security.Cryptography.RandomNumberGenerator.Fill(b); return Convert.ToHexString(b).ToLowerInvariant(); }
}
