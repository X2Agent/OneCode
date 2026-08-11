using OneCode.App.Tui;

namespace OneCode.App.Services;

/// <summary>
/// Mutable holder for the TUI event emitter, set by <see cref="InteractiveModeExecutor"/>
/// when the TUI starts. <see cref="UserQuestionService"/> depends on this bridge instead of
/// resolving <see cref="TuiContext"/> via service locator.
/// </summary>
/// <remarks>
/// Session-scoped mutable bridge: intentionally set once when the TUI is ready.
/// Not a DI anti-pattern — the emitter cannot exist before the TUI host runs.
/// </remarks>
public sealed class TuiInteractionBridge
{
    private Action<TuiEvent>? _emitEvent;

    /// <summary>Gets the current event emitter. Null before TUI initialization or in headless mode.</summary>
    public Action<TuiEvent>? EmitEvent => _emitEvent;

    /// <summary>Sets the event emitter when the TUI is ready.</summary>
    public void SetEmitter(Action<TuiEvent>? emitEvent) => _emitEvent = emitEvent;
}
