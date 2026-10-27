#pragma warning disable MAAI001 // MAF 实验性 API（HarnessAgentOptions / 截断压缩策略）：升级 MAF 时按升级复查清单回归
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace OneCode.Tests;

/// <summary>
/// 会话序列化 → 新管道恢复后，压缩是否仍作用于「恢复后的第一次模型请求」。
/// 受控 Build 的纠偏轮正是这条路径：同一会话换新管道再跑一轮。
/// </summary>
public sealed class RestoredSessionCompactionTests
{
    private const int BigResultChars = 20_000;
    private const int ToolRounds = 8;

    /// <summary>每次服务调用记录请求体字符数；run 1 连续调用大结果工具，之后回文本收尾。</summary>
    private sealed class ScriptedChatClient(int toolRounds) : IChatClient
    {
        public List<int> RequestChars { get; } = [];

        private int _toolCallsIssued;

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            RequestChars.Add(messages.Sum(MessageChars));
            AIContent content = _toolCallsIssued < toolRounds
                ? new FunctionCallContent($"call{++_toolCallsIssued}", "big", new Dictionary<string, object?> { ["n"] = _toolCallsIssued })
                : new TextContent("done");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [content])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        private static int MessageChars(ChatMessage message) => message.Contents.Sum(content => content switch
        {
            TextContent text => text.Text.Length,
            FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
            _ => 0,
        });
    }

    private static HarnessAgent CreateAgent(IChatClient chatClient) =>
        new(chatClient, new HarnessAgentOptions
        {
            Name = "restore-probe",
            ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create((int n) => new string((char)('a' + (n % 26)), BigResultChars), name: "big")],
            },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            // 8 个 20K 字符结果（约 40K token）远超 15K token 阈值，截断必然触发。
            // 用截断而非折叠：折叠策略保护当前 run 内的工具组，无法在单 run 内产生可观测差异。
            CompactionStrategy = new TruncationCompactionStrategy(
                CompactionTriggers.TokensExceed(15_000), minimumPreservedGroups: 2),
            DisableToolAutoApproval = true,
            DisableOpenTelemetry = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableAgentSkillsProvider = true,
            DisableFileMemory = true,
            DisableWebSearch = true,
        });

    [Fact]
    public async Task RestoredSession_FirstRequestOfNextRun_IsAlreadyCompacted()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstClient = new ScriptedChatClient(ToolRounds);
        var firstAgent = CreateAgent(firstClient);
        var firstSession = await firstAgent.CreateSessionAsync(ct);
        await foreach (var _ in firstAgent.RunStreamingAsync("collect", firstSession, cancellationToken: ct)) { }

        var snapshot = await firstAgent.SerializeSessionAsync(firstSession, null, ct);

        var secondClient = new ScriptedChatClient(toolRounds: 0);
        var secondAgent = CreateAgent(secondClient);
        var restored = await secondAgent.DeserializeSessionAsync(snapshot, null, ct);
        await foreach (var _ in secondAgent.RunStreamingAsync("finish up", restored, cancellationToken: ct)) { }

        var rawToolOutput = ToolRounds * BigResultChars;
        var lastRequestOfFirstRun = firstClient.RequestChars[^1];
        var firstRequestAfterRestore = secondClient.RequestChars[0];

        lastRequestOfFirstRun.Should().BeLessThan(rawToolOutput / 2, "run 1 compacts old tool results in flight");
        firstRequestAfterRestore.Should().BeLessThan(rawToolOutput / 2,
            "the restored session must not replay the raw tool output uncompacted on its first request");
    }
}
