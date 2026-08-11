namespace OneCode.Core.Tools;

/// <summary>
/// 用户提问服务接口 — 用于 AskUserQuestionTool 与用户进行交互式提问。
/// 实现可以是 TUI 弹窗、命令行交互或 headless 回退。
/// </summary>
public interface IUserQuestionService
{
    /// <summary>
    /// 向用户提问并等待回答。
    /// </summary>
    /// <param name="question">问题内容</param>
    /// <param name="options">可选的预定义选项，为 null 时表示自由文本输入</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>用户的回答，如果取消则返回 null</returns>
    Task<string?> AskAsync(
        string question,
        IReadOnlyList<string>? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// 向用户展示多问题向导并等待完成。
    /// </summary>
    /// <param name="title">向导标题</param>
    /// <param name="questions">问题列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>向导结果，包含所有问题的答案或取消标记</returns>
    Task<WizardResult> AskMultipleAsync(
        string title,
        IReadOnlyList<WizardQuestion> questions,
        CancellationToken ct = default);
}
