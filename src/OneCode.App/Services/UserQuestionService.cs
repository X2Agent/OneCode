using OneCode.App.Tui;

namespace OneCode.App.Services;

/// <summary>
/// TUI 层用户提问服务的实现 — 通过发射 <see cref="TuiUserQuestionRequest"/> 事件
/// 与 <see cref="OneCodeToplevel"/> 通信，显示交互式提问对话框。
/// </summary>
public sealed class UserQuestionService : IUserQuestionService
{
    private readonly TuiInteractionBridge _bridge;

    public UserQuestionService(TuiInteractionBridge bridge)
    {
        _bridge = bridge;
    }

    public async Task<string?> AskAsync(
        string question,
        IReadOnlyList<string>? options = null,
        CancellationToken ct = default)
    {
        var emitEvent = _bridge.EmitEvent;
        if (emitEvent is null)
        {
            return null;
        }

        var request = new TuiUserQuestionRequest(question, options);

        emitEvent(request);

        try
        {
            using var reg = ct.Register(() => request.ResponseSource.TrySetCanceled(ct));
            return await request.ResponseSource.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<OneCode.Core.Tools.WizardResult> AskMultipleAsync(
        string title,
        IReadOnlyList<OneCode.Core.Tools.WizardQuestion> questions,
        CancellationToken ct = default)
    {
        var emitEvent = _bridge.EmitEvent;
        if (emitEvent is null)
        {
            return OneCode.Core.Tools.WizardResult.Cancelled;
        }

        var request = new TuiQuestionWizardRequest(title, questions);

        emitEvent(request);

        try
        {
            using var reg = ct.Register(() => request.ResponseSource.TrySetCanceled(ct));
            return await request.ResponseSource.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OneCode.Core.Tools.WizardResult.Cancelled;
        }
    }
}
