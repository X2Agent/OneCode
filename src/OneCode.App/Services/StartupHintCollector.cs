namespace OneCode.App.Services;

/// <summary>
/// A single actionable hint to display to the user during/after startup.
/// Examples: "Detected a Go project but gopls is not installed — run /lsp install go".
/// </summary>
public sealed record StartupHint
{
    /// <summary>Stable identifier for dedup/dismiss (e.g. "lsp-missing-go").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable message shown in the TUI transcript.</summary>
    public required string Message { get; init; }

    /// <summary>Optional slash command the user can run to act on the hint (e.g. "/lsp install go").</summary>
    public string? ActionCommand { get; init; }
}

/// <summary>
/// Thread-safe collector for startup hints produced by background services
/// (e.g. <c>LspHostedService</c>) and consumed by the TUI for display.
/// Hints are deduplicated by <see cref="StartupHint.Id"/>.
/// </summary>
public interface IStartupHintCollector
{
    /// <summary>All hints collected so far (snapshot copy).</summary>
    IReadOnlyList<StartupHint> GetPending();

    /// <summary>Add a hint. Deduplicates by Id; raises <see cref="HintAdded"/> if new.</summary>
    void Add(StartupHint hint);

    /// <summary>Raised on the producer thread when a new (non-duplicate) hint is added.</summary>
    event Action<StartupHint>? HintAdded;
}

/// <summary>
/// Default implementation. Thread-safe via a lock; events are raised synchronously
/// so callers should ensure handlers are fast (or marshal to the UI thread themselves).
/// </summary>
public sealed class StartupHintCollector : IStartupHintCollector
{
    private readonly List<StartupHint> _hints = new();
    private readonly object _lock = new();

    public IReadOnlyList<StartupHint> GetPending()
    {
        lock (_lock) return _hints.ToList();
    }

    public void Add(StartupHint hint)
    {
        lock (_lock)
        {
            if (_hints.Any(h => h.Id == hint.Id))
                return;
            _hints.Add(hint);
        }

        HintAdded?.Invoke(hint);
    }

    public event Action<StartupHint>? HintAdded;
}
