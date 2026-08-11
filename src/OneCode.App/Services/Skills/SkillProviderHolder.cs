// MAAI001 suppressed: AgentSkillsProvider hot-swap wrapper triggers experimental API warning
using Microsoft.Agents.AI;

namespace OneCode.App.Services.Skills;

/// <summary>
/// Thread-safe, replaceable wrapper around the current <see cref="AgentSkillsProvider"/>.
/// <see cref="SkillChangeWatcher"/> replaces the provider when skill files change on disk;
/// the IChatClient wrapper reads it via <see cref="Current"/> without needing to be rebuilt.
/// </summary>
/// <remarks>
/// MAF 1.13.0+ made skill sources disposable (#6827), so the previous provider is now
/// disposed on replacement to avoid leaking file handles and cached skill state.
/// </remarks>
public sealed class SkillProviderHolder : IDisposable
{
    private volatile AgentSkillsProvider? _current;

    public SkillProviderHolder(AgentSkillsProvider initial) => _current = initial;

    /// <summary>Gets the current provider. May be null briefly during replacement.</summary>
    public AgentSkillsProvider? Current => _current;

    /// <summary>
    /// Atomically replaces the current provider and disposes the previous one.
    /// </summary>
    public void Replace(AgentSkillsProvider next)
    {
        var old = Interlocked.Exchange(ref _current, next);
        (old as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        var old = Interlocked.Exchange(ref _current, null);
        (old as IDisposable)?.Dispose();
    }
}
