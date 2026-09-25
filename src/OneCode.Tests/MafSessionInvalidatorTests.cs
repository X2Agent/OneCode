using OneCode.App.Services.Compact;
using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// 行为契约：compact 后的定向失效必须保留与消息历史无关的会话状态。
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

    /// <summary>
    /// 框架写入的本地历史哨兵必须随历史一起剔除，否则 compact 之后的会话永久失去 in-pipeline 压缩。
    /// </summary>
    /// <remarks>
    /// <c>CompactionProvider</c> 把「<c>ConversationId</c> 非空」解读为「历史由远端服务托管」并整个跳过压缩
    /// （框架行为由 <c>HarnessCompactionActivationTests</c> 锁定）。哨兵序列化在会话根上、不在 <c>stateBag</c> 里，
    /// 因此只剪 <c>stateBag</c> 的失效方式会把它留下——刚剪掉的压缩索引就再也无法被重建。
    /// </remarks>
    [Fact]
    public void Invalidate_DropsLocalHistorySentinelConversationId()
    {
        var conversation = CreateConversationWithSession("""
            {
              "conversationId": "_agent_local_chat_history",
              "stateBag": { "TodoProvider": { "todos": [] } }
            }
            """);

        MafSessionInvalidator.Invalidate(conversation, "compact.full");

        HasRootProperty(conversation, "conversationId").Should().BeFalse(
            "the local-history sentinel permanently disables CompactionProvider");
        ReadStateBag(conversation).Should().ContainKey("TodoProvider",
            "removing the sentinel must not turn the targeted invalidation into a full removal");
    }

    /// <summary>
    /// 反证：真·远端 <c>ConversationId</c> 必须保留——剔除的目标是哨兵，不是整个属性。
    /// </summary>
    /// <remarks>
    /// 服务端托管历史的会话靠这个 id 续跑；无条件删除会把「历史由远端服务托管」的会话切断。
    /// 本用例与上一个用例配对，证明剔除是按哨兵值定向的。
    /// </remarks>
    [Fact]
    public void Invalidate_PreservesRealServiceConversationId()
    {
        var conversation = CreateConversationWithSession("""
            {
              "conversationId": "resp_remote_123",
              "stateBag": { "TodoProvider": { "todos": [] } }
            }
            """);

        MafSessionInvalidator.Invalidate(conversation, "compact.full");

        HasRootProperty(conversation, "conversationId").Should().BeTrue(
            "a real remote ConversationId carries continuity and must survive");
    }

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

    private static bool HasRootProperty(Conversation conversation, string name)
    {
        var session = System.Text.Json.JsonSerializer
            .SerializeToElement(conversation.Metadata["mafSession"]);
        return session.TryGetProperty(name, out _);
    }
}
