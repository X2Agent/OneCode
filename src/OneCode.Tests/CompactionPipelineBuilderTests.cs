#pragma warning disable MAAI001 // MAF 实验性 API（压缩消息索引）：升级 MAF 时按升级复查清单回归
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// 只读调研档的绝对阈值上限：1M 窗口上比例阈值会放任调研子代理把上下文涨到数十万 token，
/// 每轮请求都重复付费这份前缀。
/// </summary>
public sealed class CompactionPipelineBuilderTests
{
    private const int LargeWindow = 1_000_000;
    private const int OutputReservation = 8_192;

    // 20 轮 × 12K 字符工具结果 ≈ 60K+ token：超过调研档 40K 的折叠上限，远低于 1M 窗口的比例阈值。
    private const int ResearchToolEvictionCap = 40_000;
    private const int Rounds = 20;
    private const int ToolResultChars = 12_000;

    [Fact]
    public async Task BuildForWorkerAgent_ReadOnlyResearchOnLargeWindow_FoldsOldToolResultsEarly()
    {
        var strategy = CompactionPipelineBuilder.BuildForWorkerAgent(
            Substitute.For<IChatClient>(), LargeWindow, OutputReservation, readOnlyResearch: true);
        var index = BuildToolHeavyIndex();

        var changed = await strategy.CompactAsync(index, NullLogger.Instance, TestContext.Current.CancellationToken);

        changed.Should().BeTrue("调研档在 1M 窗口上也必须按绝对上限折叠旧工具结果");
        index.IncludedTokenCount.Should().BeLessThanOrEqualTo(ResearchToolEvictionCap, "折叠到调研档上限以内才停止");
    }

    [Fact]
    public async Task BuildForWorkerAgent_GeneralWorkerOnLargeWindow_KeepsRatioThreshold()
    {
        // 反证：上限只属于调研档——通用 worker 要在大窗口里完整保留编码上下文，同样体量不得被折叠。
        var strategy = CompactionPipelineBuilder.BuildForWorkerAgent(
            Substitute.For<IChatClient>(), LargeWindow, OutputReservation, readOnlyResearch: false);
        var index = BuildToolHeavyIndex();
        var tokensBefore = index.IncludedTokenCount;

        var changed = await strategy.CompactAsync(index, NullLogger.Instance, TestContext.Current.CancellationToken);

        changed.Should().BeFalse();
        index.IncludedTokenCount.Should().Be(tokensBefore);
    }

    private static CompactionMessageIndex BuildToolHeavyIndex()
    {
        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, "research the topic")];
        for (var i = 0; i < Rounds; i++)
        {
            var callId = $"call-{i}";
            messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, "WebFetch")]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, new string('r', ToolResultChars))]));
        }

        // 反射调库内工厂：Create 无公开构造，库 API 收窄前用反射构造被测对象——
        // 重命名会在此显式红，断言的是折叠语义而非成员可见性。
        var create = typeof(CompactionMessageIndex).GetMethod(
            "Create",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        return (CompactionMessageIndex)create.Invoke(null, [messages, null])!;
    }
}
