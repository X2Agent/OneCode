using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 从 run 的流式更新里识别「待办变更工具已执行完成」的时刻，让宿主在工具边界（而不是 run 结束后）
/// 刷新待办投影。只认 add/complete/remove 的结果，只读的 get_* 不触发。
/// </summary>
/// <remarks>
/// 调用名与结果分处不同更新：先按 CallId 记下变更类调用，结果到达时才报告变更。
/// 审批流里调用可能只出现在 <see cref="ToolApprovalRequestContent"/> 内，也一并登记。
/// 实例须跨审批续跑的多次 RunStreamingAsync 迭代复用，否则跨迭代到达的结果会丢失调用名。
/// </remarks>
internal sealed class TodoMutationObserver
{
    private readonly HashSet<string> _pendingMutationCallIds = new(StringComparer.Ordinal);

    /// <summary>返回 true 表示本次更新带来了至少一个待办变更工具的结果。</summary>
    public bool Observe(AgentResponseUpdate update)
    {
        if (update.Contents is not { Count: > 0 } contents)
            return false;

        var mutated = false;
        foreach (var content in contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    TrackCall(call);
                    break;
                case ToolApprovalRequestContent { ToolCall: FunctionCallContent approvalCall }:
                    TrackCall(approvalCall);
                    break;
                case FunctionResultContent result when _pendingMutationCallIds.Remove(result.CallId):
                    mutated = true;
                    break;
            }
        }

        return mutated;
    }

    private void TrackCall(FunctionCallContent call)
    {
        if (!string.IsNullOrWhiteSpace(call.CallId)
            && call.Name is { } name
            && HarnessProviderTools.TodoMutationToolNames.Contains(name))
        {
            _pendingMutationCallIds.Add(call.CallId);
        }
    }
}
