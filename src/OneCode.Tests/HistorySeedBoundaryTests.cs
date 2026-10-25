using OneCode.App.Services.Agent;
using OneCode.App.Services.Compact;

namespace OneCode.Tests;

/// <summary>
/// 阶段播种边界的记录/读取契约：新执行世代重记、同世代不回移、epoch 漂移失效回退全量。
/// 写入端与读取端都在 <see cref="HistorySeedBoundary"/> 内，本套件走真实元数据往返。
/// </summary>
public sealed class HistorySeedBoundaryTests
{
    [Fact]
    public void RecordForExecution_ThenReadValidated_RoundTripsBoundary()
    {
        var conversation = CreateConversation();

        HistorySeedBoundary.RecordForExecution(conversation, "req-1", messageCount: 7);

        HistorySeedBoundary.ReadValidated(conversation).Should().Be(
            new HistorySeedBoundary("req-1", MafSessionInvalidator.GetHistoryEpoch(conversation), 7),
            "the recorded boundary must survive the metadata round-trip unchanged");
    }

    [Fact]
    public void RecordForExecution_SameExecutionRetry_KeepsOriginalBoundary()
    {
        var conversation = CreateConversation();
        HistorySeedBoundary.RecordForExecution(conversation, "req-1", messageCount: 5);

        // 启动重试：同一执行世代，转录已增长（上次尝试的消息已落盘）——不得回移边界，
        // 否则重试轮播种丢失上次尝试的同阶段消息，模型看不到自己已完成的步骤。
        HistorySeedBoundary.RecordForExecution(conversation, "req-1", messageCount: 12);

        HistorySeedBoundary.ReadValidated(conversation).Should().Be(
            new HistorySeedBoundary("req-1", 0, 5),
            "a start retry of the same execution generation must keep the original boundary");
    }

    [Fact]
    public void RecordForExecution_NewExecutionGeneration_RecordsFreshBoundary()
    {
        var conversation = CreateConversation();
        HistorySeedBoundary.RecordForExecution(conversation, "req-1", messageCount: 5);

        // 同会话内第二次批准的计划：新世代以当前转录位置重记。沿用旧边界会让第二次
        // 执行轮继续看到两次计划阶段的全部历史（含 SubmitPlan 调用），fresh-context 契约被破坏。
        HistorySeedBoundary.RecordForExecution(conversation, "req-2", messageCount: 20);

        HistorySeedBoundary.ReadValidated(conversation).Should().Be(
            new HistorySeedBoundary("req-2", 0, 20),
            "a newly approved plan generation must record its own boundary at the current transcript position");
    }

    [Fact]
    public void ReadValidated_EpochDriftedAfterRecording_ReturnsNull()
    {
        var conversation = CreateConversation();
        HistorySeedBoundary.RecordForExecution(conversation, "req-1", messageCount: 5);

        // 边界记录后发生 compact/clear 等结构性变更：消息索引不再指向同一批消息
        conversation.Metadata[MafSessionInvalidator.HistoryEpochKey] =
            MafSessionInvalidator.GetHistoryEpoch(conversation) + 1;

        HistorySeedBoundary.ReadValidated(conversation).Should().BeNull(
            "a boundary recorded before a structural history change no longer identifies the same " +
            "messages; the run must fall back to seeding the full transcript");
    }

    [Fact]
    public void ReadValidated_CorruptedMetadataValue_ReturnsNull()
    {
        var conversation = CreateConversation();
        conversation.Metadata[HistorySeedBoundary.MetadataKey] = "{ not json";

        HistorySeedBoundary.ReadValidated(conversation).Should().BeNull(
            "a corrupted boundary must behave like a missing one instead of failing the controlled run");
    }

    [Fact]
    public void ReadValidatedForHandoff_SameExecutionRequestId_ReturnsNullSoOriginalRequestIsNotTruncated()
    {
        var conversation = CreateConversation();
        HistorySeedBoundary.RecordForExecution(conversation, "req-1", messageCount: 5);

        HistorySeedBoundary.ReadValidatedForHandoff(conversation, "req-1").Should().BeNull(
            "re-approving a revised revision of the same plan must not use its own first execution boundary as windowStart");
        HistorySeedBoundary.ReadValidatedForHandoff(conversation, "req-2").Should().Be(
            new HistorySeedBoundary("req-1", 0, 5),
            "a new plan workflow in the same session should advance windowStart past the previous plan's boundary");
    }

    private static Conversation CreateConversation() => new()
    {
        Id = SessionId.NewId(),
        Name = "boundary-test",
        WorkingDirectory = Environment.CurrentDirectory,
        Model = "test-model",
    };
}
