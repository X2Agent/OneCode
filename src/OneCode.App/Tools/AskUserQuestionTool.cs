using System.ComponentModel;

namespace OneCode.App.Tools;

/// <summary>
/// AskUserQuestion tool — asks the user one or more questions and returns their answers.
/// MAF-native: instance method registered via AIFunctionFactory.Create.
///
/// One question: simple dialog (<see cref="IUserQuestionService.AskAsync"/>).
/// Two or more related questions: wizard-style flow (<see cref="IUserQuestionService.AskMultipleAsync"/>).
///
/// Headless fallback: returns an Error result so the agent does not mistake a
/// canned string for a real user answer. The caller must pick a safe default
/// or skip the step that required clarification.
/// </summary>
public sealed class AskUserQuestionTool(IUserQuestionService userQuestionService)
{
    private readonly IUserQuestionService _userQuestionService = userQuestionService;

    [Description("Ask the user one or more questions and block until they respond. Use this only when a blocking decision remains. " +
                 "Gather only information that cannot be resolved from the workspace or a reasonable default. " +
                 "Behavior: the call blocks until the user answers (in TUI mode). In headless mode (no interactive terminal) or when the user cancels, the tool returns an Error result — do NOT treat the error text as a user answer; pick a safe default or skip the step instead. " +
                 "Single question: provide one element in questions; keep it concise and put each complete choice in its options (2-4 concrete choices render an interactive selector in the TUI). " +
                 "Multiple related questions (2-4): provide a title and one element per question — the user navigates between questions, reviews and changes previous answers, then completes or cancels the whole flow. " +
                 "Do NOT embed a numbered option list, multiple independent topics in a single question, or a long planning report in question. " +
                 "Do NOT use this tool for questions you can answer by reading code, checking docs, or making a reasonable default assumption — only ask when genuinely blocked.")]
    public async Task<ToolResult> AskAsync(
        [Description("A descriptive title shown at the top of the dialog. Required when asking 2+ questions (example: 'Project Configuration'); ignored for a single question.")] string? title = null,
        [Description("Questions to ask (1-4). Each element needs 'question' text, plus 'id' when asking more than one; optionally 'options' (predefined choices), 'description', 'type' (singleChoice/multipleChoice/shortText/longText/confirm), 'maxLines', 'allowEmpty'. " +
                     "Example: [{\"id\": \"framework\", \"question\": \"Which framework?\", \"options\": [\"EF Core\", \"Dapper\"]}, {\"id\": \"validation\", \"question\": \"Validation command?\"}]")] AskQuestionInput[]? questions = null,
        CancellationToken ct = default)
    {
        var list = questions is { Length: > 0 } ? questions : [];

        if (list.Length == 0)
        {
            return ToolResult.Error(
                "AskUserQuestion failed: no questions provided.",
                suggestedNextAction: "Provide at least one question with 'question' text (and 'id' when asking more than one).");
        }

        // 单题：简单问答（id 仅向导引用答案时才需要）。
        if (list.Length == 1)
        {
            var single = list[0];
            var answer = await _userQuestionService.AskAsync(single.Question, single.Options, ct).ConfigureAwait(false);
            if (answer is not null)
            {
                return ToolResult.Success($"Question: {single.Question}\nAnswer: {answer}");
            }

            // 服务返回 null：用户取消或无头宿主无接收者。
            // 返回 Error 而非 Success，防止 agent 把取消标记误当作用户的真实回答写进后续决策。
            return ToolResult.Error(
                $"AskUserQuestion failed: the user did not provide an answer (cancelled or no interactive UI). Question was: {single.Question}. " +
                "Do NOT treat this as a user answer.",
                suggestedNextAction: "Choose a reasonable default or skip the step that required user input.");
        }

        // 多题：向导流程。id 用于引用各题答案，缺失即拒绝。
        List<WizardQuestion> wizardQuestions = [];
        foreach (var q in list)
        {
            if (string.IsNullOrEmpty(q.Id) || string.IsNullOrEmpty(q.Question))
            {
                return ToolResult.Error(
                    "Invalid question definition: each question must have 'id' and 'question' properties.",
                    suggestedNextAction: "Fix the question definitions and try again.");
            }

            wizardQuestions.Add(new WizardQuestion(
                q.Id, q.Question, ParseQuestionType(q.Type), q.Options, q.Description, q.MaxLines, q.AllowEmpty));
        }

        var wizardTitle = string.IsNullOrWhiteSpace(title) ? "Questions" : title;
        var result = await _userQuestionService.AskMultipleAsync(wizardTitle, wizardQuestions, ct).ConfigureAwait(false);

        if (result.IsCancelled)
        {
            return ToolResult.Error(
                $"AskUserQuestion wizard cancelled by user. Title was: {wizardTitle}. " +
                "Do NOT treat this as user answers.",
                suggestedNextAction: "Choose reasonable defaults or skip the step that required user input.");
        }

        // relaxed encoder：中文回答保持原文，不被转义成 \uXXXX 发给模型。
        var answersJson = DisplayJsonSerializer.Serialize(result.Answers, writeIndented: true);
        return ToolResult.Success($"Wizard completed: {wizardTitle}\nAnswers:\n{answersJson}");
    }

    private static QuestionType ParseQuestionType(string? typeStr) => typeStr?.ToLowerInvariant() switch
    {
        "singlechoice" => QuestionType.SingleChoice,
        "multiplechoice" => QuestionType.MultipleChoice,
        "shorttext" => QuestionType.ShortText,
        "longtext" => QuestionType.LongText,
        "confirm" => QuestionType.Confirm,
        _ => QuestionType.ShortText // 默认短文本
    };
}

/// <summary>
/// 强类型问题定义：由 MAF 反射绑定直接反序列化，取代原先的
/// JsonElement[] + 手写 GetXxxProperty 属性解析。
/// </summary>
public sealed record AskQuestionInput(
    string Question,
    string? Id = null,
    string[]? Options = null,
    string? Description = null,
    string? Type = null,
    int MaxLines = 10,
    bool AllowEmpty = false);
