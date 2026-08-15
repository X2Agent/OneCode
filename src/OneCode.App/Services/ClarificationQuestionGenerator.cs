using System.Text.Json;
using Microsoft.Extensions.AI;
using OneCode.Core.Build;
using OneCode.Core.Prompt;

namespace OneCode.App.Services;

/// <summary>
/// Model-generated requirement intake. Questions is the only field that must succeed;
/// baseline fields are best-effort extras from the same call and replace keyword/regex extraction.
/// </summary>
public sealed record RequirementIntake(
    IReadOnlyList<string> Questions,
    IReadOnlyList<string> InScope,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Constraints)
{
    public static RequirementIntake Empty { get; } = new([], [], [], []);
}

/// <summary>
/// Turns a requirement assessment into request-specific questions plus baseline fields.
/// The deterministic assessment still decides whether clarification is required;
/// this generator writes the question text and optional baseline fields.
/// </summary>
public interface IClarificationQuestionGenerator
{
    Task<RequirementIntake> GenerateAsync(
        string goal,
        RequirementAssessment assessment,
        CancellationToken ct = default);
}

/// <summary>
/// Fail-closed generator: model errors, unparseable output, or empty questions throw.
/// No template/keyword fallback — a fabricated question has no information gain.
/// Prompt-only JSON (no ResponseFormat): some OpenAI-compatible gateways reject
/// every response_format variant (see GoalDecomposer.CreateStructuredChatOptions).
/// </summary>
public sealed class ClarificationQuestionGenerator(
    IChatClient chatClient,
    IPromptManager promptManager) : IClarificationQuestionGenerator
{
    internal const string PromptName = "system/clarification";
    internal const int MaxQuestions = 5;

    internal const string FallbackPrompt = """
        You write clarification questions for a coding agent.
        Ask 1 to 5 questions in the user's language, specific to this request.
        Do not use generic templates. Extract baseline fields only when the user
        explicitly stated them; use empty arrays otherwise.
        Output ONLY JSON: {"questions":["..."],"inScope":[],"acceptanceCriteria":[],"constraints":[]}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<RequirementIntake> GenerateAsync(
        string goal,
        RequirementAssessment assessment,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        string? text;
        try
        {
            var systemPrompt = await promptManager
                .GetPromptOrDefaultAsync(PromptName, FallbackPrompt, ct)
                .ConfigureAwait(false);
            var reasons = assessment.Reasons.Count == 0
                ? "(none listed)"
                : string.Join("\n", assessment.Reasons.Select(reason => $"- {reason}"));
            var userPrompt = $"""
                User request:
                {goal.Trim()}

                Missing-information reasons:
                {reasons}
                """;

            var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt),
                ],
                new ChatOptions { MaxOutputTokens = 768 },
                ct).ConfigureAwait(false);
            text = response.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"澄清问题生成失败：{ex.Message}", ex);
        }

        return Parse(text);
    }

    internal static RequirementIntake Parse(string? text)
    {
        IntakeSet? parsed = null;
        var json = ExtractJsonObject(text);
        if (json is not null)
        {
            try
            {
                parsed = JsonSerializer.Deserialize<IntakeSet>(json, JsonOptions);
            }
            catch (JsonException)
            {
                // Fall through: empty/invalid questions throw below.
            }
        }

        var questions = Sanitize(parsed?.Questions).Take(MaxQuestions).ToArray();
        if (questions.Length == 0)
            throw new InvalidOperationException("澄清问题生成失败：模型未返回有效问题。");

        return new RequirementIntake(
            questions,
            Sanitize(parsed?.InScope),
            Sanitize(parsed?.AcceptanceCriteria),
            Sanitize(parsed?.Constraints));
    }

    private static IReadOnlyList<string> Sanitize(List<string>? values)
        => values?
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray() ?? [];

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return text[start..(end + 1)];
    }

    private sealed record IntakeSet(
        List<string>? Questions,
        List<string>? InScope,
        List<string>? AcceptanceCriteria,
        List<string>? Constraints);
}
