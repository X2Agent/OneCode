using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// R1 行为契约：工具必须先带上 MAF 原生审批标记，审批流程才会真正发生。
/// </summary>
/// <remarks>
/// <para>
/// 缺口背景：产品权限层给出 <c>Ask</c> 只代表「需要问」，并不使运行时去问任何人。只有当工具携带
/// <see cref="ApprovalRequiredAIFunction"/> 时，<c>FunctionInvokingChatClient</c> 才会把它转成
/// <see cref="ToolApprovalRequestContent"/> 而不是直接调用。修复前 <c>ToolCatalog</c> 直接返回注册函数，
/// 于是 <c>Ask</c> 在默认模式下等于「直接执行」。
/// </para>
/// <para>
/// 这些用例走真实 <see cref="HarnessAgent"/> 链路（真实 FICC、真实审批装饰器），
/// 只用可控 ChatClient 驱动模型输出，因此不依赖外网模型。
/// </para>
/// </remarks>
public sealed class ToolApprovalMarkerTests
{
    private const string DangerousToolName = "Write";

    [Fact]
    public async Task ApprovalRequiredTool_IsNotExecutedBeforeApproval()
    {
        var executed = false;
        var (agent, client) = CreateAgent(
            toolName: DangerousToolName,
            approvalMode: ToolApprovalMode.Always,
            onInvoke: () => executed = true);

        var response = await agent.RunAsync("please write", await agent.CreateSessionAsync());

        executed.Should().BeFalse("a tool behind an approval boundary must not run before the approval");
        response.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>()
            .Should().ContainSingle()
            .Which.ToolCall.Should().BeOfType<FunctionCallContent>()
            .Which.Name.Should().Be(DangerousToolName);
        client.CallCount.Should().Be(1, "the model is called once; the tool call is withheld for approval");
    }

    /// <summary>
    /// 反证：没有审批标记时同一工具会被直接执行。
    /// 这证明上面的用例测的是「标记接线」，而不是「工具调用本来就不执行」。
    /// </summary>
    [Fact]
    public async Task ToolWithoutApprovalMarker_ExecutesWithoutApproval()
    {
        var executed = false;
        var (agent, _) = CreateAgent(
            toolName: DangerousToolName,
            approvalMode: ToolApprovalMode.Never,
            onInvoke: () => executed = true);

        await agent.RunAsync("please write", await agent.CreateSessionAsync());

        executed.Should().BeTrue("without the marker the framework has nothing to gate on");
    }

    /// <summary>
    /// 标记不得重复包裹：已带标记的工具再包一层会改变绑定语义。
    /// </summary>
    [Fact]
    public void Apply_AlreadyMarkedTool_IsLeftUntouched()
    {
        var metadata = CreateMetadata(DangerousToolName, ToolApprovalMode.Always);
        var function = CreateFunction(DangerousToolName, () => { });
        var alreadyMarked = new ApprovalRequiredAIFunction(function);

        var result = ToolApprovalMarker.Apply([alreadyMarked], metadata);

        result.Should().ContainSingle().Which.Should().BeSameAs(alreadyMarked);
    }

    /// <summary>
    /// 无需审批的工具（<see cref="ToolApprovalMode.Never"/>）不得被标记，
    /// 否则只读工具也会被要求人工确认。
    /// </summary>
    [Fact]
    public void Apply_NeverModeTool_IsNotMarked()
    {
        var metadata = CreateMetadata(DangerousToolName, ToolApprovalMode.Never);
        var function = CreateFunction(DangerousToolName, () => { });

        var result = ToolApprovalMarker.Apply([function], metadata);

        result.Should().ContainSingle();
        result![0].Should().BeSameAs(function);
        ((AIFunction)result[0]).GetService<ApprovalRequiredAIFunction>().Should().BeNull();
    }

    /// <summary>
    /// <see cref="ToolApprovalMode.Conditional"/> 与 <c>Always</c> 一样需要边界：
    /// 是否需要人工确认由运行时权限决策决定，但协议边界必须已经存在，否则该决策无处生效。
    /// </summary>
    [Fact]
    public void Apply_ConditionalModeTool_IsMarked()
    {
        var metadata = CreateMetadata(DangerousToolName, ToolApprovalMode.Conditional);
        var function = CreateFunction(DangerousToolName, () => { });

        var result = ToolApprovalMarker.Apply([function], metadata);

        var markedFunction = (AIFunction)result![0];
        markedFunction.GetService<ApprovalRequiredAIFunction>().Should().NotBeNull(
            "Conditional 与 Always 一样需要协议边界标记");
        markedFunction.Name.Should().Be(DangerousToolName,
            "MAF 审批规则按工具名匹配，包装不得改变 Name");
    }

    /// <summary>没有元数据来源时保持原样，不得凭空标记（未知工具按风险拒绝，而不是按标记）。</summary>
    [Fact]
    public void Apply_NullMetadata_ReturnsToolsUnchanged()
    {
        var function = CreateFunction(DangerousToolName, () => { });

        ToolApprovalMarker.Apply([function], metadata: null).Should().ContainSingle().Which.Should().BeSameAs(function);
    }

    // Helpers

    private static ToolMetadataRegistry CreateMetadata(string toolName, ToolApprovalMode mode)
    {
        var registry = new ToolMetadataRegistry();
        registry.Register(new ToolMetadata
        {
            Name = toolName,
            Risk = ToolRisk.Destructive,
            ApprovalMode = mode,
        });
        return registry;
    }

    private static AIFunction CreateFunction(string name, Action onInvoke) =>
        AIFunctionFactory.Create(() =>
        {
            onInvoke();
            return "done";
        }, name: name);

    /// <summary>
    /// Builds a real <see cref="HarnessAgent"/> over a scripted model that always asks for
    /// <paramref name="toolName"/>, then asserts on the framework's observable behavior.
    /// </summary>
    private static (AIAgent Agent, ScriptedChatClient Client) CreateAgent(
        string toolName,
        ToolApprovalMode approvalMode,
        Action onInvoke)
    {
        var metadata = CreateMetadata(toolName, approvalMode);
        var function = CreateFunction(toolName, onInvoke);
        var tools = ToolApprovalMarker.Apply([function], metadata)!;

        var client = new ScriptedChatClient(toolName);

        var options = new HarnessAgentOptions
        {
            Name = "approval-test",
            ChatOptions = new ChatOptions { Tools = tools, ToolMode = ChatToolMode.Auto },
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions { AutoApprovalRules = [] },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        return (new HarnessAgent(client, options), client);
    }

    /// <summary>
    /// Emits one tool call for the configured tool on the first request, then a plain answer.
    /// Mirrors what a real model does without needing a network call.
    /// </summary>
    private sealed class ScriptedChatClient(string toolName) : IChatClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", toolName, new Dictionary<string, object?>())])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "finished")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages[0].Contents);
        }
    }
}
