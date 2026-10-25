using OneCode.App.Query;
using OneCode.Infrastructure.Lsp;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.Core.Prompt;

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

    /// <summary>Shared Markdown output format contract injected into all review prompts via <c>{{outputFormat}}</c>.</summary>
    internal const string OutputFormatSection =
        """
        ## Output Format (Markdown)

        Structure your response as:

        ### Summary
        One paragraph summarising the overall quality and the most important findings.

        ### Functional Issues
        Issues that cause errors, crashes, or data loss if left unfixed.

        For each issue use this exact format:
        ```
        **[SEVERITY] Category — file.cs:line**
        ```csharp
        // relevant code snippet from the file
        ```
        Description of the problem.
        > 💡 Fix: how to fix it.
        ```

        ### Design Suggestions
        Issues where the code works correctly but could be improved.

        For each issue use this exact format:
        ```
        **[SEVERITY] Category — file.cs:line**
        ```csharp
        // relevant code snippet from the file
        ```
        Description of the problem.
        > 💡 Suggestion: how to improve.
        ```

        Severity tags: `[CRITICAL]` `[WARNING]` `[SUGGESTION]`
        Category tags: `Bug` `Security` `Performance` `Quality` `Style`

        If a section has no issues, state "None" explicitly.

        End with a **Verdict** line:
        - ✅ LGTM — no blockers found
        - ⚠️ NEEDS WORK — has warnings that should be addressed
        - 🚫 BLOCKED — has critical issues that must be fixed before merging
        """;

    private static readonly string[] AllowedTools =
    [
        "Git", "Read(*)", "Grep(*)", "Glob(*)", "LS(*)", "Lsp(*)",
        "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)", "Bash(git blame:*)",
    ];

    private static readonly string[] AllowedToolsWithEdit =
    [
        "Git", "Read(*)", "Grep(*)", "Glob(*)", "LS(*)", "Lsp(*)",
        "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)", "Bash(git blame:*)",
        "Edit(*)", "Write(*)",
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

        var changedFiles = ReviewContextHelper.ParseChangedFilesFromDiff(diff);

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
        var blameTask = includeBlame
            ? ReviewContextHelper.TryGatherBlameContextAsync(
                diff,
                string.IsNullOrWhiteSpace(repoRoot) ? Environment.CurrentDirectory : repoRoot,
                gitHelper,
                logger,
                ct)
            : Task.FromResult<string?>(null);

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
        var agentRules = await ReviewContextHelper.TryReadAgentsMdAsync(
            repoRoot, changedFiles, MaxAgentsMdFiles, MaxAgentsMdChars, logger, ct).ConfigureAwait(false);

        // Optionally gather git blame for changed files（与上方 git 查询并行的任务在此收口）
        var blameContext = await blameTask.ConfigureAwait(false);

        // Gather LSP diagnostics for changed files (provides static analysis backing)
        string? lspContext = ReviewContextHelper.TryGatherLspDiagnostics(lspRegistry, changedFiles, repoRoot, logger);

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
            ["outputFormat"] = OutputFormatSection,
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

    internal static string CapDiffForPrompt(string diff) =>
        ReviewContextHelper.CapDiffForPrompt(diff, MaxDiffPromptChars);

    internal static IReadOnlyList<(string FileName, string Chunk)> SplitDiffByFile(string diff) =>
        ReviewContextHelper.SplitDiffByFile(diff);
}

