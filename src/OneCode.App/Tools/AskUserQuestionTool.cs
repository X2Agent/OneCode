using System.ComponentModel;

namespace OneCode.App.Tools;

/// <summary>
/// AskUserQuestion tool — asks the user a question and returns their answer.
/// MAF-native: instance method registered via AIFunctionFactory.Create.
///
/// Interactive mode: uses <see cref="IUserQuestionService"/> to show TUI dialog.
/// Headless fallback: returns an Error result so the agent does not mistake a
/// canned string for a real user answer. The caller must pick a safe default
/// or skip the step that required clarification.
/// </summary>
public sealed class AskUserQuestionTool
{
    private readonly IUserQuestionService _userQuestionService;

    public AskUserQuestionTool(IUserQuestionService userQuestionService)
    {
        _userQuestionService = userQuestionService;
    }

    [Description("Ask the user one focused question and block until they respond. Use this only when exactly one blocking decision remains. " +
                 "If two or more related questions are already known, call AskUserQuestions once instead of asking them serially across agent turns. " +
                 "Behavior: the call blocks until the user answers (in TUI mode). In headless mode (no interactive terminal) or when the user cancels, the tool returns an Error result — do NOT treat the error text as a user answer; pick a safe default or skip the step instead. " +
                 "Use options whenever there are 2-4 concrete choices so the TUI renders an interactive selector; omit options only for genuinely free-text answers. " +
                 "Keep question concise and put each complete choice in options. Do NOT embed a numbered option list, multiple independent questions, or a long planning report in question. " +
                 "Do NOT use this tool for questions you can answer by reading code, checking docs, or making a reasonable default assumption — only ask when genuinely blocked.")]
    public async Task<ToolResult> AskAsync(
        [Description("The question to ask. Be specific and include the trade-off or context the user needs to decide. Avoid yes/no questions when a choice between concrete options is possible.")] string question,
        [Description("Optional predefined options for the user to choose from. When provided, the user picks one; when omitted, free-text input is accepted. " +
                     "Example: ['EF Core', 'Dapper', 'ADO.NET']."),] string[]? options = null,
        CancellationToken ct = default)
    {
        // Headless 路径：没有交互式服务可用。
        // 返回 Error 而非 Success，防止 agent 把固定字符串误当作用户的真实回答写进后续决策。
        if (_userQuestionService is null)
        {
            return ToolResult.Error(
                $"AskUserQuestion failed: no interactive terminal is available. Question was: {question}. " +
                "Do NOT treat this as a user answer — proceed with a safe default or skip the action that required clarification.",
                suggestedNextAction: "Choose a reasonable default or skip the step that required user input.");
        }

        var answer = await _userQuestionService.AskAsync(question, options, ct).ConfigureAwait(false);
        if (answer is not null)
        {
            return ToolResult.Success($"Question: {question}\nAnswer: {answer}");
        }

        // 服务返回 null：可能是用户取消，也可能是 TUI 环境不可用（TuiContext 未注入）。
        // 同样返回 Error，避免 agent 把取消标记当作用户意图。
        return ToolResult.Error(
            $"AskUserQuestion failed: the user did not provide an answer (cancelled or no interactive UI). Question was: {question}. " +
            "Do NOT treat this as a user answer.",
            suggestedNextAction: "Choose a reasonable default or skip the step that required user input.");
    }

    [Description("Ask the user multiple related questions in one wizard-style flow. Prefer this whenever two or more blocking questions are already known, especially during Plan mode, rather than asking one question per agent turn. " +
                 "Gather only information that cannot be resolved from the workspace or a reasonable default. Keep the batch cohesive and usually between 2 and 4 questions. " +
                 "The user can navigate between questions, review and change previous answers, and complete or cancel the entire flow. " +
                 "Behavior: the call blocks until the user completes or cancels the wizard. In headless mode, returns an Error result.")]
    public async Task<ToolResult> AskMultipleAsync(
        [Description("A descriptive title for the wizard flow, shown at the top of the dialog. Example: 'Project Configuration' or 'Feature Selection'.")] string title,
        [Description("Array of questions to ask. Each question has an ID (for referencing the answer), the question text, optional predefined options, and optional description. " +
                     "Example: [{ 'id': 'framework', 'question': 'Which framework?', 'options': ['React', 'Vue', 'Angular'] }, { 'id': 'name', 'question': 'Project name?' }]")] JsonElement[] questions,
        CancellationToken ct = default)
    {
        // Headless 路径：没有交互式服务可用时返回 Error，避免 agent 把固定字符串当作真实回答。
        if (_userQuestionService is null)
        {
            return ToolResult.Error(
                $"AskUserQuestion.AskMultipleAsync failed: no interactive terminal is available. Wizard title was: {title}. " +
                "Do NOT treat this as user answers — proceed with safe defaults or skip the action that required clarification.",
                suggestedNextAction: "Choose reasonable defaults or skip the step that required user input.");
        }

        var wizardQuestions = new List<WizardQuestion>();
        foreach (var q in questions)
        {
            var id = GetStringProperty(q, "id");
            var question = GetStringProperty(q, "question");

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(question))
            {
                return ToolResult.Error(
                    "Invalid question definition: each question must have 'id' and 'question' properties.",
                    suggestedNextAction: "Fix the question definitions and try again.");
            }

            var description = GetStringProperty(q, "description");
            var options = GetStringArrayProperty(q, "options");
            var typeStr = GetStringProperty(q, "type") ?? "shortText";
            var maxLines = GetIntProperty(q, "maxLines") ?? 10;
            var allowEmpty = GetBoolProperty(q, "allowEmpty") ?? false;

            var type = ParseQuestionType(typeStr);
            wizardQuestions.Add(new WizardQuestion(id, question, type, options, description, maxLines, allowEmpty));
        }

        if (wizardQuestions.Count == 0)
        {
            return ToolResult.Error(
                "No valid questions provided.",
                suggestedNextAction: "Provide at least one question with 'id' and 'question' properties.");
        }

        var result = await _userQuestionService.AskMultipleAsync(title, wizardQuestions, ct).ConfigureAwait(false);

        if (result.IsCancelled)
        {
            return ToolResult.Error(
                $"AskUserQuestion wizard cancelled by user. Title was: {title}. " +
                "Do NOT treat this as user answers.",
                suggestedNextAction: "Choose reasonable defaults or skip the step that required user input.");
        }

        var answersJson = JsonSerializer.Serialize(result.Answers, new JsonSerializerOptions { WriteIndented = true });
        return ToolResult.Success($"Wizard completed: {title}\nAnswers:\n{answersJson}");
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }
        return null;
    }

    private static IReadOnlyList<string>? GetStringArrayProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Array)
        {
            return property.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString())
                .Where(s => s != null)
                .Cast<string>()
                .ToList();
        }
        return null;
    }

    private static int? GetIntProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number)
        {
            return property.GetInt32();
        }
        return null;
    }

    private static bool? GetBoolProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.True)
                return true;
            if (property.ValueKind == JsonValueKind.False)
                return false;
        }
        return null;
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
