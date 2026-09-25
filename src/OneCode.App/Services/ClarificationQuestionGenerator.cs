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
/// 调用形态为"结构化请求 + 文本降级"（<see cref="StructuredChatCall"/>）：优先请求 JSON schema，
/// provider 忽略 schema 或网关拒绝时退回 prompt-only JSON 基线——部分 OpenAI 兼容网关拒绝所有
/// response_format 变体，该基线保证可用性。
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
        // GetResponseAsync<T>（结构化路径）内部会 MakeReadOnly()，未显式指定解析器的实例会在那里抛异常，
        // 且异常会被 StructuredChatCall 的拒绝重试宽捕获吞掉、伪装成网关拒绝。见 StructuredChatCall 的守卫。
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public async Task<RequirementIntake> GenerateAsync(
        string goal,
        RequirementAssessment assessment,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        StructuredChatResult<RequirementIntake> call;
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

            // 结构化请求 + 文本降级：schema 路径直接得到 RequirementIntake；provider 忽略 schema
            // （含输出带围栏）与网关硬拒绝后的无格式重试，都落到 Parse 的文本降级路径。
            call = await StructuredChatCall.CallAsync<RequirementIntake>(
                chatClient,
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt),
                ],
                new ChatOptions { MaxOutputTokens = 768 },
                JsonOptions,
                logger: null,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"澄清问题生成失败：{ex.Message}", ex);
        }

        // 两条路径都必须过 Finalize：清洗 + 5 条上限 + 空问题 fail-closed，
        // 类型化结果不得旁路产品契约。
        return call.Value is not null ? Finalize(call.Value) : Parse(call.Text);
    }

    /// <summary>
    /// 文本降级解析：围栏/括号容错提取 JSON 对象并反序列化，交 <see cref="Finalize"/> 归一化。
    /// JsonException 与"无 JSON 块"一律按"无有效问题"由 Finalize fail-closed。
    /// </summary>
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

        return Finalize(new RequirementIntake(
            Sanitize(parsed?.Questions),
            Sanitize(parsed?.InScope),
            Sanitize(parsed?.AcceptanceCriteria),
            Sanitize(parsed?.Constraints)));
    }

    /// <summary>
    /// 归一化终筛：trim/去空、5 条上限、空问题 fail-closed。
    /// 结构化路径与文本降级路径共用——类型化结果同样必须受产品契约约束。
    /// </summary>
    private static RequirementIntake Finalize(RequirementIntake raw)
    {
        var questions = Sanitize(raw.Questions).Take(MaxQuestions).ToArray();
        if (questions.Length == 0)
            throw new InvalidOperationException("澄清问题生成失败：模型未返回有效问题。");

        return new RequirementIntake(
            questions,
            Sanitize(raw.InScope),
            Sanitize(raw.AcceptanceCriteria),
            Sanitize(raw.Constraints));
    }

    private static IReadOnlyList<string> Sanitize(IReadOnlyList<string>? values)
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
