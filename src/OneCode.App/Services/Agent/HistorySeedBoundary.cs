using OneCode.App.Services.Compact;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Approved-plan 执行世代的历史播种边界：plan→build 切换时把 (执行世代, historyEpoch, 转录消息数)
/// 记入会话元数据，受控 Build run 的 <c>TranscriptChatHistoryProvider</c> 据此保留 Plan 阶段的只读检索结果、
/// 仅剥离边界之前的 <c>SubmitPlan</c> 工具调用对（跨阶段工具名无从回放），同时供 <c>PlanHandoffExtractor</c>
/// 定位当前规划窗口起点。转录本身不删不改。
/// </summary>
/// <remarks>
/// <para>
/// 边界只在新的执行世代（<see cref="ExecutionRequestId"/> 变化）首次启动时重记；同一世代的
/// 启动重试与恢复沿用首次边界、不回移——恢复 run 因此播种得到上次尝试的同阶段消息
/// （模型看得到自己已完成的步骤），而不是一份丢弃了执行进度的空白历史。
/// </para>
/// <para>
/// 边界记录后消息历史发生结构性变更（compact/clear/snip 递增 historyEpoch）时，消息索引不再
/// 指向同一批消息，边界失效——<see cref="ReadValidated"/> 返回 null，回退全量转录播种。
/// </para>
/// </remarks>
public sealed record HistorySeedBoundary(
    string ExecutionRequestId,
    int HistoryEpoch,
    int MessageIndex)
{
    /// <summary>conversation.Metadata 中的存储键。</summary>
    public const string MetadataKey = "approvedPlanHistorySeedBoundary";

    /// <summary>
    /// 记录当前执行世代的边界。同一世代已存在则不回移（启动重试/恢复沿用首次边界）；
    /// 新世代以当前转录位置为准重记——同会话内第二次批准的计划需要自己的边界，
    /// 沿用旧边界会让它的执行轮继续看到上一轮计划阶段的 SubmitPlan 调用。
    /// </summary>
    public static void RecordForExecution(Conversation conversation, string executionRequestId, int messageCount)
    {
        if (TryRead(conversation) is { } existing && existing.ExecutionRequestId == executionRequestId)
            return;

        conversation.Metadata[MetadataKey] = JsonSerializer.SerializeToElement(new HistorySeedBoundary(
            executionRequestId,
            MafSessionInvalidator.GetHistoryEpoch(conversation),
            messageCount));
    }

    /// <summary>读取并校验边界：无边界、epoch 已漂移或值损坏时返回 null（回退全量播种）。</summary>
    public static HistorySeedBoundary? ReadValidated(Conversation conversation)
    {
        if (TryRead(conversation) is not { } boundary)
            return null;

        return boundary.HistoryEpoch == MafSessionInvalidator.GetHistoryEpoch(conversation)
            ? boundary
            : null;
    }

    /// <summary>
    /// 读取供审批交接（<c>PlanHandoffExtractor</c>）定位规划窗口起点的边界：
    /// 若边界属于当前工作流自身的上一轮执行世代（同计划修订再审），返回 null 以免截断本计划的原始诉求。
    /// </summary>
    public static HistorySeedBoundary? ReadValidatedForHandoff(
        Conversation conversation,
        string? currentExecutionRequestId)
    {
        if (ReadValidated(conversation) is not { } boundary)
            return null;

        if (!string.IsNullOrWhiteSpace(currentExecutionRequestId)
            && string.Equals(boundary.ExecutionRequestId, currentExecutionRequestId, StringComparison.Ordinal))
        {
            return null;
        }

        return boundary;
    }

    private static HistorySeedBoundary? TryRead(Conversation conversation)
    {
        if (!conversation.Metadata.TryGetValue(MetadataKey, out var raw))
            return null;

        try
        {
            return raw switch
            {
                JsonElement { ValueKind: JsonValueKind.Object } element =>
                    JsonSerializer.Deserialize<HistorySeedBoundary>(element.GetRawText()),
                string text => JsonSerializer.Deserialize<HistorySeedBoundary>(text),
                _ => null,
            };
        }
        catch (JsonException)
        {
            // 损坏的边界值与缺失等价：回退全量播种，不中断受控 run。
            return null;
        }
    }
}
