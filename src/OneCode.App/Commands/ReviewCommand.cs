using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Lsp;

using OneCode.Core.Prompt;
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

    /// <summary>单份 AGENTS.md 注入上限（字符），规则文件过长时截断，防止撑爆 prompt。</summary>
    private const int MaxAgentsMdChars = 2_000;

    /// <summary>注入的 AGENTS.md 数量上限：取变更文件祖先链上最深的 N 个目录（浅层规则随链自然包含）。</summary>
    private const int MaxAgentsMdFiles = 3;

    /// <summary>diff 注入 prompt 的体量上限（字符），超出按整文件截断，防止超大变更打爆上下文。</summary>
    internal const int MaxDiffPromptChars = 50_000;

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

        var changedFiles = ParseChangedFilesFromDiff(diff);
        // blame 只依赖 diff，与下方 git 查询并行启动
        var blameTask = includeBlame
            ? TryGatherBlameContextAsync(diff, gitHelper, ct)
            : Task.FromResult<string?>(null);

        // Gather context in parallel：独立的 git 查询一批并发
        var branchTask = gitHelper.ReadAsync(["branch", "--show-current"], ct);
        var logTask = gitHelper.ReadAsync(["log", "--oneline", "-5"], ct);
        var hashTask = gitHelper.ReadAsync(["log", "--format=%H", "-5"], ct);
        var repoRootTask = gitHelper.ReadAsync(["rev-parse", "--show-toplevel"], ct);
        await Task.WhenAll(branchTask, logTask, hashTask, repoRootTask).ConfigureAwait(false);
        var branch = await branchTask;
        var recentLog = await logTask;
        var recentHashes = (await hashTask)?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => h.Trim())
            .Where(h => h.Length > 0)
            .ToList() ?? [];
        var repoRoot = (await repoRootTask)?.Trim();

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

        // Try to read AGENTS.md for project-specific review rules（沿变更文件祖先目录分层收集）
        var agentRules = await TryReadAgentsMdAsync(repoRoot, changedFiles, ct).ConfigureAwait(false);

        // Optionally gather git blame for changed files（与上方 git 查询并行的任务在此收口）
        var blameContext = await blameTask.ConfigureAwait(false);

        // Gather LSP diagnostics for changed files (provides static analysis backing)
        string? lspContext = TryGatherLspDiagnostics(changedFiles, repoRoot);

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
            ["diff"] = CapDiffForPrompt(diff.TrimEnd()),
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

            var (output, reviewCompleted) = FormatAggregateOutput(result);
            if (reviewCompleted)
            {
                // 非流式命令路径没有 QueryStreamService.CommitPending，评审完整跑完即落缓存。
                reviewCacheService.CommitPending();
            }
            else
            {
                reviewCacheService.DiscardPending();
            }

            return CommandResult.Text(output);
        }
        catch (InvalidOperationException ex)
        {
            reviewCacheService.DiscardPending();
            return CommandResult.Error($"Parallel review failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 把 DAG 结果格式化为最终报告：聚合成功取聚合输出；聚合失败或未产出终态
    /// （整体超时被兜底为 Failed 时聚合 outcome 缺失）降级输出各视角原始分段，不静默丢报告。
    /// 返回值第二项表示评审是否完整完成（决定增量缓存提交还是丢弃）。
    /// </summary>
    internal static (string Output, bool ReviewCompleted) FormatAggregateOutput(AgentWorkflowResult result)
    {
        var summary = BuildExecutionSummary(result);
        var aggregate = result.TaskOutcomes.SingleOrDefault(outcome => outcome.TaskId == AggregateTaskId);
        if (aggregate is { Success: true })
            return ($"{aggregate.Output!.Trim()}\n\n{summary}", true);

        var reason = aggregate is null
            ? "aggregation did not produce a terminal outcome (workflow timeout or interrupted)"
            : $"aggregation failed: {aggregate.Error}";
        return (
            $"{result.FinalOutput}\n\n(Aggregation unavailable — {reason}; raw perspective sections shown above.)\n\n{summary}",
            false);
    }

    /// <summary>每个 DAG 任务的执行摘要行（状态 / 耗时 / 轮数 / token），追加在报告尾部让并行执行可见。</summary>
    internal static string BuildExecutionSummary(AgentWorkflowResult result)
    {
        static string FormatTokens(long value) => value switch
        {
            >= 1_000_000 => $"{value / 1_000_000.0:F1}M",
            >= 1_000 => $"{value / 1_000.0:F1}k",
            _ => value.ToString(CultureInfo.InvariantCulture),
        };

        var lines = result.TaskOutcomes.Select(outcome =>
        {
            var status = outcome.Success ? "ok" : $"FAILED — {outcome.Error}";
            return string.Create(CultureInfo.InvariantCulture,
                $"- {outcome.TaskId}: {status} · {outcome.DurationMs / 1000.0:F1}s · {outcome.TurnsCompleted} turns"
                + $" · {FormatTokens(outcome.InputTokens)} in / {FormatTokens(outcome.OutputTokens)} out");
        });
        return string.Join("\n", lines);
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

        var rendered = await Task.WhenAll(ParallelPerspectives.Select(async perspective =>
            (perspective.PromptName,
             Prompt: await LoadPromptAsync(promptManager, perspective.PromptName, perspectiveVariables, ct)
                 .ConfigureAwait(false)))).ConfigureAwait(false);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (promptName, prompt) in rendered)
        {
            if (prompt is null)
                return null;
            result[promptName] = prompt;
        }
        return result;
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
    /// Files are fetched concurrently (capped at 5) and assembled in diff order.
    /// </summary>
    private async Task<string?> TryGatherBlameContextAsync(
        string diff, IGitHelper gitHelper, CancellationToken ct)
    {
        try
        {
            var changedFiles = ParseChangedFilesFromDiff(diff);
            if (changedFiles.Count == 0) return null;

            // Cap at 5 files to avoid bloating the prompt
            var blameResults = await Task.WhenAll(changedFiles.Take(5)
                .Where(File.Exists)
                .Select(async file => (File: file, Result: await gitHelper.RunAsync(
                    ["blame", "--porcelain", file], ct).ConfigureAwait(false))))
                .ConfigureAwait(false);

            var sb = new StringBuilder();
            foreach (var (file, result) in blameResults)
            {
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

    /// <summary>
    /// 沿变更文件的祖先目录分层收集 AGENTS.md（仓库根 + 变更文件最近的上层目录），
    /// 让 src/AGENTS.md 这类子目录规范真正进入审查依据。每份截断到
    /// <see cref="MaxAgentsMdChars"/>，最多取 <see cref="MaxAgentsMdFiles"/> 份（浅层在前）。
    /// </summary>
    private async Task<string?> TryReadAgentsMdAsync(
        string? repoRoot, IReadOnlyList<string> changedFiles, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || changedFiles.Count == 0) return null;
        try
        {
            var rootPath = Path.GetFullPath(repoRoot);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in changedFiles)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(rootPath, file)));
                while (dir is not null
                    && dir.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                    && dirs.Add(dir))
                {
                    dir = Path.GetDirectoryName(dir);
                }
            }

            // 取最深 N 个目录（最贴近变更的规则优先入选），按浅→深输出（通用 → 具体）
            var selected = dirs
                .OrderByDescending(d => d.Length)
                .Take(MaxAgentsMdFiles)
                .OrderBy(d => d.Length);

            var sections = new List<string>();
            foreach (var dir in selected)
            {
                var path = Path.Combine(dir, "AGENTS.md");
                if (!File.Exists(path)) continue;
                var content = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();
                if (content.Length == 0) continue;
                if (content.Length > MaxAgentsMdChars)
                    content = content[..MaxAgentsMdChars] + "\n…(truncated)";
                var relative = Path.GetRelativePath(rootPath, path).Replace('\\', '/');
                sections.Add($"### {relative}\n{content}");
            }

            return sections.Count > 0 ? string.Join("\n\n", sections) : null;
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
    /// Matching is exact on normalized absolute paths (diagnostic FilePath comes from
    /// file:// URIs), replacing the old bidirectional-EndsWith heuristic that could
    /// cross-match files sharing a basename.
    /// </summary>
    private string? TryGatherLspDiagnostics(IReadOnlyList<string> changedFiles, string? repoRoot)
    {
        try
        {
            if (changedFiles.Count == 0) return null;

            var allDiagnostics = lspRegistry.GetAllDiagnostics();
            if (allDiagnostics.Count == 0) return null;

            var changedSet = changedFiles
                .Select(cf => repoRoot is null ? cf : Path.Combine(repoRoot, cf))
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Match diagnostics to changed files, filter to Error/Warning only
            var relevant = allDiagnostics
                .Where(d => d.Severity is LspDiagnosticSeverity.Error or LspDiagnosticSeverity.Warning)
                .Where(d => changedSet.Contains(NormalizePath(d.FilePath)))
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

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>
    /// 按整文件截断注入 prompt 的 diff：预算内能装下多少整文件就装多少；
    /// 单文件即超限时截头保留第一个文件；尾部附省略文件清单与缩小范围提示。
    /// 预算内返回原文。
    /// </summary>
    internal static string CapDiffForPrompt(string diff)
    {
        if (diff.Length <= MaxDiffPromptChars) return diff;

        var files = SplitDiffByFile(diff);
        var builder = new StringBuilder();
        var budget = MaxDiffPromptChars;
        var included = 0;
        foreach (var (_, chunk) in files)
        {
            if (chunk.Length > budget) break;
            builder.Append(chunk);
            budget -= chunk.Length;
            included++;
        }

        if (included == 0 && files.Count > 0)
        {
            // 单文件即超限：保留其截断头部，保证评审仍有实质内容
            builder.Append(files[0].Chunk.AsSpan(0, Math.Min(files[0].Chunk.Length, MaxDiffPromptChars)));
            included = 1;
        }

        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"[diff truncated: showing {included} of {files.Count} changed files]");
        var omitted = files.Skip(included).Select(f => f.FileName).ToList();
        foreach (var name in omitted.Take(20))
            builder.AppendLine(CultureInfo.InvariantCulture, $"  - {name}");
        if (omitted.Count > 20)
            builder.AppendLine(CultureInfo.InvariantCulture, $"  - … ({omitted.Count - 20} more omitted)");
        builder.AppendLine("[Narrow the scope: /review --staged | --base <ref> | <file-path>]");
        return builder.ToString();
    }

    /// <summary>以 "diff --git " 行为界把 diff 切成 (文件名, 块) 序列。</summary>
    internal static IReadOnlyList<(string FileName, string Chunk)> SplitDiffByFile(string diff)
    {
        var chunks = new List<(string FileName, string Chunk)>();
        var start = diff.IndexOf("diff --git ", StringComparison.Ordinal);
        while (start >= 0)
        {
            var next = diff.IndexOf("diff --git ", start + 1, StringComparison.Ordinal);
            var chunk = next < 0 ? diff[start..] : diff[start..next];
            chunks.Add((ExtractDiffFileName(chunk), chunk));
            start = next;
        }
        return chunks;
    }

    /// <summary>从 "diff --git a/X b/Y" 头行取 Y（兼容含空格路径）。</summary>
    private static string ExtractDiffFileName(string chunk)
    {
        const string marker = " b/";
        var firstLineEnd = chunk.IndexOf('\n');
        var firstLine = firstLineEnd < 0 ? chunk : chunk[..firstLineEnd];
        var idx = firstLine.IndexOf(marker, StringComparison.Ordinal);
        return idx < 0 ? firstLine : firstLine[(idx + marker.Length)..].Trim();
    }
}

