using OneCode.App.Services;
using OneCode.App.Services.Lsp;
using OneCode.Core.Lsp;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Git;
using System.Text;

namespace OneCode.App.Commands;

/// <summary>
/// /review — AI-powered code review with severity levels, scope control and structured output.
///
/// Prompt loaded from prompts/system/review.prompt (overridable via project/user-level
/// .onecode/prompts/). Use --focus to switch to a specialized review prompt:
///   - --focus security     → prompts/system/review-security.prompt (OWASP, secrets, injection, crypto)
///   - --focus crashes      → prompts/system/review-crashes.prompt (null deref, bounds, races, leaks)
///   - --focus performance  → prompts/system/review-performance.prompt (N+1, allocations, blocking I/O)
///   - --focus style        → prompts/system/review-style.prompt (naming, consistency, readability)
///
/// Usage:
///   /review                   → review unstaged changes
///   /review --staged          → review staged changes only
///   /review --all             → review staged + unstaged (git diff HEAD)
///   /review --base main       → review diff against a branch/commit
///   /review --severity critical|warning|all  → filter output severity
///   /review --focus security  → specialize review on security
///   /review --no-edit         → report only, do not suggest edits
///   /review --blame           → attach git blame context for changed lines
///   /review [file-path]       → restrict review to a specific file or directory
/// </summary>
public sealed class ReviewCommand(
    IGitHelper gitHelper,
    IPromptManager promptManager,
    LspDiagnosticRegistry lspRegistry,
    ReviewCacheService reviewCacheService,
    ILogger<ReviewCommand>? logger = null) : Command
{
    public override string Name => "review";
    public override string Description => "AI code review with severity levels and structured output";
    public override CommandCategory Category => CommandCategory.Git;
    public override string? ArgumentHint => "[--staged|--all|--base <ref>] [--severity critical|warning|all] [--focus security|crashes|performance|style] [--no-edit] [--blame] [file-path]";
    public override string? ProgressMessage => "reviewing code";

    /// <summary>
    /// Maps a --focus value to its specialized prompt name under prompts/system/.
    /// Keys are validated against this map; unknown values yield an error before any
    /// git work is performed.
    /// </summary>
    private static readonly Dictionary<string, string> FocusToPromptName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["security"] = "system/review-security",
        ["crashes"] = "system/review-crashes",
        ["performance"] = "system/review-performance",
        ["style"] = "system/review-style",
    };

    private static readonly string[] AllowedTools =
    [
        "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)",
        "Bash(git blame:*)", "Bash(grep:*)", "Read(*)",
    ];

    private static readonly string[] AllowedToolsWithEdit =
    [
        "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)",
        "Bash(git blame:*)", "Bash(grep:*)", "Read(*)", "Edit(*)", "Write(*)",
    ];

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var noEdit = args.Contains("--no-edit");
        var includeBlame = args.Contains("--blame");

        var severity = ParseFlag(args, "--severity") ?? "all";
        var baseRef = ParseFlag(args, "--base");
        var focus = ParseFlag(args, "--focus");
        if (focus is not null && !FocusToPromptName.ContainsKey(focus))
        {
            var valid = string.Join("|", FocusToPromptName.Keys);
            return CommandResult.Error(
                $"Unknown --focus value '{focus}'. Valid values: {valid}.");
        }
        var filePaths = args.Where(a => !a.StartsWith('-')
            && (baseRef is null || a != baseRef)
            && (focus is null || a != focus)).ToArray();

        // Determine diff scope
        string[] diffArgs;
        string scopeDescription;
        if (baseRef is not null)
        {
            diffArgs = filePaths.Length > 0 ? ["diff", baseRef, "--", .. filePaths] : ["diff", baseRef];
            scopeDescription = $"diff against `{baseRef}`";
        }
        else if (args.Contains("--all"))
        {
            diffArgs = filePaths.Length > 0 ? ["diff", "HEAD", "--", .. filePaths] : ["diff", "HEAD"];
            scopeDescription = "all changes (staged + unstaged)";
        }
        else if (args.Contains("--staged"))
        {
            diffArgs = filePaths.Length > 0 ? ["diff", "--staged", "--", .. filePaths] : ["diff", "--staged"];
            scopeDescription = "staged changes";
        }
        else
        {
            diffArgs = filePaths.Length > 0 ? ["diff", "--", .. filePaths] : ["diff"];
            scopeDescription = "unstaged changes";
        }

        string diff;
        try
        {
            var result = await gitHelper.RunAsync(diffArgs, ct).ConfigureAwait(false);
            if (result is null)
                return CommandResult.Error("git is not available.");
            diff = result.Stdout;
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to get git diff: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(diff))
            return CommandResult.Text($"No changes to review ({scopeDescription}).");

        // Gather context in parallel
        var branchTask = gitHelper.ReadAsync(["branch", "--show-current"], ct);
        var logTask = gitHelper.ReadAsync(["log", "--oneline", "-5"], ct);
        var hashTask = gitHelper.ReadAsync(["log", "--format=%H", "-5"], ct);
        await Task.WhenAll(branchTask, logTask, hashTask).ConfigureAwait(false);
        var branch = await branchTask;
        var recentLog = await logTask;
        var recentHashes = (await hashTask)?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => h.Trim())
            .Where(h => h.Length > 0)
            .ToList() ?? [];

        // Incremental review: skip already-reviewed commits
        var reviewCache = reviewCacheService.Load(baseRef);
        var newHashes = reviewCache.FilterNewCommits(recentHashes).ToList();
        var reviewedCount = recentHashes.Count - newHashes.Count;
        string recentLogDisplay;
        if (newHashes.Count == 0 && recentHashes.Count > 0)
        {
            // All commits already reviewed — but still show the diff for re-review
            recentLogDisplay = $"{recentLog.Trim()}\n(All {recentHashes.Count} commits previously reviewed — re-reviewing for regression)";
        }
        else if (reviewedCount > 0)
        {
            recentLogDisplay = $"{recentLog.Trim()}\n({reviewedCount} previously reviewed commit(s) — {newHashes.Count} new)";
        }
        else
        {
            recentLogDisplay = recentLog.Trim();
        }

        // Try to read AGENTS.md for project-specific review rules
        var repoRoot = await gitHelper.ReadAsync(["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);
        var agentRules = await TryReadAgentsMdAsync(repoRoot?.Trim(), ct).ConfigureAwait(false);

        // Optionally gather git blame for changed files
        string? blameContext = null;
        if (includeBlame)
            blameContext = await TryGatherBlameContextAsync(diff, gitHelper, ct).ConfigureAwait(false);

        // Gather LSP diagnostics for changed files (provides static analysis backing)
        string? lspContext = TryGatherLspDiagnostics(diff);

        var outputFormat = await promptManager.GetPromptAsync("system/review-output-text", ct).ConfigureAwait(false)
            ?? "Structure your response with Summary, Functional Issues, and Design Suggestions sections.";

        var severityScope = severity switch
        {
            "critical" => "Report only **Functional Issues** (bugs that cause errors, crashes, or data loss if left unfixed). Every finding must include file path + line number + code snippet as evidence.",
            "warning" => "Report both **Functional Issues** (will cause errors/data loss if unfixed) and **Design Suggestions** (works correctly but could be improved). Every finding must include file path + line number + code snippet as evidence.",
            _ => "Report ALL findings with their severity: **Functional Issues** (will cause errors/data loss if unfixed), **Design Suggestions** (works correctly but could be improved), and minor/style issues (naming, consistency, readability, dead code). Do not omit low-severity findings. Every finding must include file path + line number + code snippet as evidence.",
        };

        var editInstruction = noEdit
            ? "This is a read-only review. Do NOT use Edit or Write tools — report findings only."
            : "After identifying issues, you MAY use the Edit tool to apply fixes for Functional Issues if the fix is straightforward and non-destructive. Before applying any edit, you MUST first Read the target file to confirm its current content — do not rely on the diff alone.";

        var variables = new Dictionary<string, string>
        {
            ["branch"] = branch ?? "",
            ["scopeDescription"] = scopeDescription,
            ["recentLog"] = recentLogDisplay,
            ["agentRulesSection"] = agentRules is not null
                ? $"\n## Project-Specific Rules (from AGENTS.md)\n{agentRules}\n"
                : "",
            ["diff"] = diff.TrimEnd(),
            ["blameSection"] = blameContext is not null
                ? $"\n## Git Blame Context (changed files)\n{blameContext}\n"
                : "",
            ["lspSection"] = lspContext is not null
                ? $"\n## Static Analysis (LSP Diagnostics)\n{lspContext}\n"
                : "",
            ["severityScope"] = severityScope,
            ["editInstruction"] = editInstruction,
            ["outputFormat"] = outputFormat,
        };

        var promptName = focus is null ? "system/review" : FocusToPromptName[focus];
        var prompt = await LoadPromptAsync(promptManager, promptName, variables, ct).ConfigureAwait(false);
        if (prompt is null)
        {
            return CommandResult.Error(
                $"Prompt '{promptName}' is not available. Verify prompts/{promptName}.prompt exists.");
        }
        var tools = noEdit ? AllowedTools : AllowedToolsWithEdit;

        // Defer cache write until the command-prompt stream completes successfully
        // (QueryStreamService.CommitPending). Cancel/error discards the stage.
        reviewCacheService.ScheduleCommit(baseRef, recentHashes);

        return CommandResult.Prompt(prompt, tools);
    }

    /// <summary>
    /// Parse changed files from the diff and fetch git blame --porcelain for each.
    /// Truncates to 50 blame entries per file to keep the prompt manageable.
    /// </summary>
    private async Task<string?> TryGatherBlameContextAsync(
        string diff, IGitHelper gitHelper, CancellationToken ct)
    {
        try
        {
            var changedFiles = ParseChangedFilesFromDiff(diff);
            if (changedFiles.Count == 0) return null;

            var sb = new StringBuilder();
            // Cap at 5 files to avoid bloating the prompt
            foreach (var file in changedFiles.Take(5))
            {
                if (!File.Exists(file)) continue;
                var result = await gitHelper.RunAsync(
                    ["blame", "--porcelain", file], ct).ConfigureAwait(false);
                if (result is null || string.IsNullOrWhiteSpace(result.Stdout)) continue;

                var entries = GitBlameParser.Parse(result.Stdout, file);
                sb.AppendLine(CultureInfo.InvariantCulture, $"### {file}");
                foreach (var entry in entries.Take(50))
                {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"  L{entry.LineNumber} [{entry.CommitHash[..8]}] {entry.AuthorName} — {entry.Content}");
                }
                sb.AppendLine();
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
        }
        catch (Exception ex)
        {
            if (logger is not null)
                logger.LogWarning(ex, "ReviewCommand.BuildGitBlameContext failed");
            else
                System.Diagnostics.Debug.WriteLine($"ReviewCommand.BuildGitBlameContext failed: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<string> ParseChangedFilesFromDiff(string diff)
    {
        List<string> files = [];
        foreach (var line in diff.Split('\n'))
        {
            // diff --git a/foo.cs b/foo.cs
            if (!line.StartsWith("+++ b/", StringComparison.Ordinal)) continue;
            var path = line["+++ b/".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(path) && path != "/dev/null")
                files.Add(path);
        }
        return files;
    }

    private async Task<string?> TryReadAgentsMdAsync(string? repoRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;
        try
        {
            var candidates = new[]
            {
                Path.Combine(repoRoot, "AGENTS.md"),
                Path.Combine(repoRoot, ".github", "AGENTS.md"),
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    // Extract review-related sections only to avoid bloating the prompt
                    var lines = content.Split('\n');
                    var reviewSection = ExtractSection(lines, ["review", "code quality", "standards", "rules"]);
                    return reviewSection ?? content[..Math.Min(content.Length, 800)];
                }
            }
        }
        catch (Exception ex)
        {
            if (logger is not null)
                logger.LogWarning(ex, "ReviewCommand.LoadReviewContext failed for {RepoRoot}", repoRoot);
            else
                System.Diagnostics.Debug.WriteLine($"ReviewCommand.LoadReviewContext failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Query LspDiagnosticRegistry for diagnostics on changed files.
    /// Only returns Error/Warning severity — skips Information/Hint to avoid noise.
    /// Capped at 200 diagnostics max to avoid prompt bloat.
    /// </summary>
    private string? TryGatherLspDiagnostics(string diff)
    {
        try
        {
            var changedFiles = ParseChangedFilesFromDiff(diff);
            if (changedFiles.Count == 0) return null;

            var allDiagnostics = lspRegistry.GetAllDiagnostics();
            if (allDiagnostics.Count == 0) return null;

            // Match diagnostics to changed files, filter to Error/Warning only
            var relevant = allDiagnostics
                .Where(d => d.Severity is LspDiagnosticSeverity.Error or LspDiagnosticSeverity.Warning)
                .Where(d =>
                {
                    var filePath = d.FilePath;
                    return changedFiles.Any(cf =>
                        filePath.EndsWith(cf, StringComparison.OrdinalIgnoreCase) ||
                        cf.EndsWith(Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase));
                })
                .Take(200)
                .ToList();

            if (relevant.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Existing static analysis issues for changed files ({relevant.Count} Error/Warning):");
            sb.AppendLine();

            var grouped = relevant.GroupBy(d => d.FilePath);
            foreach (var group in grouped)
            {
                var fileName = Path.GetFileName(group.Key);
                sb.AppendLine(CultureInfo.InvariantCulture, $"### {fileName}");
                foreach (var d in group.Take(20)) // max 20 per file
                {
                    var severity = d.Severity == LspDiagnosticSeverity.Error ? "ERROR" : "WARNING";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  [{severity}] {d.Source ?? d.ServerName}: {d.Message}");
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            if (logger is not null)
                logger.LogWarning(ex, "ReviewCommand: LSP diagnostics aggregation failed");
            else
                System.Diagnostics.Debug.WriteLine($"ReviewCommand LSP diagnostics aggregation failed: {ex.Message}");
            return null; // Non-critical; skip
        }
    }

    private static string? ExtractSection(string[] lines, string[] keywords)
    {
        var result = new StringBuilder();
        var inSection = false;
        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                inSection = keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (inSection) result.AppendLine(line);
            }
            else if (inSection)
            {
                result.AppendLine(line);
            }
        }
        return result.Length > 0 ? result.ToString().Trim() : null;
    }
}

