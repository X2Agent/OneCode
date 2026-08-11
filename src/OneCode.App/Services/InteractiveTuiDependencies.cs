using OneCode.App.Services.BuildMode;
using OneCode.App.Tui;
using OneCode.Infrastructure.Keybindings;
using OneCode.Core.Cost;
using OneCode.Infrastructure.Media;

namespace OneCode.App.Services;

/// <summary>
/// TUI-facing dependencies for <see cref="InteractiveModeExecutor"/> context construction.
/// </summary>
public sealed record InteractiveTuiDependencies
{
    public required ImagePipeline ImagePipeline { get; init; }
    public required TrustService TrustService { get; init; }
    public required ICostTracker CostTracker { get; init; }
    public required KeybindingLoader KeybindingLoader { get; init; }
    public required BuildRunTuiReplayService BuildRunTuiReplay { get; init; }
}
