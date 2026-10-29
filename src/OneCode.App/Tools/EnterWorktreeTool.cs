using System.ComponentModel;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.App.Session;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Git;

namespace OneCode.App.Tools;

/// <summary>EnterWorktree tool — enters or creates a git worktree for isolated development.</summary>
public sealed class EnterWorktreeTool
{
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly ISessionWorkingDirectory _sessionManager;
    private readonly IGitHelper _gitHelper;
    private readonly ILogger<EnterWorktreeTool>? _logger;

    public EnterWorktreeTool(
        IWorkingDirectoryAccessor wd,
        ISessionWorkingDirectory sessionManager,
        IGitHelper gitHelper,
        ILogger<EnterWorktreeTool>? logger = null)
        => (_wd, _sessionManager, _gitHelper, _logger) = (wd, sessionManager, gitHelper, logger);

    [Description("Enter or create a git worktree. Creates a new branch and isolated working directory. Switches the session working directory to the worktree path.")]
    public async Task<ToolResult> EnterAsync(
        [Description("Optional slug for the worktree name.")] string? name = null,
        CancellationToken ct = default)
    {
        var cwd = _wd.WorkingDirectory;
        var statePath = GetWorktreeStatePath();
        if (File.Exists(statePath))
        {
            // 孤儿接管：状态文件指向的 worktree 已不存在（崩溃残留 / 手动删除 / 换机）——
            // 直接接管覆盖，不再硬阻塞后续操作；worktree 仍在时维持既有拒绝。
            var existingWorktree = TryReadStateWorktreePath(statePath);
            if (existingWorktree is not null && Directory.Exists(existingWorktree))
                return ToolResult.Error("Already in a worktree session");

            _logger?.LogInformation(
                "Stale worktree state {StatePath} points to missing directory {Worktree}; taking over",
                statePath, existingWorktree ?? "(unknown)");
        }

        var gitRoot = await _gitHelper.GetRepositoryRootAsync(cwd, ct).ConfigureAwait(false);
        if (gitRoot is null) return ToolResult.Error("Not in a git repository");

        var slug = !string.IsNullOrWhiteSpace(name) ? SanitizeSlug(name!) : GenerateSlug();
        if (slug.Length == 0) slug = GenerateSlug();
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var worktreePath = WorktreeLayout.GetWorktreePath(gitRoot, slug);
        if (Directory.Exists(worktreePath))
            worktreePath = WorktreeLayout.GetWorktreePath(gitRoot, $"{slug}-{sessionId}");
        var branchName = $"worktree/{Path.GetFileName(worktreePath)}";

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
        // 当前作用域共享同一目录 holder，后续工具立即按 worktree 边界执行。
        OneCodeAgentRunContext.WorkingDirectoryOverride = worktreePath;

        return ToolResult.JsonSuccess(new { worktreePath, worktreeBranch = branchName });
    }

    /// <summary>
    /// 会话级状态文件路径：并发会话各持独立 worktree 状态，互不阻塞。
    /// 无会话上下文（无头/测试）时回退全局文件。
    /// </summary>
    public static string GetWorktreeStatePath()
    {
        var conversationId = ToolActivationContext.CurrentConversationId;
        // SessionId 为 ^[0-9a-f]{32}$；不满足该形状的异常值按无会话回退，杜绝文件名注入。
        var isSessionScoped = conversationId is not null
            && conversationId.Length == 32
            && conversationId.All(char.IsAsciiHexDigitLower);
        var fileName = isSessionScoped
            ? $"worktree_state.{conversationId}.json"
            : "worktree_state.json";
        return Path.Combine(PathsHelper.GetUserConfigDir(), fileName);
    }

    /// <summary>读取状态文件指向的 worktree 路径（解析失败返回 null，供孤儿判定用）。</summary>
    private string? TryReadStateWorktreePath(string statePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            return doc.RootElement.TryGetProperty("worktreePath", out var wp) ? wp.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Failed to read worktree state {StatePath}; treating it as stale", statePath);
            return null;
        }
    }

    private static string SanitizeSlug(string n) => new(n.Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
    private static string GenerateSlug() { var b = new byte[4]; System.Security.Cryptography.RandomNumberGenerator.Fill(b); return Convert.ToHexString(b).ToLowerInvariant(); }
}
