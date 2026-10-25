namespace OneCode.App.Services.Interaction;

/// <summary>
/// 提问端口的交互式实现：把提问 / 向导请求交给前端通道并等待回答。
/// 无接收者或用户取消时返回空结果，由调用工具自行降级。
/// </summary>
public sealed class InteractionUserQuestionService(IInteractionDispatcher dispatcher) : IUserQuestionService
{
    public async Task<string?> AskAsync(
        string question,
        IReadOnlyList<string>? options = null,
        CancellationToken ct = default)
    {
        var request = new QuestionInteractionRequest(question, options);
        try
        {
            return await InteractionAwaiter.WaitAsync(
                dispatcher, request, request.ResponseSource, (string?)null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<WizardResult> AskMultipleAsync(
        string title,
        IReadOnlyList<WizardQuestion> questions,
        CancellationToken ct = default)
    {
        var request = new WizardInteractionRequest(title, questions);
        try
        {
            return await InteractionAwaiter.WaitAsync(
                dispatcher, request, request.ResponseSource, WizardResult.Cancelled, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WizardResult.Cancelled;
        }
    }
}