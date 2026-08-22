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

            case TuiToolStart { ToolId: var id, Name: var name, ToolInput: var input }:
                transcript.AddToolStart(name, input, id);
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

            case TuiTeamProgress { Header: var header }:
                transcript.UpdateModeProgress(new TuiModeProgress(WorkingMode.Team, header));
                return true;

            case TuiModeProgress progress:
                transcript.UpdateModeProgress(progress);
                return true;

            case TuiGoalResult:
                // Structured evidence is retained for resume/reporting. The user-facing
                // transcript already receives the concise TuiModeProgress projection.
                return true;

            case TuiFileChange { FileName: var file, AddedLines: var added, RemovedLines: var removed }:
                transcript.AddFileChange(file, added, removed);
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
