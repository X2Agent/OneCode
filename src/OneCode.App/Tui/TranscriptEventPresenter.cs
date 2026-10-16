namespace OneCode.App.Tui;

/// <summary>
/// Projects transcript-only <see cref="TuiEvent"/> values into
/// <see cref="ChatTranscriptView"/>. Cross-region events remain coordinated by
/// <see cref="OneCodeToplevel"/>.
/// </summary>
internal sealed class TranscriptEventPresenter(ChatTranscriptView transcript)
{
    private readonly Dictionary<OneCode.Core.Build.BuildRunId, long> _buildRunSequences = [];

    public void Reset() => _buildRunSequences.Clear();

    public bool TryPresent(TuiEvent evt)
    {
        switch (evt)
        {
            case TuiTextDelta { Text: var text }:
                transcript.AppendStreamingToken(text);
                return true;

            case TuiThinkingDelta { Text: var thought }:
                transcript.AddThinking(thought);
                return true;

            case TuiToolStart { ToolId: var id, Name: var name, ToolInput: var input, AgentName: var agent }:
                transcript.AddToolStart(name, input, id, agent);
                return true;

            case TuiBuildRunState state:
                if (!_buildRunSequences.TryGetValue(state.RunId, out var sequence)
                    || state.SequenceNumber > sequence)
                {
                    _buildRunSequences[state.RunId] = state.SequenceNumber;
                    transcript.UpdateBuildRunStatus(state);
                }
                return true;

            case TuiBuildDelivery { Result: var delivery }:
                transcript.AddBuildDeliveryCard(delivery);
                return true;

            case TuiAgentMessage { AgentName: var agent, Content: var content }:
                // 子代理/工作流消息直达主对话（折叠预览块），不再压缩成一行进度。
                transcript.AddAgentSpeech(agent, content);
                return true;

            case TuiModeProgress progress:
                transcript.UpdateModeProgress(progress);
                return true;

            case TuiGoalPlan { Steps: var steps }:
                // P1：分解结果对用户可见——执行前展示编号子目标清单（一次性块，不刷屏）。
                transcript.AddSystem($"已分解为 {steps.Count} 个子目标：");
                for (var i = 0; i < steps.Count; i++)
                    transcript.AddSystem($"  {i + 1}. {steps[i]}");
                return true;

            case TuiGoalResult
            {
                Completed: var completed,
                Failed: var failed,
                Skipped: var skipped,
                Committed: var committed,
                ValidationSummary: var summary,
                ChangedFiles: var changedFiles,
                FinalConclusion: var conclusion,
            }:
                if (!string.IsNullOrWhiteSpace(conclusion))
                {
                    transcript.AppendStreamingToken(conclusion.Trim() + "\n");
                }
                var publishTag = committed ? " · 已合并提交到当前分支" : "";
                transcript.AddSystem($"Goal 执行结束：✓ 完成 {completed} · ✗ 失败 {failed} · ○ 跳过 {skipped}{publishTag}");
                if (changedFiles is { Count: > 0 })
                {
                    transcript.AddSystem($"  产物文件 ({changedFiles.Count})：{string.Join(", ", changedFiles.Take(10))}{(changedFiles.Count > 10 ? " …" : "")}");
                }
                foreach (var line in summary.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    transcript.AddSystem($"  {line.Trim()}");
                return true;

            case TuiToolDone { Name: var name, IsError: var err, Result: var result, ToolInput: var input, ToolId: var tid, AgentName: var agent }:
                transcript.AddToolDone(name, err, input, result ?? "", tid, agent);
                // 子代理 fork 的最终发言：同步模式的完整结果渲染为说话人块（代理名 +
                // 预览 + 折叠）；后台模式信封 {background:true, taskId, ...} 无 result
                // 文本，必须跳过（发言块随真实结果到达，由任务输出承载）。
                if (ToolResultSummarizer.NormalizeName(name) == "agent"
                    && TryExtractAgentSpeech(result, out var speechAgent, out var speechText))
                    transcript.AddAgentSpeech(speechAgent, speechText);
                return true;


            case TuiFileChange { FileName: var file, AddedLines: var added, RemovedLines: var removed, AgentName: var agent }:
                transcript.AddFileChange(file, agent, added, removed);
                return true;

            case TuiCompactSuggested { Message: var message }:
                transcript.AddStreamingNotice($"💡 {message}", TuiPalette.Warning);
                return true;

            case TuiNotice notice:
                transcript.AddStreamingNotice(notice.Message, TuiPalette.Warning);
                return true;

            case TuiGoalBudgetWarning warning:
                // Fix-6：GOAL 预算预警横幅——Early(70%) 黄色，Late(90%) 橙色。
                var color = warning.Level == OneCode.Core.Goals.GoalBudgetWarningLevel.Late
                    ? TuiPalette.AgentOrange
                    : TuiPalette.AgentYellow;
                var label = warning.Level == OneCode.Core.Goals.GoalBudgetWarningLevel.Late ? "橙色预警" : "黄色预警";
                transcript.AddStreamingNotice(
                    $"⚠ GOAL 预算{label}：已消耗 attempts={warning.TotalAttempts}, tokens={warning.TotalTokens}",
                    color);
                return true;

            case TuiBudgetWarning budgetWarning:
                // 全模式预算预警横幅：与 GOAL 预警同一套黄/橙两级配色
                var budgetColor = budgetWarning.Level == OneCode.Core.Goals.GoalBudgetWarningLevel.Late
                    ? TuiPalette.AgentOrange
                    : TuiPalette.AgentYellow;
                var budgetLabel = budgetWarning.Level == OneCode.Core.Goals.GoalBudgetWarningLevel.Late ? "橙色预警" : "黄色预警";
                transcript.AddStreamingNotice(
                    $"⚠ 预算{budgetLabel}：本次执行已消耗 {budgetWarning.UsedTokens:N0} / {budgetWarning.LimitTokens:N0} tokens",
                    budgetColor);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 从 AgentTool 结果 JSON 提取发言块内容：同步成功信封的 result 文本 + 代理名。
    /// 后台模式（background=true）或解析失败返回 false，调用方不渲染发言块。
    /// internal 供单元测试覆盖（含后台信封反面场景）。
    /// </summary>
    internal static bool TryExtractAgentSpeech(string? result, out string agentName, out string speech)
    {
        agentName = "agent";
        speech = string.Empty;
        if (string.IsNullOrWhiteSpace(result) || result[0] != '{')
            return false;
        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (root.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.True)
                return false;
            if (!root.TryGetProperty("result", out var resultEl) || resultEl.ValueKind != JsonValueKind.String)
                return false;
            var text = resultEl.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (root.TryGetProperty("agent", out var agentEl) && agentEl.ValueKind == JsonValueKind.String
                && agentEl.GetString() is { Length: > 0 } name)
                agentName = name;
            speech = text;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
