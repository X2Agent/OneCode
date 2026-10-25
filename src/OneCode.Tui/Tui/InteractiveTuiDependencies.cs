using OneCode.App.Services.BuildMode;
using OneCode.Infrastructure.Keybindings;
using OneCode.Infrastructure.Media;

namespace OneCode.Tui;

/// <summary>
/// TUI-facing dependencies for <see cref="InteractiveModeExecutor"/> context construction.
/// </summary>
public sealed record InteractiveTuiDependencies
{
    public required ImagePipeline ImagePipeline { get; init; }
    public required TrustService TrustService { get; init; }
    public required KeybindingLoader KeybindingLoader { get; init; }
    public required BuildRunReplayService BuildRunReplay { get; init; }
}
