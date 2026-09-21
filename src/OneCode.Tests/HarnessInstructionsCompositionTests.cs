using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Agent;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// §4.6 行为契约：MAF 是通用片段与主体的唯一合成方。
/// </summary>
/// <remarks>
/// 修复前产品手工拼接后整体作为 agent 指令下发。现在两段分开传入，MAF 负责顺序
/// （<c>HarnessInstructions</c> 在前，<c>ChatOptions.Instructions</c> 在后，中间两个换行）。
/// 这些用例捕获模型实际收到的最终指令，而不是断言两个字符串字段。
/// </remarks>
public sealed class HarnessInstructionsCompositionTests
{
    [Fact]
    public async Task FinalInstructions_PlaceHarnessFragmentBeforeAgentBodyExactlyOnce()
    {
        var client = new CapturingChatClient();

        var options = new HarnessAgentOptions
        {
            HarnessInstructions = "HARNESS_SENTINEL",
            ChatOptions = new ChatOptions { Instructions = "AGENT_BODY_SENTINEL" },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        await agent.RunAsync("hello", await agent.CreateSessionAsync());

        var instructions = client.LastInstructions;
        instructions.Should().NotBeNull();

        var harnessIndex = instructions!.IndexOf("HARNESS_SENTINEL", StringComparison.Ordinal);
        var bodyIndex = instructions.IndexOf("AGENT_BODY_SENTINEL", StringComparison.Ordinal);

        harnessIndex.Should().BeGreaterThanOrEqualTo(0);
        bodyIndex.Should().BeGreaterThanOrEqualTo(0);
        harnessIndex.Should().BeLessThan(bodyIndex, "the harness fragment comes first");
        instructions.Split("HARNESS_SENTINEL", StringSplitOptions.None).Length.Should().Be(2,
            "the fragment must appear exactly once — a pre-joined string would duplicate it");
    }

    /// <summary>
    /// 反证：公共 opt-out 不得覆盖调用方的指令配置。
    /// </summary>
    [Fact]
    public async Task ProductOptOuts_DoNotReplaceCallerInstructions()
    {
        var client = new CapturingChatClient();

        var options = new HarnessAgentOptions
        {
            HarnessInstructions = "CALLER_HARNESS",
            ChatOptions = new ChatOptions { Instructions = "CALLER_BODY" },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        await agent.RunAsync("hello", await agent.CreateSessionAsync());

        client.LastInstructions.Should().Contain("CALLER_HARNESS");
        client.LastInstructions.Should().Contain("CALLER_BODY");
    }

    /// <summary>
    /// 反证：自包含提示词的路径必须显式抑制框架默认文案。
    /// </summary>
    /// <remarks>
    /// AutoDream 与 Goal 子目标各自携带完整提示（含自己的注入防御），历史上从未与共享片段合成，
    /// 而公共 opt-out 曾把整个产品的默认文案压掉。<c>HarnessInstructions</c> 改为 <c>null</c>
    /// 会让 MAF 把通用指令加到这些专用提示之上——那不是本次迁移要改的文案。
    /// </remarks>
    [Fact]
    public async Task SelfContainedPath_SuppressesFrameworkDefaults()
    {
        var client = new CapturingChatClient();

        var options = new HarnessAgentOptions
        {
            HarnessInstructions = OneCodeHarnessDefaults.SuppressFrameworkDefaults,
            ChatOptions = new ChatOptions { ModelId = "fast-model" },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        await agent.RunAsync("consolidate", await agent.CreateSessionAsync());

        client.LastInstructions.Should().NotContain(HarnessAgent.DefaultInstructions,
            "a self-contained prompt must not gain MAF's generic instructions as a side effect");
        client.LastInstructions.Should().NotContain("General guidelines");
    }

    /// <summary>
    /// 空串是有意的抑制：调用方传 <c>""</c> 时不得回退到 MAF 默认文案，
    /// 否则「有意不说话」会变成「自动说话」。
    /// </summary>
    [Fact]
    public async Task EmptyHarnessInstructions_SuppressFrameworkDefaults()
    {
        var client = new CapturingChatClient();

        var options = new HarnessAgentOptions
        {
            HarnessInstructions = "",
            ChatOptions = new ChatOptions { Instructions = "ONLY_BODY" },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        await agent.RunAsync("hello", await agent.CreateSessionAsync());

        client.LastInstructions.Should().Contain("ONLY_BODY");
        client.LastInstructions.Should().NotContain(HarnessAgent.DefaultInstructions,
            "an explicit empty string suppresses the framework defaults");
    }
}
