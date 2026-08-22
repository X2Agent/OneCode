using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;
using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Coordinator;

public sealed record TeamClarificationQuestion(
    string Id,
    string Question,
    bool Blocking);

public sealed record RequirementAnalysisResult(
    RequirementBaseline Draft,
    IReadOnlyList<TeamClarificationQuestion> Questions,
    bool CanProceedWithoutClarification);

public sealed class TeamRequirementService(
    RequirementAssessmentService assessmentService,
    IClarificationQuestionGenerator questionGenerator)
{
    private const string ClarificationResponseMarker = "Clarification response:";

    /// <summary>
    /// Clarification call rules: an already-answered goal and an assessment that does not
    /// require clarification never hit the model — the generator is only called when the
    /// deterministic gate demands questions, so its fail-closed errors stay scoped to that path.
    /// </summary>
    public async Task<RequirementAnalysisResult> AnalyzeAsync(string goal, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        if (HasClarificationResponse(goal))
            return ComposeResult(goal, RequirementIntake.Empty, canProceed: true);

        var assessment = assessmentService.Assess(goal);
        if (!assessment.RequiresClarification)
            return ComposeResult(goal, RequirementIntake.Empty, canProceed: true);

        var intake = await questionGenerator.GenerateAsync(goal, assessment, ct).ConfigureAwait(false);
        return ComposeResult(goal, intake, canProceed: false);
    }

    internal ImplementationPlan CreateImplementationPlan(RequirementAnalysisResult analysis, TeamConfig? config = null)
    {
        if (!analysis.CanProceedWithoutClarification)
            throw new InvalidOperationException("Blocking requirement questions must be resolved before Team planning.");

        var goal = analysis.Draft.ProductGoal;
        var allowedPaths = analysis.Draft.InScope.Count > 0
            ? analysis.Draft.InScope
            : [];
        // 能力驱动计划：按团队真实工具能力选择计划形态，消除"只读团队被塞进写流水线"的错配。
        var readOnlyPlan = config is not null && !TeamCapabilityProfile.From(config).CanWriteFiles;
        var acceptance = analysis.Draft.AcceptanceCriteria.Count > 0
            ? analysis.Draft.AcceptanceCriteria
            : [readOnlyPlan
                ? "Discussion produces a consolidated conclusion addressing the request."
                : "The approved request is implemented without executor failures."];

        if (readOnlyPlan)
        {
            return CreateReadOnlyDiscussionPlan(analysis, config!, goal, allowedPaths, acceptance);
        }

        var tasks = new List<TeamTaskDefinition>
        {
            new(
                "analysis",
                $"Analyze: {Summarize(goal)}",
                TeamTaskKind.Analysis,
                "planner",
                [],
                ["The approved scope, affected files and risks are identified."],
                TeamToolPolicy.ReadOnly,
                RequiredTools: ["Read", "Grep", "Glob", "LS", "FindReferences"],
                AllowedPaths: allowedPaths,
                ExpectedOutputs: ["Analysis report covering scope, affected files, and risks."],
                MaxAttempts: 3,
                RetryPolicy: TaskRetryPolicy.Default),
            new(
                "implementation",
                $"Implement: {Summarize(goal)}",
                TeamTaskKind.Implementation,
                "executor",
                ["analysis"],
                acceptance,
                TeamToolPolicy.WriteAllowed,
                RequiredTools: ["Read", "Grep", "Glob", "LS", "Edit", "Write"],
                AllowedPaths: allowedPaths,
                RequiredGates: ["build", "unit-test"],
                ExpectedOutputs: ["Implemented changes satisfying the acceptance criteria."],
                // Write tasks: no auto-retry of side effects; rely on OperationId idempotency ledger.
                MaxAttempts: 1,
                RetryPolicy: null),
            new(
                "validation",
                $"Validate: {Summarize(goal)}",
                TeamTaskKind.Acceptance,
                "reviewer",
                ["implementation"],
                ["The implementation satisfies the approved acceptance criteria."],
                TeamToolPolicy.ReadOnly,
                RequiredTools: ["Read", "Grep", "Glob", "LS"],
                AllowedPaths: allowedPaths,
                RequiredGates: ["acceptance"],
                ExpectedOutputs: ["Validation report confirming acceptance criteria are met."],
                MaxAttempts: 3,
                RetryPolicy: TaskRetryPolicy.Default),
        };

        return new ImplementationPlan(
            Summary: $"Execute approved Team request: {goal}",
            Tasks: tasks,
            RequiredGates:
            [
                new QualityGateDefinition("lsp-diagnostics", QualityGateKind.LspDiagnostics, Required: false, "Modified files have no LSP error diagnostics."),
                new QualityGateDefinition("build", QualityGateKind.Build, Required: true, "Project build must pass before commit."),
                new QualityGateDefinition("unit-test", QualityGateKind.UnitTest, Required: true, "Configured unit tests must pass before commit."),
                new QualityGateDefinition("acceptance", QualityGateKind.AcceptanceCriteria, Required: true, "Required tasks must provide acceptance evidence."),
            ],
            Risks: assessmentService.Assess(goal).Risk == BuildRiskLevel.High
                ? ["The request contains operations with elevated change risk."]
                : [],
            NonGoals: analysis.Draft.OutOfScope);
    }

    private RequirementAnalysisResult ComposeResult(
        string goal,
        RequirementIntake intake,
        bool canProceed)
    {
        var questions = intake.Questions
            .Select((question, index) => new TeamClarificationQuestion(
                $"requirement-{index + 1}",
                question,
                Blocking: true))
            .ToList();
        var draft = new RequirementBaseline(
            ProductGoal: GetBaseGoal(goal),
            InScope: intake.InScope,
            OutOfScope: [],
            AcceptanceCriteria: intake.AcceptanceCriteria,
            Constraints: intake.Constraints,
            Assumptions: [],
            OpenQuestions: questions.Select(question => question.Question).ToList(),
            RequiresApproval: true);
        return new RequirementAnalysisResult(draft, questions, canProceed);
    }

    internal static bool HasClarificationResponse(string goal)
    {
        var index = goal.IndexOf(ClarificationResponseMarker, StringComparison.Ordinal);
        if (index < 0)
            return false;
        var answer = goal[(index + ClarificationResponseMarker.Length)..];
        return !string.IsNullOrWhiteSpace(answer);
    }

    /// <summary>
    /// Strips the appended clarification Q&A block from an effective goal so that
    /// plan summaries, task titles and approval cards only show the original request.
    /// The full effective goal (with answers) is still used for execution context.
    /// </summary>
    internal static string GetBaseGoal(string goal)
    {
        var index = goal.IndexOf(ClarificationResponseMarker, StringComparison.Ordinal);
        var baseGoal = index < 0 ? goal : goal[..index].Trim();
        return baseGoal.Length > 0 ? baseGoal : goal.Trim();
    }

    /// <summary>
    /// 只读团队（如 code-review / research）的纯研讨计划：两任务、无 build/unit-test 门禁。
    /// AssigneeRole 取团队成员的真实首个角色，不再虚构 planner/reviewer。
    /// </summary>
    private static ImplementationPlan CreateReadOnlyDiscussionPlan(
        RequirementAnalysisResult analysis,
        TeamConfig config,
        string goal,
        IReadOnlyList<string> allowedPaths,
        IReadOnlyList<string> acceptance)
    {
        var profile = TeamCapabilityProfile.From(config);
        var tools = profile.HasWebAccess
            ? (IReadOnlyList<string>)["Read", "Grep", "Glob", "LS", "FindReferences", "WebSearch", "WebFetch"]
            : (IReadOnlyList<string>)["Read", "Grep", "Glob", "LS", "FindReferences"];
        var synthesizerRole = config.Members
            .FirstOrDefault(member => !string.IsNullOrWhiteSpace(member.Role))?.Role ?? "reviewer";
        return new ImplementationPlan(
            Summary: $"Discuss and resolve the Team request: {goal}",
            Tasks:
            [
                new(
                    "analysis",
                    $"Analyze: {Summarize(goal)}",
                    TeamTaskKind.Analysis,
                    synthesizerRole,
                    [],
                    ["The approved scope, key considerations and open questions are identified."],
                    TeamToolPolicy.ReadOnly,
                    RequiredTools: tools,
                    AllowedPaths: allowedPaths,
                    ExpectedOutputs: ["Analysis covering scope, considerations and open questions."],
                    MaxAttempts: 3,
                    RetryPolicy: TaskRetryPolicy.Default),
                new(
                    "review-synthesis",
                    $"Synthesize: {Summarize(goal)}",
                    TeamTaskKind.Review,
                    synthesizerRole,
                    ["analysis"],
                    acceptance,
                    TeamToolPolicy.ReadOnly,
                    RequiredTools: tools,
                    AllowedPaths: allowedPaths,
                    RequiredGates: ["acceptance"],
                    ExpectedOutputs: ["Consolidated conclusion addressing the request."],
                    MaxAttempts: 3,
                    RetryPolicy: TaskRetryPolicy.Default),
            ],
            RequiredGates:
            [
                new QualityGateDefinition("lsp-diagnostics", QualityGateKind.LspDiagnostics, Required: false, "Modified files have no LSP error diagnostics."),
                new QualityGateDefinition("acceptance", QualityGateKind.AcceptanceCriteria, Required: true, "Required tasks must provide acceptance evidence."),
            ],
            Risks: [],
            NonGoals: analysis.Draft.OutOfScope);
    }

    private static string Summarize(string goal)
        => goal.Length <= 80 ? goal : goal[..77] + "...";
}
