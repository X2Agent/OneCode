using System.ComponentModel;
using OneCode.Core.IO;

namespace OneCode.App.Tools;

/// <summary>
/// 只读 Git 检查工具：为 Plan 模式等无 shell 环境提供 <c>git status/log/diff/show/blame/branch/remote</c>。
///
/// <para>
/// 安全模型：子动作白名单 + 固定参数模板——模型只能选择动作与提供受限的 ref/path/count 参数，
/// 所有参数经 <see cref="IProcessRunner.ExecuteWithTimeoutAsync"/> 以 ArgumentList 传递
/// （无 shell 注入面），且拒绝以 <c>-</c> 开头的 token（防选项注入）与根路径。
/// 工具整体 <see cref="ToolRisk.ReadOnly"/>，不提供任何修改工作区或远端的动作。
/// </para>
/// </summary>
public sealed class GitTool(
    IProcessRunner processRunner,
    IWorkingDirectoryAccessor workingDirectory,
    ILogger<GitTool>? logger = null)
{
    /// <summary>单次输出上限（字符）——diff/blame 可能产生大输出，截断保护上下文窗口。</summary>
    private const int MaxOutputChars = 20_000;

    /// <summary>log 子动作默认返回的提交数。</summary>
    private const int DefaultLogCount = 20;

    private const int MinLogCount = 1;
    private const int MaxLogCount = 200;

    /// <summary>30s 硬超时：git 检查命令应秒级返回；超时多为仓库异常（网络 remote 已被动作白名单排除）。</summary>
    private const int TimeoutMs = 30_000;

    private static readonly string[] AllowedActions =
        ["status", "log", "diff", "show", "blame", "branch", "remote"];

    /// <summary>
    /// 只读 Git 检查：查看仓库状态、历史、差异与分支信息。
    /// 仅支持白名单内的只读动作，任何修改工作区或访问远端的操作都不可用。
    /// </summary>
    /// <returns>命令 stdout；非零退出或超时返回错误信息。</returns>
    [Description("Read-only git inspection: status, log, diff, show, blame, branch, remote. " +
                 "Use this to investigate repository history and pending changes (e.g. in plan mode). " +
                 "No mutating operations are available — stage/commit/push are deliberately excluded.")]
    public async Task<ToolResult> ExecuteAsync(
        [Description("Read-only git action: status | log | diff | show | blame | branch | remote")] string action,
        [Description("Optional ref for log/diff/show (e.g. HEAD~1, main, HEAD~3..HEAD). Must not start with '-'.")] string? refName = null,
        [Description("Optional file path (relative to the working directory) for diff/blame. Must not start with '-'.")] string? path = null,
        [Description("Commit count for log. Default 20, clamped to 1..200.")] int count = DefaultLogCount,
        [Description("For diff: show staged changes instead of unstaged.")] bool staged = false,
        CancellationToken ct = default)
    {
        var normalizedAction = action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AllowedActions.Contains(normalizedAction))
        {
            return ToolResult.Error(
                $"Unknown git action '{action}'. Allowed read-only actions: {string.Join(", ", AllowedActions)}.");
        }

        if (!string.IsNullOrWhiteSpace(refName) && !IsValidToken(refName))
            return ToolResult.Error($"Invalid ref '{refName}': must be a non-empty ref not starting with '-'.");

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!IsValidToken(path))
                return ToolResult.Error($"Invalid path '{path}': must be a relative path not starting with '-'.");

            var baseDir = string.IsNullOrWhiteSpace(workingDirectory.WorkingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory.WorkingDirectory;
            var resolved = Infrastructure.PathsHelper.SafeResolve(
                path.Trim(),
                baseDir,
                workingDirectory.AdditionalDirectories);
            if (!resolved.IsSuccess)
                return ToolResult.Error(resolved.Error ?? $"Invalid path '{path}'.");
        }

        var arguments = normalizedAction switch
        {
            "status" => ["status", "--porcelain", "-b"],
            "log" => BuildLogArguments(Math.Clamp(count, MinLogCount, MaxLogCount), refName),
            "diff" => BuildDiffArguments(refName, path, staged),
            "show" => ["show", "--stat", string.IsNullOrWhiteSpace(refName) ? "HEAD" : refName.Trim()],
            "blame" => string.IsNullOrWhiteSpace(path)
                ? null
                : new List<string> { "blame", "--", path.Trim() },
            "branch" => new List<string> { "branch", "-a", "-v" },
            "remote" => new List<string> { "remote", "-v" },
            // 白名单门控保证到达此处必为 7 个动作之一；此臂仅锁不变量，兼作 switch 穷尽性要求。
            _ => throw new UnreachableException($"Unknown git action '{normalizedAction}'."),
        };

        // 唯一 null 是 blame 缺 path。
        if (arguments is null)
            return ToolResult.Error("blame requires a file path.");

        ProcessResult? result;
        try
        {
            // 会话工作目录（EnterWorktree / GOAL worktree 隔离后与启动仓库不同），
            // 与 BashTool/LspTool 同一来源。
            result = await processRunner.ExecuteWithTimeoutAsync(
                "git", [.. arguments], workingDirectory: workingDirectory.WorkingDirectory,
                timeoutMs: TimeoutMs, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Git tool failed for action {Action}.", normalizedAction);
            return ToolResult.Error($"Error: git {normalizedAction} failed unexpectedly: {ex.Message}");
        }

        if (result is null)
        {
            logger?.LogWarning("Git tool could not start the git process for action {Action}.", normalizedAction);
            return ToolResult.Error("Error: failed to start git. Verify git is installed and on PATH.");
        }

        if (result.TimedOut)
            return ToolResult.Error($"Error: git {normalizedAction} timed out after {TimeoutMs / 1000}s.");

        if (result.ExitCode != 0)
        {
            var stderr = result.Stderr.Trim();
            return ToolResult.Error($"Error: git {normalizedAction} failed (exit {result.ExitCode}): {stderr}");
        }

        return ToolResult.Success(Truncate(result.Stdout));
    }

    /// <summary>token 合法性：非空、不以 <c>-</c> 开头（防选项注入）、无控制字符、非根路径。</summary>
    private static bool IsValidToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0
            && !trimmed.StartsWith('-')
            && !IsRootedToken(trimmed)
            && trimmed.All(c => !char.IsControl(c));
    }

    /// <summary>
    /// 平台无关的根路径判定：Path.IsPathRooted 在 Unix 上不认 "C:\x" 与 "\\unc"，
    /// Windows 风格的根 token 会被当 ref 放行（跨平台语义由 GitToolTests 锚定）。
    /// </summary>
    private static bool IsRootedToken(string value) =>
        Path.IsPathRooted(value)
        || value.StartsWith('\\')
        || (value.Length >= 2 && value[1] == ':' && char.IsAsciiLetter(value[0]));

    private static List<string> BuildLogArguments(int count, string? refName)
    {
        var args = new List<string> { "log", "--oneline", "--decorate=short", "-n", count.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(refName))
            args.Add(refName.Trim());
        return args;
    }

    private static List<string> BuildDiffArguments(string? refName, string? path, bool staged)
    {
        var args = new List<string> { "diff" };
        if (staged)
            args.Add("--staged");
        if (!string.IsNullOrWhiteSpace(refName))
            args.Add(refName.Trim());
        if (!string.IsNullOrWhiteSpace(path))
            args.AddRange(["--", path.Trim()]);
        return args;
    }

    private static string Truncate(string output)
    {
        if (output.Length <= MaxOutputChars)
            return output;

        var truncated = output[..MaxOutputChars];
        var lastNewline = truncated.LastIndexOf('\n');
        return (lastNewline > 0 ? truncated[..lastNewline] : truncated)
               + $"\n[Output truncated at {MaxOutputChars} chars]";
    }
}
