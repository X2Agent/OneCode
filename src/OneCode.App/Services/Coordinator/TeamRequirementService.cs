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

    public ImplementationPlan CreateImplementationPlan(RequirementAnalysisResult analysis)
    {
        if (!analysis.CanProceedWithoutClarification)
            throw new InvalidOperationException("Blocking requirement questions must be resolved before Team planning.");

        var goal = analysis.Draft.ProductGoal;
        var allowedPaths = analysis.Draft.InScope.Count > 0
            ? analysis.Draft.InScope
            : [];
        var acceptance = analysis.Draft.AcceptanceCriteria.Count > 0
            ? analysis.Draft.AcceptanceCriteria
            : ["The approved request is implemented without executor failures."];
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
            ProductGoal: goal.Trim(),
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

    private static string Summarize(string goal)
        => goal.Length <= 80 ? goal : goal[..77] + "...";
}
