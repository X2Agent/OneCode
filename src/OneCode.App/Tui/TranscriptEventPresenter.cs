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

            case TuiAgentMessage { AgentName: var agent }:
                transcript.UpdateModeProgress(new TuiModeProgress(
                    WorkingMode.Team,
                    $"{agent} 已完成阶段工作…"));
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

            default:
                return false;
        }
    }
}
