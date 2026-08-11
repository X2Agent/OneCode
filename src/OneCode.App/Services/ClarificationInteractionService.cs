namespace OneCode.App.Services;

public sealed record ClarificationInteractionResult(
    string? Response,
    bool IsCancelled)
{
    public static ClarificationInteractionResult Cancelled { get; } = new(null, true);
}

/// <summary>
/// Shared application-layer adapter for Build/Team requirement clarification.
/// Rendering remains owned by the existing InlineSelector and QuestionWizard components.
/// </summary>
public interface IClarificationInteractionService
{
    Task<ClarificationInteractionResult> AskAsync(
        string title,
        IReadOnlyList<string> questions,
        bool confirmationOnly = false,
        CancellationToken ct = default);
}

public sealed class ClarificationInteractionService(IUserQuestionService userQuestions)
    : IClarificationInteractionService
{
    public async Task<ClarificationInteractionResult> AskAsync(
        string title,
        IReadOnlyList<string> questions,
        bool confirmationOnly = false,
        CancellationToken ct = default)
    {
        if (questions.Count == 0)
            return ClarificationInteractionResult.Cancelled;

        if (confirmationOnly)
        {
            var answer = await userQuestions.AskAsync(
                questions[0],
                ["确认执行", "取消"],
                ct).ConfigureAwait(false);
            return answer is null || answer == "取消"
                ? ClarificationInteractionResult.Cancelled
                : new ClarificationInteractionResult("确认执行", false);
        }

        if (questions.Count == 1)
        {
            var answer = await userQuestions.AskAsync(questions[0], null, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(answer)
                ? ClarificationInteractionResult.Cancelled
                : new ClarificationInteractionResult(answer.Trim(), false);
        }

        var wizardQuestions = questions
            .Select((question, index) => new WizardQuestion(
                $"clarification-{index + 1}",
                question,
                QuestionType.ShortText))
            .ToList();
        var result = await userQuestions.AskMultipleAsync(title, wizardQuestions, ct).ConfigureAwait(false);
        if (result.IsCancelled)
            return ClarificationInteractionResult.Cancelled;

        var combined = wizardQuestions
            .Select(question => result.Answers.TryGetValue(question.Id, out var answer)
                ? $"{question.Question}\n{answer}"
                : null)
            .Where(item => !string.IsNullOrWhiteSpace(item));
        var response = string.Join("\n\n", combined);
        return string.IsNullOrWhiteSpace(response)
            ? ClarificationInteractionResult.Cancelled
            : new ClarificationInteractionResult(response, false);
    }
}
