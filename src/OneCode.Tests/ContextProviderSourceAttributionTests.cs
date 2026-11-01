#pragma warning disable MAAI001 // MAF 实验性 API（HarnessAgentOptions / AIContextProvider）：升级 MAF 时按升级复查清单回归
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Agent;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// 契约探针：MAF 给 provider 注入消息盖的来源标记形态。
///
/// 分场景 token 统计（per-contributor 分桶）依赖两条契约：
///   1. 注入消息的 sourceType 恒为 <see cref="AgentRequestMessageSourceType.AIContextProvider"/>；
///   2. sourceId = <b>provider 类型全名</b>（<c>GetAgentRequestMessageSourceId</c> 读取），
///      且不同 provider 可区分——它是分桶键。
/// MAF 升级若改变取值形态，此测试大声失败，分桶逻辑同步修正而不是静默错桶。
///
/// 已知边界：经 <c>AIContext.Instructions</c> 贡献的 provider（skills / design / shell env）
/// 由 MAF 合并进 ChatOptions.Instructions，无逐 provider 标记——这部分归入 SystemPrompt 桶，
/// 不参与消息级分桶。
/// </summary>
public sealed class ContextProviderSourceAttributionTests
{
    private sealed class BucketAProvider : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            AIContextProvider.InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AIContext { Messages = [new ChatMessage(ChatRole.User, "BUCKET_A_MESSAGE")] });
    }

    [Fact]
    public async Task ProviderInjectedMessages_CarrySourceTypeAndProviderTypeId()
    {
        var client = new CapturingChatClient();
        var options = new HarnessAgentOptions
        {
            AIContextProviders = [new BucketAProvider()],
            ChatOptions = new ChatOptions { Instructions = "BASE_INSTRUCTIONS" },
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await agent.RunAsync("hello", session, cancellationToken: TestContext.Current.CancellationToken);

        var injected = client.LastMessages
            .Where(m => m.Text == "BUCKET_A_MESSAGE")
            .ToList();
        injected.Should().NotBeEmpty("provider 注入的消息必须抵达 ChatClient 层（携带来源标记）");
        injected[0].GetAgentRequestMessageSourceType().Should().Be(AgentRequestMessageSourceType.AIContextProvider,
            "sourceType 标记是持久/瞬态判定（缓存断点、回写过滤）与分桶的公共前提");
        injected[0].GetAgentRequestMessageSourceId().Should().Be(typeof(BucketAProvider).FullName,
            "sourceId = provider 类型全名——分场景统计的桶键，MAF 升级改变形态时须同步分桶逻辑");

        // harness 默认注册的 TodoProvider 也注入了消息：不同 provider 的 sourceId 必须可区分
        var providerSourceIds = client.LastMessages
            .Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider)
            .Select(m => m.GetAgentRequestMessageSourceId())
            .ToHashSet();
        providerSourceIds.Count.Should().BeGreaterThan(1,
            "分桶前提：不同 provider 的 sourceId 必须互不相同");
    }
}
