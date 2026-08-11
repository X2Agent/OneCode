using System.ComponentModel;
using System.Text.RegularExpressions;
using OneCode.App.Services.Agent;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.Core.PlanMode;

namespace OneCode.App.Tools;

/// <summary>
/// Plan Mode 下 LLM 使用的规划工具——拆分为两个独立工具：
/// <see cref="SavePlanAsync"/>（Phase 4 草稿迭代）和 <see cref="SubmitPlanAsync"/>（Phase 5 提交审批）。
/// </summary>
/// <remarks>
/// Plan revisions and approval state are persisted by <see cref="IPlanWorkflowApplicationService"/>.
/// The legacy mutable plan file and conversation metadata are not written by this tool.
/// </remarks>
public sealed class CreatePlanTool(
    IPlanModeService planMode,
    PlanCardPublisher planCardPublisher,
    ISessionConversationAccess sessionManager,
    IPlanWorkflowApplicationService planWorkflow)
{

    /// <summary>
    /// Phase 4：保存 plan 草稿（不退出 Plan 模式）。
    ///
    /// LLM 可多次调用此工具迭代修订 plan，每次调用覆盖同一会话的 plan 文件
    /// 并发布 Draft 状态的 plan card（仅展示，不弹决策面板）。
    /// </summary>
    [Description("Save the plan draft to the plan file without exiting plan mode. Use this during Phase 4 to iteratively refine the plan. The plan card is shown in DRAFT state — the user cannot approve/reject yet. Call SubmitPlan when the plan is finalized.")]
    public async Task<ToolResult> SavePlanAsync(
        [Description("The plan content in markdown format.")] string content,
        [Description("Optional structured steps for the plan card UI.")] PlanStepDto[]? steps = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ToolResult.Error("Plan content cannot be empty.");

        var sessionId = sessionManager.ForegroundConversation?.Id;
        if (sessionId is null)
            return ToolResult.Error("SavePlan requires an active conversation session.");

        if (steps is not { Length: > 0 })
            return ToolResult.Error("SavePlan requires at least one structured step.");

        try
        {
            var definitions = steps.Select(ToDefinition).ToArray();
            var current = await planWorkflow.GetAsync(sessionId.Value, ct).ConfigureAwait(false);
            var result = await planWorkflow.SaveDraftAsync(new SavePlanDraftCommand(
                Guid.NewGuid().ToString("N"),
                sessionId.Value,
                current?.Version ?? -1,
                DeriveTitle(content),
                content,
                definitions,
                [],
                [],
                OneCodeAgentRunContext.CurrentRunId), ct).ConfigureAwait(false);
            planCardPublisher.Publish(result.Workflow);
            return ToolResult.JsonSuccess(new
            {
                status = "plan_saved",
                phase = "draft",
                planId = result.Workflow.Id.ToString(),
                revision = result.Revision.Revision,
                workflowVersion = result.Workflow.Version,
                contentHash = result.Revision.ContentHash,
                message = "Plan draft persisted as a versioned revision. Continue refining or call SubmitPlan when ready.",
            });
        }
        catch (Exception ex) when (ex is PlanValidationException or PlanTransitionException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    /// <summary>
    /// Phase 5：提交最终 plan，持久化 Submitted revision，并立即返回。
    /// 审批仅在当前 Plan Run 完整持久化且协议校验通过后开放。
    /// </summary>
    [Description("Submit the finalized plan for user review. Requires structured steps, runs safety and quality validation, persists an immutable submitted revision, and returns immediately. Approval becomes available only after the current Plan run closes successfully.")]
    public async Task<ToolResult> SubmitPlanAsync(
        [Description("The final plan content in markdown format.")] string content,
        [Description("Required structured execution steps. Each step must include id, title/label, description/content, acceptance criteria, dependencies, files, and risk.")] PlanStepDto[]? steps = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ToolResult.Error("Plan content cannot be empty.");

        if (!planMode.IsInPlanMode)
        {
            return ToolResult.Error(
                "SubmitPlan requires plan mode. Enter plan mode first, then SavePlan/SubmitPlan.");
        }

        // 安全约束（项目硬约束）：Plan 模式退出前必须扫描 plan 内容，检测破坏性命令
        // (rm -rf / git push --force / curl|sh 等)、权限提升请求 (BypassPermissions /
        // disable safety)、prompt injection 短语。命中则拒绝退出（无论交互式还是
        // headless 路径），要求用户介入修订。这是 defense-in-depth —— 即使 LLM 被
        // prompt injection，也无法通过 plan 退出流程执行危险操作。
        var scanResult = PlanContentSafetyScanner.Scan(content);
        if (scanResult.IsSuspicious)
        {
            return ToolResult.Error(
                "Plan content rejected by safety scanner. Matched patterns: " +
                $"{string.Join(", ", scanResult.MatchedPatterns)}. " +
                "Revise the plan to remove destructive commands, privilege escalation, " +
                "or prompt injection phrases before re-submitting.");
        }

        // 机制级质量门槛：SubmitPlan 时强制校验 plan 内容质量，防止 LLM 跳过 Phase 1-3
        // 调研直接提交空泛 plan 退出。这是"指令级防御"（prompt 要求 5 阶段）之外的
        // "机制级防御"——即使 LLM 被 injection 误导提前退出，质量门槛也会拒绝并要求补充。
        var qualityFailures = PlanContentQualityGate.Validate(content);
        if (qualityFailures.Count > 0)
        {
            return ToolResult.Error(
                "Plan content did not pass quality gate for exit. " +
                "Complete Phase 1-3 (investigate codebase, design approach) before exiting:\n" +
                string.Join("\n", qualityFailures.Select(f => $"  - {f}")));
        }

        if (steps is not { Length: > 0 })
            return ToolResult.Error("SubmitPlan requires at least one structured step.");

        var sessionId = sessionManager.ForegroundConversation?.Id;
        if (sessionId is null)
            return ToolResult.Error("SubmitPlan requires an active conversation session.");

        IReadOnlyList<PlanStepDefinition> definitions;
        try
        {
            definitions = steps.Select(ToDefinition).ToArray();
            PlanStepValidator.Validate(definitions);
        }
        catch (PlanValidationException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        var activeRunId = OneCodeAgentRunContext.CurrentRunId;
        if (string.IsNullOrWhiteSpace(activeRunId))
            return ToolResult.Error("SubmitPlan must run inside an active agent run.");

        var title = DeriveTitle(content);
        PlanSubmissionResult submission;
        try
        {
            var current = await planWorkflow.GetAsync(sessionId.Value, ct).ConfigureAwait(false);
            submission = await planWorkflow.SubmitAsync(
                new SubmitPlanCommand(
                    Guid.NewGuid().ToString("N"),
                    sessionId.Value,
                    current?.Version ?? -1,
                    title,
                    content,
                    definitions,
                    [],
                    [],
                    activeRunId),
                ct).ConfigureAwait(false);
        }
        catch (PlanValidationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (PlanTransitionException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        planCardPublisher.Publish(submission.Workflow);

        return ToolResult.JsonSuccess(new
        {
            status = "plan_run_finalizing",
            planId = submission.Workflow.Id.ToString(),
            revision = submission.Revision.Revision,
            workflowVersion = submission.Workflow.Version,
            contentHash = submission.Revision.ContentHash,
            message = "Plan submitted. Approval will become available after the Plan run closes successfully.",
        });
    }

    private static PlanStepDefinition ToDefinition(PlanStepDto step)
        => new()
        {
            Id = step.Id?.Trim() ?? string.Empty,
            Title = (step.Title ?? step.Label)?.Trim() ?? string.Empty,
            Description = (step.Description ?? step.Content)?.Trim() ?? string.Empty,
            Files = step.Files ?? [],
            AcceptanceCriteria = step.AcceptanceCriteria ?? [],
            DependsOn = step.DependsOn ?? [],
            Risk = Enum.TryParse<PlanStepRisk>(step.Risk, ignoreCase: true, out var risk)
                ? risk
                : PlanStepRisk.Low,
        };

    private static string DeriveTitle(string content)
    {
        // Use the first non-empty markdown heading or the first line.
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart(' ', '#', '-');
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed.Length > 60 ? trimmed[..57] + "..." : trimmed;
        }
        return "Plan";
    }

}

/// <summary>
/// Scans plan content for forbidden patterns. Retained as a defense-in-depth utility.
///
/// <para>
/// 两层防御机制：
/// </para>
/// <list type="number">
///   <item>
///     <b>机制级防御（新增）</b>：从 plan markdown 中提取 <c>```sh</c> / <c>```bash</c> /
///     <c>```shell</c> / <c>```ps1</c> code block 中的命令，逐条用
///     <see cref="OneCode.Core.Permissions.DangerousCommandPatterns.Layer0HardDeny"/>
///     检查。比纯正则更结构化——即使 LLM 用变量拼接、间接调用等方式在 markdown 中
///     写入危险命令，只要 code block 中的命令文本匹配 Layer0 模式就会被拦截。
///   </item>
///   <item>
///     <b>正则文本扫描（保留）</b>：扫描 plan 全文中的破坏性命令短语、权限提升请求、
///     prompt injection 指标。模式来源为 <see cref="DangerousCommandPatterns.Layer0HardDeny"/>
///     （单一事实源，消除重复维护）+ 特权/注入专用模式（文本语义，非命令）。
///   </item>
/// </list>
///
/// <para>
/// Catches prompt-injected plans that try to:
/// - execute destructive shell commands (rm -rf /, git push --force, curl|sh)
/// - disable safety invariants or switch to BypassPermissions/Yolo
/// - instruct the agent to ignore previous instructions or auto-approve
/// </para>
///
/// This does NOT replace the safety invariants (which still run on every tool
/// call after exit). Its purpose is to block the privilege-escalation path where
/// a prompt-injected LLM writes a malicious plan and auto-exits Plan mode.
/// </summary>
internal static class PlanContentSafetyScanner
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// plan 内容最大允许长度（64KB）。超过则视为可疑——防止 DoS 或超大 payload 绕过扫描。
    /// </summary>
    private const int MaxPlanContentLength = 64 * 1024;

    // 机制级防御：从 Layer0HardDeny 单一事实源构建正则
    // 与 BashCommandInvariant 使用同一套模式，消除重复维护导致的漂移风险。
    private static readonly (string Name, Regex Regex)[] DestructiveCommandPatterns =
        DangerousCommandPatterns.Layer0HardDeny
            .Select(p => (p.Name, new Regex(p.Pattern, RegexOptions.IgnoreCase, MatchTimeout)))
            .ToArray();

    // 正则文本扫描：特权提升请求（文本语义，非命令，独立维护）
    private static readonly (string Name, Regex Regex)[] PrivilegeEscalationPatterns =
    [
        ("privilege_escalation_bypass", new(@"(BypassPermissions|YoloMode|switch\s+to\s+Yolo|disable\s+safety)", RegexOptions.IgnoreCase, MatchTimeout)),
        ("privilege_escalation_force_approve", new(@"(auto-approve|force-approve|skip\s+approval|skip\s+user\s+review)", RegexOptions.IgnoreCase, MatchTimeout)),
    ];

    // 正则文本扫描：prompt injection 指标（文本语义，独立维护）
    private static readonly (string Name, Regex Regex)[] PromptInjectionPatterns =
    [
        ("prompt_injection_ignore", new(@"ignore\s+(previous|prior|all|your)\s+instructions", RegexOptions.IgnoreCase, MatchTimeout)),
        ("prompt_injection_role_change", new(@"you\s+are\s+now\s+(a|an)\s+(different|jailbroken|unrestricted)", RegexOptions.IgnoreCase, MatchTimeout)),
        ("prompt_injection_reveal_system", new(@"(reveal|show|print|output)\s+(your|the)\s+(system\s+)?prompt", RegexOptions.IgnoreCase, MatchTimeout)),
    ];

    // 机制级防御：markdown code block 命令提取
    // 匹配 ```sh / ```bash / ```shell / ```ps1 / ```powershell 开头的 code block，
    // 提取其中的命令文本逐条检查。即使 LLM 用 markdown 包裹危险命令，也能被检测到。
    private static readonly Regex ShellCodeBlockRegex = new(
        @"```(?:sh|bash|shell|ps1|powershell)\s*\n(.*?)```",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        MatchTimeout);

    public static PlanScanResult Scan(string planContent)
    {
        if (string.IsNullOrWhiteSpace(planContent))
            return new PlanScanResult(IsSuspicious: false, MatchedPatterns: []);

        // 大小限制：防止超大 payload 绕过扫描或导致 ReDoS
        if (planContent.Length > MaxPlanContentLength)
            return new PlanScanResult(IsSuspicious: true, MatchedPatterns: ["plan_content_too_large"]);

        var matched = new List<string>(capacity: 16);

        // 机制级：提取 code block 中的 shell 命令，逐条用 Layer0 正则检查
        // 这是比全文正则更结构化的防御：即使 LLM 用编码/变量拼接在 markdown 中
        // 写入危险命令，只要 code block 中的命令文本匹配 Layer0 模式就会被拦截。
        foreach (Match blockMatch in ShellCodeBlockRegex.Matches(planContent))
        {
            var blockContent = blockMatch.Groups[1].Value;
            // 按行拆分，逐行检查（支持一个 code block 中多条命令）
            foreach (var line in blockContent.Split('\n', '\r'))
            {
                var command = line.Trim();
                if (string.IsNullOrEmpty(command) || command.StartsWith('#'))
                    continue;

                foreach (var (name, regex) in DestructiveCommandPatterns)
                {
                    try
                    {
                        if (regex.IsMatch(command))
                        {
                            matched.Add($"{name}(code_block)");
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        matched.Add($"{name}(code_block)_timeout");
                    }
                }
            }
        }

        // 正则文本扫描：全文扫描破坏性命令（覆盖非 code block 的命令引用）
        ScanPatterns(planContent, DestructiveCommandPatterns, matched);

        // 正则文本扫描：特权提升请求
        ScanPatterns(planContent, PrivilegeEscalationPatterns, matched);

        // 正则文本扫描：prompt injection 指标
        ScanPatterns(planContent, PromptInjectionPatterns, matched);

        return new PlanScanResult(IsSuspicious: matched.Count > 0, MatchedPatterns: matched.Distinct().ToList());
    }

    private static void ScanPatterns(
        string content,
        (string Name, Regex Regex)[] patterns,
        List<string> matched)
    {
        foreach (var (name, regex) in patterns)
        {
            try
            {
                if (regex.IsMatch(content))
                    matched.Add(name);
            }
            catch (RegexMatchTimeoutException)
            {
                // Treat timeout as suspicious — don't auto-approve what we can't scan.
                matched.Add(name + "_timeout");
            }
        }
    }
}

internal sealed record PlanScanResult(bool IsSuspicious, IReadOnlyList<string> MatchedPatterns);

/// <summary>
/// 机制级质量门槛——SubmitPlan 时强制校验 plan 内容质量。
///
/// <para>
/// plan.prompt 的 Phase 4/5 指令是"指令级防御"（软约束），
/// LLM 可能被 injection 误导或误判阶段，在未完成 Phase 1-3 调研时提前退出。
/// 本类提供"机制级防御"（硬约束）：退出前强制校验 plan 内容满足最低质量门槛，
/// 不满足则拒绝退出并返回具体的失败原因，指导 LLM 补充。
/// </para>
///
/// <para>
/// 三项检查（全部通过才允许退出）：
/// <list type="bullet">
///   <item><b>最小长度</b>：plan 内容 ≥ 100 字符。防止"空 plan 退出"。</item>
///   <item><b>结构完整性</b>：至少一个 markdown 标题（<c>#</c>）。确保 plan 是结构化文档而非流水账。</item>
///   <item><b>调研证据</b>（自适应，支持现有项目/Greenfield/混合场景）：
///     <list type="bullet">
///       <item><b>现有项目场景</b>：引用至少一个文件路径（<c>src/</c>、<c>.cs</c>、<c>.py</c> 等）或包含 code block。</item>
///       <item><b>新项目/Greenfield 场景</b>：引用技术栈关键词（React/Vue/Django/Spring/.NET 等）。
///         架构决策关键词（项目结构/架构设计等）作为补充证据但不强制——技术栈选择本身就暗示了架构决策。</item>
///     </list>
///   </item>
/// </list>
/// </para>
///
/// <para>
/// <b>关于硬编码正则的设计权衡</b>：
/// <list type="bullet">
///   <item><b>为何硬编码</b>：这是机制级安全防御，不应因配置缺失或配置错误而失效。
///     硬编码保证了零配置开箱即用，且模式集中管理、易于审计。</item>
///   <item><b>已知局限</b>：无法覆盖所有技术栈/语言/命名约定。当前模式覆盖了主流场景，
///     对于边缘场景（如内部私有框架、非主流语言）可能误拒。</item>
///   <item><b>未来演进</b>：若需配置化，可通过 <c>IOptions&lt;PlanQualityGateOptions&gt;</c> 注入额外模式，
///     但默认模式必须保留（作为不可绕过的底线）。配置只能"增加"模式，不能"减少"。</item>
/// </list>
/// </para>
///
/// <para>
/// 设计权衡：质量门槛不检查"plan 内容是否正确"（那是用户审批环节的职责），
/// 只检查"plan 内容是否具备最低结构化质量"。这避免了过度 restrictive 导致合理 plan 被拒。
/// </para>
/// </summary>
internal static class PlanContentQualityGate
{
    /// <summary>plan 内容最小允许长度（100 字符）。低于此值视为"空 plan 退出"。</summary>
    private const int MinPlanContentLength = 100;

    /// <summary>markdown 标题正则（匹配行首的 #、##、### 等）。</summary>
    private static readonly Regex MarkdownHeadingRegex = new(
        @"^\s{0,3}#{1,6}\s+\S",
        RegexOptions.Multiline,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// 文件路径引用正则——匹配常见的源码文件路径模式：
    /// - 相对路径：src/...、lib/...、app/... 等
    /// - 文件扩展名：.cs、.py、.ts、.js、.go、.rs、.java、.cpp、.h、.md、.yaml、.json 等
    /// </summary>
    private static readonly Regex FilePathReferenceRegex = new(
        @"(?:(?:src|lib|app|test|tests|docs|scripts|components|services|models|views|controllers)/[^\s)""']*|" +
        @"\b\w+\.(?:cs|py|ts|tsx|js|jsx|go|rs|java|cpp|c|h|hpp|md|yaml|yml|json|xml|sql|sh|ps1)\b)",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>code block 正则（匹配任意语言的 markdown code block）。</summary>
    private static readonly Regex CodeBlockRegex = new(
        @"```\w*\s*\n",
        RegexOptions.None,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// 技术栈关键词正则——匹配常见框架/技术栈名称。用于 Greenfield 场景（Phase 1 Branch B）
    /// 的调研证据替代：新项目无文件可引用，但应明确技术选型。
    /// 涵盖前端（React/Vue/Angular/Svelte）、后端（Express/Nest/Django/Flask/Spring/ASP.NET/Rails）、
    /// 移动端（React Native/Flutter/SwiftUI）、构建工具（Vite/Webpack/Turborepo）等。
    /// </summary>
    private static readonly Regex TechStackReferenceRegex = new(
        @"\b(?:React|Vue|Angular|Svelte|SolidJS|Next\.js|Nuxt|Gatsby|Astro|" +
        @"Express|NestJS|Koa|Fastify|Django|Flask|FastAPI|Spring(?:\s+Boot)?|ASP\.NET(?:\s+Core)?|Rails|Laravel|Gin|Echo|Fiber|" +
        @"React\s+Native|Flutter|SwiftUI|Jetpack\s+Compose|Xamarin|MAUI|" +
        @"Vite|Webpack|Turborepo|Rollup|esbuild|Parcel|" +
        @"PostgreSQL|MySQL|MongoDB|Redis|SQLite|DynamoDB|" +
        @"GraphQL|gRPC|REST|WebSocket|" +
        @"Docker|Kubernetes|Terraform|AWS|Azure|GCP)\b",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// 架构决策关键词正则——匹配项目组织/结构相关术语。用于 Greenfield 场景
    /// 作为技术栈引用的补充证据：新项目 plan 应明确代码组织方式。
    /// </summary>
    private static readonly Regex ArchitectureDecisionRegex = new(
        // 英文关键词
        @"\b(?:project\s+structure|directory\s+(?:structure|layout)|" +
        @"module\s+(?:structure|breakdown|organization)|architecture|folder\s+structure|" +
        @"component\s+hierarchy|layer(?:ed)?\s+architecture|monorepo|microservices?|" +
        @"MVC|MVVM|clean\s+architecture|hexagonal)\b" +
        // 中文关键词
        @"|(?:项目结构|目录结构|架构设计|模块划分|分层架构|组件层级|单体仓库|微服务)",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// 校验 plan 内容质量。返回失败原因列表；空列表表示通过。
    /// </summary>
    public static IReadOnlyList<string> Validate(string planContent)
    {
        var failures = new List<string>(capacity: 3);

        if (string.IsNullOrWhiteSpace(planContent))
        {
            failures.Add("Plan content is empty. Complete Phase 1-3 (investigate codebase, design approach) before writing the plan.");
            return failures;
        }

        // 1. 最小长度检查
        if (planContent.Length < MinPlanContentLength)
        {
            failures.Add(
                $"Plan content is too short ({planContent.Length} chars, minimum {MinPlanContentLength}). " +
                "A plan ready for user review should include context, approach, and verification steps. " +
                "Complete Phase 1-3 investigation before finalizing.");
        }

        // 2. 结构完整性检查：至少一个 markdown 标题
        if (!MarkdownHeadingRegex.IsMatch(planContent))
        {
            failures.Add(
                "Plan lacks markdown structure (no headings found). " +
                "Organize the plan with headings like '# Context', '# Approach', '# File Paths', '# Verification'.");
        }

        // 3. 调研证据检查（自适应——支持新项目 Greenfield 场景与混合场景）：
        //    - 现有项目场景：引用文件路径或包含 code block
        //    - 新项目/Greenfield 场景：引用技术栈关键词（架构决策关键词作为补充证据，不强制）
        //    - 混合场景：任一证据满足即可通过
        var hasFilePath = FilePathReferenceRegex.IsMatch(planContent);
        var hasCodeBlock = CodeBlockRegex.IsMatch(planContent);
        var hasTechStack = TechStackReferenceRegex.IsMatch(planContent);
        var hasArchitectureDecision = ArchitectureDecisionRegex.IsMatch(planContent);
        var existingProjectEvidence = hasFilePath || hasCodeBlock;
        // Greenfield 证据：技术栈引用即可；架构决策关键词作为补充但不强制。
        var greenfieldEvidence = hasTechStack;
        if (!existingProjectEvidence && !greenfieldEvidence)
        {
            failures.Add(
                "Plan does not reference any file paths, code blocks, or tech stack keywords. " +
                "Complete Phase 1 (investigate existing codebase with Read/Grep/Glob, OR for new projects " +
                "specify the tech stack) and reflect the evidence in the plan.");
        }
        // 保留 hasArchitectureDecision 的计算用于未来扩展（如更细粒度的质量评分），
        // 当前不参与 pass/fail 判定。

        return failures;
    }
}


/// <summary>
/// DTO for structured plan steps passed to <see cref="CreatePlanTool.SavePlanAsync"/> /
/// <see cref="CreatePlanTool.SubmitPlanAsync"/>. Mirrors <see cref="PlanStep"/> but lives
/// at the tool boundary so the LLM-facing schema is JSON-friendly.
/// </summary>
public sealed class PlanStepDto
{
    [Description("Stable unique step ID used by dependencies and execution tracking.")]
    public string? Id { get; set; }

    [Description("Step title. Label is accepted as a backwards-compatible alias.")]
    public string? Title { get; set; }

    [Description("Backwards-compatible short label shown in the plan card.")]
    public string? Label { get; set; }

    [Description("Detailed step description. Content is accepted as a backwards-compatible alias.")]
    public string? Description { get; set; }

    [Description("Backwards-compatible secondary plan-card content.")]
    public string? Content { get; set; }

    [Description("Files expected to be read or changed by this step.")]
    public string[]? Files { get; set; }

    [Description("Observable conditions that prove this step is complete.")]
    public string[]? AcceptanceCriteria { get; set; }

    [Description("IDs of prerequisite steps.")]
    public string[]? DependsOn { get; set; }

    [Description("One of: low, medium, high. Defaults to low.")]
    public string? Risk { get; set; }

    [Description("Optional agent assigned to this step (shown as → name).")]
    public string? Assignee { get; set; }

    [Description("One of: pending, current, done. Defaults to pending.")]
    public string? Status { get; set; }
}
