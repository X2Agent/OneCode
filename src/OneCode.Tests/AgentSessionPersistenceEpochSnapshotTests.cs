using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Compact;
using OneCode.App.Session;
using OneCode.Core.Domain;
using System.Text.Json;

namespace OneCode.Tests;

/// <summary>
/// 行为契约：epoch 快照必须与快照内容同源，不能是「写回时的当前值」。
/// </summary>
/// <remarks>
/// <para>
/// 若 <c>PersistSessionAsync</c> 在序列化之后才记录 epoch，那么序列化期间发生 compact/clear
/// 时，一份对应旧历史的快照会被盖上<b>新</b> epoch 标签，恢复路径的 epoch 比对形同虚设——
/// 已压缩掉的消息会被重新喂给模型。
/// </para>
/// <para>
/// 实现采用「捕获—序列化—复核」的 CAS 语义：epoch 在序列化<b>前</b>捕获，写回前复核；
/// 不一致则丢弃快照。测试用会在序列化中途递增 epoch 的 agent 直接命中该竞态窗口。
/// </para>
/// </remarks>
public sealed class AgentSessionPersistenceEpochSnapshotTests
{
    [Fact]
    public async Task PersistSessionAsync_HistoryEpochChangesDuringSerialize_DiscardsSnapshot()
    {
        var conversation = CreateConversationWithEpoch(3);
        var store = CreateStore(conversation);
        var agent = new EpochChangingAgent(conversation, bumpEpochDuringSerialize: true);

        await store.PersistSessionAsync(agent, new FreshSession(), conversation.Id, TestContext.Current.CancellationToken);

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

        await store.PersistSessionAsync(agent, new FreshSession(), conversation.Id, TestContext.Current.CancellationToken);

        conversation.Metadata.Should().ContainKey("mafSession");
        conversation.Metadata[MafSessionInvalidator.MafSessionEpochKey].Should().Be(3);
    }

    /// <summary>
    /// 可观察后果：被丢弃的快照不得在下次运行时被恢复。
    /// 断言打在 <see cref="AgentSessionPersistence.CreateOrRestoreSessionAsync"/> 的返回值上，
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
            TestContext.Current.CancellationToken);

        var restored = await store.CreateOrRestoreSessionAsync(
            new EpochChangingAgent(conversation, bumpEpochDuringSerialize: false),
            conversation.Id,
            TestContext.Current.CancellationToken);

        restored.Should().BeOfType<FreshSession>(
            "the stale snapshot was written against a history that no longer exists");
    }

    private static AgentSessionPersistence CreateStore(Conversation conversation)
    {
        var manager = Substitute.For<ISessionConversationAccess>();
        manager.GetConversation(conversation.Id).Returns(conversation);
        return new AgentSessionPersistence(manager, null!, NullLogger<AgentSessionPersistence>.Instance);
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
