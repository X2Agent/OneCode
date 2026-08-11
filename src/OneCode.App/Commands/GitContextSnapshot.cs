namespace OneCode.App.Commands;

/// <summary>
/// Snapshot of common git context (status/diff/branch/log) used by prompt-style
/// commands (/commit) to build LLM prompts. Output is pre-formatted
/// as strings (with "(empty)" / "(git not available)" placeholders) so callers can
/// interpolate directly into prompt templates without re-checking nullability.
/// </summary>
internal sealed record GitContextSnapshot(
    string Status,
    string Diff,
    string Branch,
    string Log)
{
    /// <summary>
    /// Collect git context by running four read-only git commands in order.
    /// Each field falls back to a human-readable placeholder when git is unavailable
    /// or the command produces no output — never returns null.
    /// </summary>
    public static async Task<GitContextSnapshot> ReadAsync(
        IGitHelper gitHelper, CancellationToken ct)
    {
        var status = await gitHelper.ReadAsync(["status"], ct).ConfigureAwait(false);
        var diff = await gitHelper.ReadAsync(["diff", "HEAD"], ct).ConfigureAwait(false);
        var branch = await gitHelper.ReadAsync(["branch", "--show-current"], ct).ConfigureAwait(false);
        var log = await gitHelper.ReadAsync(["log", "--oneline", "-10"], ct).ConfigureAwait(false);

        return new GitContextSnapshot(status, diff, branch, log);
    }
}
