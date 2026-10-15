namespace OneCode.Tests.TestSupport;

using System.Diagnostics;

/// <summary>
/// Polling-based wait helper for async timing scenarios (debounce windows, watcher callbacks,
/// background loops). Replaces fixed <c>Task.Delay</c> sleeps: it returns as soon as the condition
/// is met and fails once the deadline elapses — faster on success and immune to slow-machine flake.
/// </summary>
internal static class Eventually
{
    // 5s ceiling: only paid when the condition genuinely never becomes true; polling exits early.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // 10ms poll cadence: fast enough to not dominate the 5s budget, slow enough to not spin hot.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Polls <paramref name="condition"/> every 10ms until it returns true.
    /// Throws after <paramref name="timeout"/> (default 5s) with <paramref name="because"/>
    /// as the diagnostic message.
    /// </summary>
    public static async Task TrueAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        string? because = null,
        CancellationToken ct = default)
    {
        var budget = timeout ?? DefaultTimeout;
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > budget)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Condition was not met within {budget.TotalMilliseconds:F0}ms{(because is null ? "" : $": {because}")}");
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls <paramref name="probe"/> until the captured value satisfies <paramref name="when"/>
    /// (non-null by default), returning the last observed value. Useful for NSubstitute callbacks
    /// that record invocations into a local variable.
    /// </summary>
    public static async Task<T> ValueAsync<T>(
        Func<T> probe,
        Func<T, bool>? when = null,
        TimeSpan? timeout = null,
        string? because = null,
        CancellationToken ct = default)
    {
        T value = default!;
        var satisfied = when ?? (v => v is not null);
        await TrueAsync(
            () => { value = probe(); return satisfied(value); },
            timeout,
            because,
            ct).ConfigureAwait(false);
        return value;
    }
}
