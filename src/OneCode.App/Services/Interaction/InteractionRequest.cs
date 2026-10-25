using OneCode.Core.Goals;

namespace OneCode.App.Services.Interaction;

/// <summary>
/// 前端交互请求基类 —— 仅在进程内流转，不进入运行事件流。
/// 引擎经 Core 端口发起请求，App 侧端口实现转成本类型交给前端投递通道。
/// </summary>
public abstract record InteractionRequest;

/// <summary>工具审批请求。</summary>
public sealed record ApprovalInteractionRequest(
    string RequestId,
    string ToolName,
    string? ToolInput = null,
    string? AgentName = null,
    string? Reason = null) : InteractionRequest
{
    /// <summary>用于回传用户决策。</summary>
    public TaskCompletionSource<ApprovalDecision> ResponseSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>从 Core 审批请求投影交互请求（补齐展示所需的代理名与原因）。</summary>
    public static ApprovalInteractionRequest FromCore(ApprovalRequest request) =>
        new(request.RequestId, request.ToolName, request.ToolInput, request.AgentName, request.Reason);
}

/// <summary>单问题提问请求。</summary>
public sealed record QuestionInteractionRequest(
    string Question,
    IReadOnlyList<string>? Options = null) : InteractionRequest
{
    /// <summary>用于回传用户回答。</summary>
    public TaskCompletionSource<string?> ResponseSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>多问题向导请求。</summary>
public sealed record WizardInteractionRequest(
    string Title,
    IReadOnlyList<WizardQuestion> Questions) : InteractionRequest
{
    /// <summary>用于回传向导结果。</summary>
    public TaskCompletionSource<WizardResult> ResponseSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Goal 预算耗尽时的决策请求（携带 Core 预算上下文供前端渲染）。</summary>
public sealed record BudgetInteractionRequest(BudgetExhaustedContext Context) : InteractionRequest
{
    /// <summary>用于回传用户决策。</summary>
    public TaskCompletionSource<GoalBudgetDecision> ResponseSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}