namespace OneCode.Automation.Cron;

/// <summary>
/// Reverse-injected abstraction that decouples <see cref="CronSchedulerService"/> from the
/// App-layer conversation runtime. The scheduler only knows that, when a job is due, it must
/// hand the prompt off to "something" that will run it to completion; the App layer is
/// responsible for wiring that something to its own chat/session services (foreground
/// conversation injection, read-only permission policy, system-prompt construction, etc.).
/// </summary>
/// <remarks>
/// Dependency direction: App → Automation → Infrastructure → Core.
/// Automation never references <c>OneCode.App</c>; App implements this interface and
/// registers it via DI so the scheduler can consume it.
/// </remarks>
public interface ICronJobExecutor
{
    /// <summary>
    /// Execute a single triggered cron job to completion. Implementations are expected to:
    /// <list type="bullet">
    ///   <item>Ensure a foreground conversation exists (or create one).</item>
    ///   <item>Build (and cache) the system prompt.</item>
    ///   <item>Inject <paramref name="prompt"/> as a user message.</item>
    ///   <item>Drain the agent event stream to completion under a read-only tool policy.</item>
    /// </list>
    /// Implementations MUST serialize concurrent invocations themselves if they share a
    /// foreground conversation with the TUI main loop.
    /// </summary>
    /// <param name="prompt">The prompt to enqueue. Never null or whitespace.</param>
    /// <param name="ct">Cancellation token. Implementations should honor cancellation but may
    /// pass <see cref="CancellationToken.None"/> to the underlying agent if headless runs must
    /// not be cancellable mid-turn.</param>
    Task ExecuteJobAsync(string prompt, CancellationToken ct);
}
