namespace OneCode.App.Commands;

/// <summary>
/// Thread-safe singleton accessor for mutable session-scoped application state.
/// </summary>
public sealed class AppStateAccessor : IAppStateAccessor
{
    private readonly Lock _lock = new();
    private AppState _state = new();

    public AppState Current
    {
        get { lock (_lock) { return _state; } }
    }

    public void Update(Func<AppState, AppState> updater)
    {
        lock (_lock) { _state = updater(_state); }
    }
}
