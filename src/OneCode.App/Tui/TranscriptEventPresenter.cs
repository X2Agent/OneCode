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

            case TuiAgentCoordination { FromName: var from, ToName: var to }:
                transcript.UpdateModeProgress(new TuiModeProgress(
                    WorkingMode.Team,
                    $"正在协调 {from} 与 {to}…"));
                return true;

            case TuiAgentMessage { AgentName: var agent, Content: var content }:
                // TEAM 讨论内容直达主对话（折叠预览块），不再压缩成一行进度。
                transcript.AddTeamSpeech(agent, content);
                return true;

            case TuiTeamProgress { Header: var header, Tasks: var tasks }:
                if (tasks.Count == 0)
                {
                    transcript.UpdateModeProgress(new TuiModeProgress(WorkingMode.Team, header));
                }
                else
                {
                    // 仅打印摘要行（如「团队 'impl' 需要澄清 N 个问题后才能规划」）。
                    // 问题明细不再逐条打印——澄清向导组件已完整展示问题，重复输出造成干扰。
                    transcript.AddSystem(header);
                }
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

            case TuiGoalResult { Completed: var completed, Failed: var failed, Skipped: var skipped, ValidationSummary: var summary }:
                // P1：结构化证据保留用于 resume/报告；对话流渲染简洁结果卡
                // （此前被静默吞掉，用户看不到任何步骤级结论）。
                transcript.AddSystem($"Goal 执行结束：✓ 完成 {completed} · ✗ 失败 {failed} · ○ 跳过 {skipped}");
                foreach (var line in summary.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    transcript.AddSystem($"  {line.Trim()}");
                return true;

            case TuiToolDone { Name: var name, IsError: var err, Result: var result, ToolInput: var input, ToolId: var tid, AgentName: var agent }:
                transcript.AddToolDone(name, err, input, result ?? "", tid, agent);
                return true;

            case TuiTeamTaskProgress:
                // TEAM 任务级进度已移入右侧 TeamSidebarView（状态板），
                // 不再逐条刷对话流 ModeProgress——多任务并行时互相覆盖且刷屏。
                // 事件仍在此被吞掉（返回 true），避免落入 default 处理。
                return true;

            case TuiFileChange { FileName: var file, AddedLines: var added, RemovedLines: var removed, AgentName: var agent }:
                transcript.AddFileChange(file, agent, added, removed);
                return true;

            case TuiCompactSuggested { Message: var message }:
                transcript.AddStreamingNotice($"💡 {message}", TuiPalette.Warning);
                return true;

            case TuiGoalBudgetWarning warning:
                // Fix-6：GOAL 预算预警横幅——Early(70%) 黄色，Late(90%) 橙色。
                var color = warning.Level == OneCode.Core.Goals.GoalBudgetWarningLevel.Late
                    ? TuiPalette.AgentOrange
                    : TuiPalette.AgentYellow;
                var label = warning.Level == OneCode.Core.Goals.GoalBudgetWarningLevel.Late ? "橙色预警" : "黄色预警";
                transcript.AddStreamingNotice(
                    $"⚠ GOAL 预算{label}：已消耗 attempts={warning.TotalAttempts}, tokens={warning.TotalTokens}, cost=${warning.EstimatedCostUsd:0.####}",
                    color);
                return true;

            default:
                return false;
        }
    }
}
