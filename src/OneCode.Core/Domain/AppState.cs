using OneCode.Core.Permissions;

namespace OneCode.Core.Domain;

/// <summary>
/// The application state for a OneCode session.
/// </summary>
public sealed record AppState
{
    public IReadOnlyList<Microsoft.Extensions.AI.AIFunction> Tools { get; init; } = Array.Empty<Microsoft.Extensions.AI.AIFunction>();
    public ToolPermissionContext ToolPermissionContext { get; init; } = new();

    public string? MainLoopModel { get; init; }
    public EffortLevel EffortValue { get; init; } = EffortLevel.Medium;
    public bool ThinkingEnabled { get; init; }
    /// <summary>Whether to display historical thinking blocks expanded in the TUI conversation view.</summary>
    public bool ShowThinking { get; init; }
}
