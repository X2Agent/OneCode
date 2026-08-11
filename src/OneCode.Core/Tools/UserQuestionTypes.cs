namespace OneCode.Core.Tools;

/// <summary>
/// 问题类型枚举。
/// </summary>
public enum QuestionType
{
    /// <summary>单选题 — 从预定义选项中选择一个。</summary>
    SingleChoice,

    /// <summary>多选题 — 从预定义选项中选择多个。</summary>
    MultipleChoice,

    /// <summary>短文本题 — 单行文本输入，适合简短回答。</summary>
    ShortText,

    /// <summary>长文本/文档题 — 多行文本输入，适合详细需求描述。</summary>
    LongText,

    /// <summary>确认题 — Yes/No 二选一。</summary>
    Confirm,
}

/// <summary>
/// 向导中的单个问题定义。
/// </summary>
public sealed class WizardQuestion
{
    /// <summary>问题唯一标识。</summary>
    public string Id { get; }

    /// <summary>问题文本。</summary>
    public string Question { get; }

    /// <summary>问题类型。</summary>
    public QuestionType Type { get; }

    /// <summary>可选的预定义选项（单选/多选/确认题使用）。</summary>
    public IReadOnlyList<string>? Options { get; }

    /// <summary>问题描述/提示（可选）。</summary>
    public string? Description { get; }

    /// <summary>多行文本的最大行数（仅 LongText 类型使用，默认 10）。</summary>
    public int MaxLines { get; }

    /// <summary>是否允许空回答（默认 false）。</summary>
    public bool AllowEmpty { get; }

    /// <summary>用户回答，初始为空。</summary>
    public string? Answer { get; set; }

    /// <summary>多选题的答案列表（仅 MultipleChoice 类型使用）。</summary>
    public List<string> MultipleAnswers { get; } = new();

    public WizardQuestion(
        string id,
        string question,
        QuestionType type = QuestionType.ShortText,
        IReadOnlyList<string>? options = null,
        string? description = null,
        int maxLines = 10,
        bool allowEmpty = false)
    {
        Id = id;
        Question = question;
        Type = type;
        Options = options;
        Description = description;
        MaxLines = maxLines;
        AllowEmpty = allowEmpty;
    }

    /// <summary>是否为选择题类型。</summary>
    public bool IsChoiceType => Type is QuestionType.SingleChoice or QuestionType.MultipleChoice or QuestionType.Confirm;

    /// <summary>是否为文本输入类型。</summary>
    public bool IsTextType => Type is QuestionType.ShortText or QuestionType.LongText;

    /// <summary>获取确认题的选项（是/否）。</summary>
    public static IReadOnlyList<string> ConfirmOptions { get; } = new[] { "是", "否" };
}

/// <summary>
/// 向导结果 — 包含所有问题的答案。
/// </summary>
public sealed record WizardResult(
    IReadOnlyDictionary<string, string> Answers,
    bool IsCancelled = false)
{
    public static WizardResult Cancelled { get; } = new(new Dictionary<string, string>(), true);
}
