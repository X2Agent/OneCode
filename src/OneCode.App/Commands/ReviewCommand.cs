using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Lsp;

using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Git;
using System.Text;

using OneCode.Core.Lsp;

namespace OneCode.App.Commands;

/// <summary>
/// /review — AI-powered code review with severity levels, scope control and structured output.
///
/// Default (no --focus) runs a **three-perspective parallel review** through the shared MAF DAG
/// (AgentTaskWorkflowCompiler, same base as ParallelAgents): security / performance / style
/// read-only branches execute in parallel, then an aggregate task dedupes and merges findings
/// into one report. All branches are read-only (Read/Grep/… only — no Bash, no Edit/Write).
///
/// --focus switches to a single specialized streaming review:
///   - --focus security     → prompts/system/review-security.prompt (OWASP, secrets, injection, crypto)
///   - --focus crashes      → prompts/system/review-crashes.prompt (null deref, bounds, races, leaks)
///   - --focus performance  → prompts/system/review-performance.prompt (N+1, allocations, blocking I/O)
///   - --focus style        → prompts/system/review-style.prompt (naming, consistency, readability)
///
/// Usage:
///   /review                   → three-perspective parallel review (security+performance+style)
///   /review --staged          → review staged changes only
///   /review --all             → review staged + unstaged (git diff HEAD)
///   /review --base main       → review diff against a branch/commit
///   /review --severity critical|warning|all  → filter output severity
///   /review --focus security  → single specialized streaming review
///   /review --no-edit         → report only, do not suggest edits (single-view path)
///   /review --blame           → attach git blame context for changed lines
///   /review [file-path]       → restrict review to a specific file or directory
/// </summary>
public sealed class ReviewCommand(
    IGitHelper gitHelper,
    IPromptManager promptManager,
    LspDiagnosticRegistry lspRegistry,
    ReviewCacheService reviewCacheService,
    AgentTaskWorkflowCompiler workflowCompiler,
    AgentTaskWorkflowHost workflowHost,
    ICacheSafeParamsProvider? cacheSafeParams = null,
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

    /// <summary>
    /// Default (no --focus) parallel perspectives. Each key doubles as the DAG task id
    /// (<c>review-&lt;key&gt;</c>) and the specialized prompt name (<c>system/review-&lt;key&gt;</c>).
    /// </summary>
    internal static readonly (string Key, string PromptName)[] ParallelPerspectives =
    [
        ("security", "system/review-security"),
        ("performance", "system/review-performance"),
        ("style", "system/review-style"),
    ];

    internal const string AggregateTaskId = "review-aggregate";
    internal const string AggregatePromptName = "system/review-aggregate";

    /// <summary>Read-only sub-agents have no edit tools; the edit instruction is always report-only.</summary>
    private const string ReadOnlyEditInstruction =
        "This is a read-only review. Do NOT use Edit or Write tools — report findings only.";

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
        var a = CommandArgs.Parse(args, ["severity", "base", "focus"]);
        var noEdit = a.Has("no-edit");
        var includeBlame = a.Has("blame");

        var severity = a.Value("severity") ?? "all";
        var baseRef = a.Value("base");
        var focus = a.Value("focus");
        if (focus is not null && !FocusToPromptName.ContainsKey(focus))
        {
            var valid = string.Join("|", FocusToPromptName.Keys);
            return CommandResult.Error(
                $"Unknown --focus value '{focus}'. Valid values: {valid}.");
        }
        // 值型旗标的值已被解析器消费，Positionals 只剩文件路径
        var filePaths = a.Positionals.ToArray();

        // Determine diff scope
        string[] diffArgs;
        string scopeDescription;
        if (baseRef is not null)
        {
            diffArgs = filePaths.Length > 0 ? ["diff", baseRef, "--", .. filePaths] : ["diff", baseRef];
            scopeDescription = $"diff against `{baseRef}`";
        }
        else if (a.Has("all"))
        {
            diffArgs = filePaths.Length > 0 ? ["diff", "HEAD", "--", .. filePaths] : ["diff", "HEAD"];
            scopeDescription = "all changes (staged + unstaged)";
        }
        else if (a.Has("staged"))
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

        // Defer cache write until the review completes successfully. The single-view path
        // commits via QueryStreamService.CommitPending after the prompt stream; the parallel
        // path commits itself. Cancel/error discards the stage.
        reviewCacheService.ScheduleCommit(baseRef, recentHashes);

        if (focus is null)
            return await RunParallelReviewAsync(variables, ct).ConfigureAwait(false);

        var promptName = FocusToPromptName[focus];
        var prompt = await LoadPromptAsync(promptManager, promptName, variables, ct).ConfigureAwait(false);
        if (prompt is null)
        {
            return CommandResult.Error(
                $"Prompt '{promptName}' is not available. Verify prompts/{promptName}.prompt exists.");
        }
        var tools = noEdit ? AllowedTools : AllowedToolsWithEdit;

        return CommandResult.Prompt(prompt, tools);
    }

    /// <summary>
    /// 三视角并行评审（默认路径）：security / performance / style 三个只读分支经共享 MAF DAG
    /// （<see cref="AgentTaskWorkflowCompiler"/>，与 ParallelAgents 同一底座）并行执行，聚合任务
    /// 等三视角全部完成后汇总结论并去重。全部任务 <see cref="AgentTaskExecutionAccess.ReadOnly"/>，
    /// 工具面收敛到 ReadOnlyAgentTools（无 Bash/Edit/Write），比单视角串行路径更严格。
    /// 聚合失败时降级返回各视角原始分段（FinalOutput 已含每任务错误行），不静默丢报告。
    /// </summary>
    internal async Task<CommandResult> RunParallelReviewAsync(
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct)
    {
        var tasks = BuildPerspectiveTasks(
            await RenderPerspectivePromptsAsync(variables, ct).ConfigureAwait(false),
            await LoadPromptAsync(promptManager, AggregatePromptName, variables, ct).ConfigureAwait(false));
        if (tasks.Count == 0)
            return CommandResult.Error(
                $"Parallel review requires prompts {string.Join(", ", ParallelPerspectives.Select(p => p.PromptName))} and {AggregatePromptName}.");

        try
        {
            var workflow = workflowCompiler.Compile(
                tasks,
                cacheSafeParams?.Current,
                ToolActivationContext.CurrentCapabilities);
            var result = await workflowHost.RunAsync(workflow, tasks, ct).ConfigureAwait(false);

            var aggregate = result.TaskOutcomes.Single(outcome => outcome.TaskId == AggregateTaskId);
            if (aggregate.Success)
            {
                // 非流式命令路径没有 QueryStreamService.CommitPending，评审完整跑完即落缓存。
                reviewCacheService.CommitPending();
                return CommandResult.Text(aggregate.Output!.Trim());
            }

            reviewCacheService.DiscardPending();
            return CommandResult.Text(
                $"{result.FinalOutput}\n\n(Aggregation task failed: {aggregate.Error}; raw perspective sections shown above.)");
        }
        catch (InvalidOperationException ex)
        {
            reviewCacheService.DiscardPending();
            return CommandResult.Error($"Parallel review failed: {ex.Message}");
        }
    }

    /// <summary>渲染三视角 prompt；任一模板缺失返回 null（调用方显式报错，不静默降级）。</summary>
    private async Task<IReadOnlyDictionary<string, string>?> RenderPerspectivePromptsAsync(
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct)
    {
        // 只读子代理没有 Edit/Write 工具，编辑指令固定为 report-only。
        var perspectiveVariables = new Dictionary<string, string>(variables)
        {
            ["editInstruction"] = ReadOnlyEditInstruction,
        };

        var rendered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, promptName) in ParallelPerspectives)
        {
            var prompt = await LoadPromptAsync(promptManager, promptName, perspectiveVariables, ct).ConfigureAwait(false);
            if (prompt is null)
                return null;
            rendered[promptName] = prompt;
        }
        return rendered;
    }

    /// <summary>
    /// 组装 4 任务 DAG：三个只读视角分支（不注入上游、互不依赖 → MAF fan-out 并行）+
    /// 一个聚合任务（依赖全部视角，barrier 汇聚，上游输出由 DAG 自动注入 prompt）。
    /// </summary>
    internal static IReadOnlyList<AgentWorkflowTask> BuildPerspectiveTasks(
        IReadOnlyDictionary<string, string>? renderedPerspectivePrompts,
        string? aggregatePrompt)
    {
        if (renderedPerspectivePrompts is null || aggregatePrompt is null)
            return [];

        var tasks = new List<AgentWorkflowTask>(ParallelPerspectives.Length + 1);
        foreach (var (key, promptName) in ParallelPerspectives)
        {
            tasks.Add(new AgentWorkflowTask(
                $"review-{key}",
                renderedPerspectivePrompts[promptName],
                "general-purpose",
                $"{key} perspective review",
                [],
                InjectUpstreamResults: false));
        }
        tasks.Add(new AgentWorkflowTask(
            AggregateTaskId,
            aggregatePrompt,
            "general-purpose",
            "aggregate and dedupe review findings",
            ParallelPerspectives.Select(p => $"review-{p.Key}").ToArray(),
            InjectUpstreamResults: true));
        return tasks;
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

