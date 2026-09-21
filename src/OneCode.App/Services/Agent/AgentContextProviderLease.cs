using Microsoft.Agents.AI;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Owns the lifetime of per-run <see cref="AIContextProvider"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an owner is needed.</b> MAF's <c>ChatClientAgent</c> does not dispose the
/// <c>AIContextProviders</c> it is given, and the product builds some of them per run (skills, so a
/// server that connected in the background is visible on the next run). Those providers hold real
/// resources — <c>CachingAgentSkillsSource</c> keeps a <see cref="SemaphoreSlim"/> gate and the MCP
/// skills source keeps a reconcile gate. Leaving them to the GC makes the number of live gates depend
/// on finalization timing rather than on how many runs happened.
/// </para>
/// <para>
/// <b>What is owned.</b> Only providers this pipeline created. Providers injected from DI
/// (memory search, LSP diagnostics, shell environment) are shared singletons and must survive the run,
/// so disposal is opt-in per provider rather than applied to the whole list.
/// </para>
/// <para>
/// <b>When it releases.</b> After the last use, which for a run is when the runner finishes — including
/// cancellation, exceptions and an early-exiting stream. The runner disposes in a <c>finally</c>, so an
/// abandoned enumeration still releases.
/// </para>
/// </remarks>
public sealed class AgentContextProviderLease : IDisposable
{
    private readonly List<IDisposable> _owned;
    private bool _disposed;

    private AgentContextProviderLease(List<IDisposable> owned)
    {
        _owned = owned;
    }

    /// <summary>
    /// Creates a lease over the providers that this pipeline created.
    /// </summary>
    /// <param name="providers">
    /// Providers assembled for one run. Only <see cref="IDisposable"/> ones are tracked; the rest are
    /// shared or stateless and outlive the run.
    /// </param>
    public static AgentContextProviderLease Track(IEnumerable<AIContextProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        return new AgentContextProviderLease(
            providers.OfType<IDisposable>().ToList());
    }

    /// <summary>Number of providers whose lifetime this lease controls.</summary>
    public int Count => _owned.Count;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Reverse order, mirroring construction: a provider built on top of another is released first.
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                _owned[i].Dispose();
            }
            catch (Exception)
            {
                // One provider failing to release must not strand the others. Disposal happens on the
                // way out of a run, where there is no caller left to report to.
            }
        }

        _owned.Clear();
    }
}
