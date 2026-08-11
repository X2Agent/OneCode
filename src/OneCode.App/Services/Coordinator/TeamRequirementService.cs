using System.Text.RegularExpressions;
using OneCode.App.Services.BuildMode;
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

public sealed class TeamRequirementService(RequirementAssessmentService assessmentService)
{
    public RequirementAnalysisResult Analyze(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var assessment = assessmentService.Assess(goal);
        var questionTexts = assessmentService.BuildClarificationQuestions(assessment, goal);
        var questions = questionTexts
            .Select((question, index) => new TeamClarificationQuestion(
                $"requirement-{index + 1}",
                question,
                Blocking: true))
            .ToList();
        var acceptance = ExtractAcceptanceCriteria(goal);
        var target = ExtractTarget(goal);
        var draft = new RequirementBaseline(
            ProductGoal: goal.Trim(),
            InScope: target is null ? [] : [target],
            OutOfScope: [],
            AcceptanceCriteria: acceptance,
            Constraints: [],
            Assumptions: [],
            OpenQuestions: questions.Select(question => question.Question).ToList(),
            RequiresApproval: true);
        return new RequirementAnalysisResult(
            draft,
            questions,
            CanProceedWithoutClarification: questions.Count == 0);
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
            Risks: assessmentService.Assess(goal).Risk == OneCode.Core.Build.BuildRiskLevel.High
                ? ["The request contains operations with elevated change risk."]
                : [],
            NonGoals: analysis.Draft.OutOfScope);
    }

    private static IReadOnlyList<string> ExtractAcceptanceCriteria(string goal)
    {
        var criteria = new List<string>();
        if (ContainsAny(goal, "build", "compile", "构建", "编译"))
            criteria.Add("The project build succeeds.");
        if (ContainsAny(goal, "test", "测试"))
            criteria.Add("Relevant automated tests pass.");
        if (ContainsAny(goal, "fix", "修复"))
            criteria.Add("The reported defect is no longer reproducible.");
        if (ContainsAny(goal, "refactor", "重构"))
            criteria.Add("Existing observable behavior remains compatible unless explicitly changed.");
        return criteria;
    }

    private static string? ExtractTarget(string goal)
    {
        var match = Regex.Match(
            goal,
            @"(?<!\w)(?:[A-Za-z]:[\\/]|[./])?[^\s,，;；]+\.(?:cs|csproj|slnx?|json|ya?ml|md)(?!\w)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }

    private static string Summarize(string goal)
        => goal.Length <= 80 ? goal : goal[..77] + "...";

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
