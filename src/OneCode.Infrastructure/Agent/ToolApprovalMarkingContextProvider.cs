using Microsoft.Agents.AI;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// 运行期审批边界：把上下文提供器注入的工具一并纳入 MAF 的审批协议。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它。</b><c>ToolApprovalMarker.Apply</c> 只能在管道构建期标记产品工具目录里的工具；
/// 那一时刻 Harness 的待办清单 / 工作记忆提供器还没把工具并入请求，于是这些工具不带
/// <c>ApprovalRequiredAIFunction</c>，<c>FunctionInvokingChatClient</c> 直接执行——权限检查器给出的 Ask
/// 在这条路径上静默失效（fail-open）。
/// </para>
/// <para>
/// <b>为什么挂在上下文提供器而不是 chat client 装饰器上。</b>Harness 把
/// <c>FunctionInvokingChatClient</c> 装在调用方传入的 chat client <b>之上</b>，产品侧与
/// <c>ModelCallHookDecorator</c> 同层的装饰器位于其下方，改写后的工具列表到不了审批判定点。而上下文
/// 提供器按「Harness 自建提供器在前、用户提供器在后」的顺序执行，本类作为最后一个提供器能看到全部工具；
/// <c>ChatClientAgent</c> 对提供器返回值采用替换语义，最终 <c>ChatOptions.Tools</c> 就是本类返回的列表，
/// 批准之后 FICC 也从这个列表取函数实例执行。
/// </para>
/// <para>
/// <b>幂等。</b><c>ToolApprovalMarker.Apply</c> 跳过已带标记的工具，构建期标记过的产品工具经过本类不会被二次包装。
/// </para>
/// </remarks>
public sealed class ToolApprovalMarkingContextProvider : AIContextProvider
{
    private readonly ToolMetadataRegistry _metadata;

    /// <summary>
    /// 初始化运行期审批边界提供器。
    /// </summary>
    /// <param name="metadata">产品工具元数据注册表，提供每个工具名的 <c>ApprovalMode</c>。</param>
    public ToolApprovalMarkingContextProvider(ToolMetadataRegistry metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
    }

    /// <summary>无会话状态：只改写本次请求的工具列表。</summary>
    public override IReadOnlyList<string> StateKeys => [];

    /// <inheritdoc />
    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var input = context.AIContext;
        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = input.Instructions,
            Messages = input.Messages,
            Tools = ToolApprovalMarker.Apply(input.Tools, _metadata),
        });
    }
}
