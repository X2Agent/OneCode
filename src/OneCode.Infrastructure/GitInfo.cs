using OneCode.Infrastructure.Abstractions;

namespace OneCode.Infrastructure;

/// <summary>
/// Git repository information and status.
/// </summary>
public sealed record GitStatus(
    string? Branch,
    bool IsDirty,
    int UncommitedChanges,
    int UntrackedFiles,
    string? RemoteUrl);

/// <summary>
/// Provides Git information about the working directory.
/// </summary>
public sealed class GitInfo
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<GitInfo> _logger;

    public GitInfo(IProcessRunner processRunner, ILogger<GitInfo> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <summary>
    /// Returns null if not a git repo or git is not installed.
    /// </summary>
    public async Task<GitStatus?> GetStatusAsync(string workingDir, CancellationToken ct = default)
    {
        try
        {
            var branch = await RunGitAsync(workingDir, "branch --show-current", ct);
            var changedLines = (await RunGitAsync(workingDir, "status --porcelain", ct))
                ?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length >= 3)
                .ToArray() ?? Array.Empty<string>();
            var isDirty = changedLines.Any(l => !l.StartsWith("??", StringComparison.Ordinal) && !l.StartsWith("!!", StringComparison.Ordinal));
            var uncommited = changedLines.Count(l => "MADR".Contains(l[0]));
            var untracked = changedLines.Count(l => l.StartsWith("??", StringComparison.Ordinal));
            var remoteUrl = await RunGitAsync(workingDir, "config --get remote.origin.url", ct);

            return new GitStatus(
                Branch: string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
                IsDirty: isDirty,
                UncommitedChanges: uncommited,
                UntrackedFiles: untracked,
                RemoteUrl: string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get git status for {WorkingDir}", workingDir);
            return null;
        }
    }

    private async Task<string?> RunGitAsync(
        string workingDir, string args, CancellationToken ct)
    {
        var arguments = ParseGitArgs(args).ToArray();
        var result = await _processRunner.ExecuteWithArgumentListAsync(
            "git", arguments, workingDir, ct: ct).ConfigureAwait(false);
        return result?.Success == true ? result.Stdout : null;
    }

    /// <summary>
    /// Parse git command line arguments, preserving quoted strings.
    /// </summary>
    private static IEnumerable<string> ParseGitArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            yield break;

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in args)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
            yield return current.ToString();
    }
}
