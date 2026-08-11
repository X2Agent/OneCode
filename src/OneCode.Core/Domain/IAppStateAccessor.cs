namespace OneCode.Core.Domain;

/// <summary>
/// Provides read/write access to the mutable session-scoped application state.
/// Registered as a singleton in the DI container for the CLI lifetime.
/// </summary>
public interface IAppStateAccessor
{
    AppState Current { get; }

    void Update(Func<AppState, AppState> updater);
}
