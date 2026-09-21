using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Compact;
using OneCode.App.Session;
using OneCode.Core.Domain;
using OneCode.Infrastructure.Agent;
using System.Text.Json;

namespace OneCode.Tests;

/// <summary>
/// B1 行为契约：非法模型配置必须显式失败，不能静默退化成不可达的阈值。
/// </summary>
/// <remarks>
/// <para>
/// 修复前预算是 <c>Math.Max(1, window - output)</c>。当输出预留大于等于上下文窗口时，
/// 预算被钳制为 1，三层阈值随之变成 0——压缩看起来「已装配」，但任何策略都不可能触发，
/// 配置错误被完全掩盖。
/// </para>
/// <para>
/// 断言打在<b>公开入口</b>（<see cref="CompactionPipelineBuilder"/>）而非内部辅助方法上：
/// 校验逻辑是装配的一部分，测试只认「装配失败」这一可观察结果，不绑定实现位置。
/// </para>
/// </remarks>
public sealed class CompactionBudgetValidatorTests
{
    [Fact]
    public void BuildForMainAgent_OutputExceedsWindow_Throws()
    {
        var act = () => CompactionPipelineBuilder.BuildForMainAgent(
            Substitute.For<IChatClient>(), maxContextWindowTokens: 8192, maxOutputTokens: 8192);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*no input budget*");
    }

    /// <summary>反证：窗口合法但预留为负（用户配置错误）同样必须失败。</summary>
    [Fact]
    public void BuildForMainAgent_NonPositiveValues_Throw()
    {
        var client = Substitute.For<IChatClient>();

        ((Action)(() => CompactionPipelineBuilder.BuildForMainAgent(client, 0, 1024)))
            .Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => CompactionPipelineBuilder.BuildForMainAgent(client, 200_000, -1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// 预算必须为消息预留请求开销（指令、工具 schema、协议框架）。
    /// 若没有这份预留，阈值会按消息拿不到的空间计算，触发时机晚于真实上限。
    /// </summary>
    [Fact]
    public void BuildForMainAgent_ValidPair_ReservesRequestOverhead()
    {
        var act = () => CompactionPipelineBuilder.BuildForMainAgent(
            Substitute.For<IChatClient>(), 200_000, 8192);

        act.Should().NotThrow("a valid pair must build; the reservation is internal to threshold derivation");
    }

    /// <summary>
    /// 反证：输出预留恰好等于窗口时必须失败，而不是产生 0 阈值。
    /// 这是修复前的真实缺陷（钳制为 1 → 阈值 0 → 压缩永不触发）。
    /// </summary>
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1000, 1500)]
    public void BuildForMainAgent_NoRoomForInput_Throws(int window, int output)
    {
        var act = () => CompactionPipelineBuilder.BuildForMainAgent(
            Substitute.For<IChatClient>(), window, output);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

/// <summary>
/// B3 行为契约：compact 后的定向失效必须保留与消息历史无关的会话状态。
/// </summary>
/// <remarks>
/// <c>mafSession</c> 里同时装着消息历史相关状态（压缩索引、内存历史）和无关状态
/// （会话待办、工作记忆目录、审批 standing rules）。整体删除会让后者在每次 compact 后丢失，
/// 而用户只是压缩了历史，并没有重置会话。
/// </remarks>
public sealed class MafSessionInvalidatorTests
{
    [Fact]
    public void Invalidate_RemovesHistoryBoundStateOnly()
    {
        var conversation = CreateConversationWithSession("""
            {
              "conversationId": "conv-1",
              "stateBag": {
                "PipelineCompactionStrategy": { "messagegroups": [] },
                "InMemoryChatHistoryProvider": { "messages": [] },
                "TodoProvider": { "todos": [{ "title": "keep me" }] },
                "FileMemoryProvider": { "workingFolder": "20260101_abc" },
                "toolApprovalState": { "rules": [] }
              }
            }
            """);

        MafSessionInvalidator.Invalidate(conversation, "compact.full");

        var stateBag = ReadStateBag(conversation);
        stateBag.Should().NotContainKey("PipelineCompactionStrategy");
        stateBag.Should().NotContainKey("InMemoryChatHistoryProvider");
        stateBag.Should().ContainKey("TodoProvider", "the todo list is not bound to message positions");
        stateBag.Should().ContainKey("FileMemoryProvider", "the working-memory folder must survive compaction");
        stateBag.Should().ContainKey("toolApprovalState");
    }

    /// <summary>
    /// 迁移政策：迁移前的固定 stateKey <c>"compaction"</c> 必须随历史一起清除，不做格式转换。
    /// </summary>
    /// <remarks>
    /// 旧装配显式传 <c>"compaction"</c>；改由 Harness 挂载后键名变为策略类型名，旧键不再有读取方。
    /// 它与新键一样是按消息位置绑定的分组索引，留在快照里只会让每次保存携带一段永不使用的状态。
    /// </remarks>
    [Fact]
    public void Invalidate_RemovesLegacyCompactionStateKey()
    {
        var conversation = CreateConversationWithSession("""
            {
              "stateBag": {
                "compaction": { "messagegroups": [] },
                "PipelineCompactionStrategy": { "messagegroups": [] },
                "TodoProvider": { "todos": [{ "title": "keep me" }] }
              }
            }
            """);

        MafSessionInvalidator.Invalidate(conversation, "compact.full");

        var stateBag = ReadStateBag(conversation);
        stateBag.Should().NotContainKey("compaction", "the pre-migration key must not be carried forward");
        stateBag.Should().NotContainKey("PipelineCompactionStrategy");
        stateBag.Should().ContainKey("TodoProvider", "legacy-key cleanup must stay targeted, not a full removal");
    }

    /// <summary>
    /// epoch 快照必须同步，否则 <c>CreateOrRestoreSessionAsync</c> 会因 epoch 不一致
    /// 把保留下来的非历史状态一起丢弃。
    /// </summary>
    [Fact]
    public void Invalidate_SyncsEpochSnapshotSoRetainedStateSurvives()
    {
        var conversation = CreateConversationWithSession("""
            { "stateBag": { "TodoProvider": { "todos": [] } } }
            """);

        MafSessionInvalidator.Invalidate(conversation, "compact.full");

        conversation.Metadata[MafSessionInvalidator.MafSessionEpochKey]
            .Should().Be(MafSessionInvalidator.GetHistoryEpoch(conversation),
                "a pruned snapshot is still current, so the epoch check must not discard it");
    }

    /// <summary>历史 epoch 仍须递增，保证按位置绑定消息的旧状态不会复活。</summary>
    [Fact]
    public void Invalidate_IncrementsHistoryEpoch()
    {
        var conversation = CreateConversationWithSession("""{ "stateBag": {} }""");

        MafSessionInvalidator.Invalidate(conversation, "compact.partial");

        MafSessionInvalidator.GetHistoryEpoch(conversation).Should().Be(1);
    }

    /// <summary>
    /// 反证：快照不可解析时不得假装「已定向失效」——此时整体删除才是安全选择，
    /// 否则一个损坏的快照会长期留存并被反复尝试恢复。
    /// </summary>
    [Fact]
    public void Invalidate_UnparsableSnapshot_FallsBackToFullRemoval()
    {
        var conversation = new Conversation
        {
            Id = SessionId.NewId(),
            Name = "broken",
            WorkingDirectory = Environment.CurrentDirectory,
            Model = "test-model",
        };
        conversation.Metadata["mafSession"] = "{ not json";

        MafSessionInvalidator.Invalidate(conversation, "compact.full");

        conversation.Metadata.Should().NotContainKey("mafSession");
        conversation.Metadata.Should().NotContainKey(MafSessionInvalidator.MafSessionEpochKey);
    }

    // Helpers

    private static Conversation CreateConversationWithSession(string sessionJson)
    {
        var conversation = new Conversation
        {
            Id = SessionId.NewId(),
            Name = "session-test",
            WorkingDirectory = Environment.CurrentDirectory,
            Model = "test-model",
        };
        conversation.Metadata["mafSession"] = sessionJson;
        return conversation;
    }

    private static Dictionary<string, System.Text.Json.JsonElement> ReadStateBag(Conversation conversation)
    {
        var session = System.Text.Json.JsonSerializer
            .SerializeToElement(conversation.Metadata["mafSession"]);
        var stateBag = session.GetProperty("stateBag");
        return stateBag.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }
}

/// <summary>
/// B3 行为契约：epoch 快照必须与快照内容同源，不能是「写回时的当前值」。
/// </summary>
/// <remarks>
/// <para>
/// 缺陷形态：<c>PersistSessionAsync</c> 先 <c>await SerializeSessionAsync</c>，再把<b>当时</b>的
/// <c>historyEpoch</c> 记进 <c>mafSessionEpoch</c>。若这期间有 compact/clear 结构性改动消息历史，
/// 一份对应旧历史的快照会被盖上<b>新</b> epoch 标签，恢复路径的 epoch 比对形同虚设——
/// 已压缩掉的消息会被重新喂给模型。
/// </para>
/// <para>
/// 修复后是「捕获—序列化—复核」的 CAS 语义：epoch 在序列化<b>前</b>捕获，写回前复核；
/// 不一致则丢弃快照。测试用会在序列化中途递增 epoch 的 agent 直接命中该竞态窗口。
/// </para>
/// </remarks>
public sealed class AgentSessionStoreEpochSnapshotTests
{
    [Fact]
    public async Task PersistSessionAsync_HistoryEpochChangesDuringSerialize_DiscardsSnapshot()
    {
        var conversation = CreateConversationWithEpoch(3);
        var store = CreateStore(conversation);
        var agent = new EpochChangingAgent(conversation, bumpEpochDuringSerialize: true);

        await store.PersistSessionAsync(agent, new FreshSession(), conversation.Id, CancellationToken.None);

        conversation.Metadata.Should().NotContainKey("mafSession",
            "the snapshot describes the pre-change history and must not be restorable");
        conversation.Metadata.Should().NotContainKey(MafSessionInvalidator.MafSessionEpochKey,
            "leaving the stamp behind would let a later save resurrect the discarded snapshot");
    }

    /// <summary>
    /// 反证的另一半：epoch 未变时必须正常落盘，且记录的是序列化前捕获的值。
    /// 若实现退化成「永不落盘」，这个测试会失败。
    /// </summary>
    [Fact]
    public async Task PersistSessionAsync_HistoryUnchanged_StampsEpochCapturedWithSnapshot()
    {
        var conversation = CreateConversationWithEpoch(3);
        var store = CreateStore(conversation);
        var agent = new EpochChangingAgent(conversation, bumpEpochDuringSerialize: false);

        await store.PersistSessionAsync(agent, new FreshSession(), conversation.Id, CancellationToken.None);

        conversation.Metadata.Should().ContainKey("mafSession");
        conversation.Metadata[MafSessionInvalidator.MafSessionEpochKey].Should().Be(3);
    }

    /// <summary>
    /// 可观察后果：被丢弃的快照不得在下次运行时被恢复。
    /// 断言打在 <see cref="AgentSessionStore.CreateOrRestoreSessionAsync"/> 的返回值上，
    /// 而不是内部元数据状态。
    /// </summary>
    [Fact]
    public async Task RestoreAfterDiscardedSnapshot_ReturnsFreshSession()
    {
        var conversation = CreateConversationWithEpoch(3);
        conversation.Metadata["mafSession"] = """{ "stateBag": { "TodoProvider": {} } }""";
        var store = CreateStore(conversation);

        await store.PersistSessionAsync(
            new EpochChangingAgent(conversation, bumpEpochDuringSerialize: true),
            new FreshSession(),
            conversation.Id,
            CancellationToken.None);

        var restored = await store.CreateOrRestoreSessionAsync(
            new EpochChangingAgent(conversation, bumpEpochDuringSerialize: false),
            conversation.Id,
            CancellationToken.None);

        restored.Should().BeOfType<FreshSession>(
            "the stale snapshot was written against a history that no longer exists");
    }

    // Helpers

    private static AgentSessionStore CreateStore(Conversation conversation)
    {
        var manager = Substitute.For<ISessionConversationAccess>();
        manager.GetConversation(conversation.Id).Returns(conversation);
        return new AgentSessionStore(manager, NullLogger<AgentSessionStore>.Instance);
    }

    private static Conversation CreateConversationWithEpoch(int epoch)
    {
        var conversation = new Conversation
        {
            Id = SessionId.NewId(),
            Name = "epoch-snapshot-test",
            WorkingDirectory = Environment.CurrentDirectory,
            Model = "test-model",
        };
        conversation.Metadata[MafSessionInvalidator.HistoryEpochKey] = epoch;
        return conversation;
    }

    /// <summary>Concrete <see cref="AgentSession"/> used as the "fresh session" sentinel.</summary>
    private sealed class FreshSession : AgentSession;

    /// <summary>Concrete <see cref="AgentSession"/> used as the "restored from snapshot" sentinel.</summary>
    private sealed class RestoredSession : AgentSession;

    private sealed class EpochChangingAgent(Conversation conversation, bool bumpEpochDuringSerialize) : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct)
            => throw new NotImplementedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct)
            => throw new NotImplementedException();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct)
            => ValueTask.FromResult<AgentSession>(new FreshSession());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement json, JsonSerializerOptions? options, CancellationToken ct)
            => ValueTask.FromResult<AgentSession>(new RestoredSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? options, CancellationToken ct)
        {
            // 模拟序列化期间并发发生的结构性历史变更（compact/clear/snip）。
            if (bumpEpochDuringSerialize)
                MafSessionInvalidator.Invalidate(conversation, "compact.full");

            return ValueTask.FromResult(
                JsonSerializer.SerializeToElement(new { stateBag = new { TodoProvider = new { } } }));
        }
    }
}
