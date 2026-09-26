using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using OneCode.App.Services.Context;
using OneCode.App.Services.Memory;
using OneCode.App.Services.Skills;
using OneCode.Core.Agent;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>Builds shared <see cref="AIContextProvider"/> lists for all agent profiles.</summary>
/// <remarks>
/// <para>
/// Providers are declared in <see cref="Registry"/> keyed by <see cref="AgentCapability"/>, and
/// <see cref="BuildCommon"/> simply walks the profile's capability set in declaration order. Adding a
/// provider is therefore a two-line change in one file (enum member + registry entry) instead of edits
/// across this builder, a profile-to-bool switch, and an options record.
/// </para>
/// <para>
/// Order follows <see cref="AgentCapability"/> declaration order, which is deliberate: the same
/// profile always yields the same provider order, so injection order stays predictable.
/// </para>
/// </remarks>
public sealed class SharedContextProviderBuilder(
    ILoggerFactory loggerFactory,
    SkillProviderFactory skillProviderFactory,
    AgentMemoryDependencies memory,
    AgentRuntimeContextDependencies runtime)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly SkillProviderFactory _skillProviderFactory = skillProviderFactory;
    private readonly AgentMemoryDependencies _memory = memory;
    private readonly AgentRuntimeContextDependencies _runtime = runtime;

    /// <summary>Builds the shared ContextProvider list for <paramref name="profile"/>.</summary>
    /// <remarks>
    /// Each entry is a factory evaluated only when the profile holds that capability, so providers
    /// whose construction is expensive or stateful (skill discovery, shell lookup, CodeAct probing)
    /// are never built for profiles that do not use them.
    /// </remarks>
    public List<AIContextProvider> BuildCommon(
        PipelineProfile profile,
        AgentContextProviderOptions options)
    {
        var behavior = PipelineProfileBehavior.For(profile);
        List<AIContextProvider> providers = [];
        var cwd = options.WorkingDirectory;

        foreach (var capability in Enum.GetValues<AgentCapability>())
        {
            if (!behavior.Has(capability) || !Registry.TryGetValue(capability, out var factory))
                continue;

            if (factory(this, options, cwd) is { } provider)
                providers.Add(provider);
        }

        return providers;
    }

    /// <summary>
    /// Capability-to-provider registry. Enumeration order defines injection order.
    /// A factory returning <see langword="null"/> means "capability enabled but not applicable right
    /// now" (e.g. no foreground shell executor) — the provider is skipped without failing the build.
    /// </summary>
    private static readonly Dictionary<AgentCapability, Func<SharedContextProviderBuilder, AgentContextProviderOptions, string, AIContextProvider?>> Registry =
        new()
        {
            // Built per run so skills discovered since the previous run (MCP servers that connected in
            // the background) take effect immediately. Within a run MAF's provider-level cache avoids
            // re-discovery on every turn.
            [AgentCapability.Skills] = (b, _, _) => b._skillProviderFactory.Create(),

            [AgentCapability.MemorySearch] = (b, _, _) => MemorySearchProviderFactory.Create(
                b._memory.MemoryService,
                b._loggerFactory),

            [AgentCapability.DesignContext] = (b, o, cwd) => new DesignContextProvider(
                b._memory.SessionManager,
                b._loggerFactory.CreateLogger<DesignContextProvider>(),
                cwd,
                o.ConversationId),

            [AgentCapability.LspDiagnostics] = (b, _, cwd) => new LspDiagnosticContextProvider(
                b._runtime.LspDiagnosticRegistry,
                b._loggerFactory.CreateLogger<LspDiagnosticContextProvider>(),
                cwd),

            [AgentCapability.ShellEnvironment] = (b, _, _) =>
                b._memory.SessionManager.ForegroundConversation is { } shellConversation
                && b._runtime.ShellExecutorManager.TryGet(shellConversation.Id) is { } shellExecutor
                    ? new ShellEnvironmentProvider(shellExecutor)
                    : null,

            [AgentCapability.CodeAct] = (b, _, cwd) => b._runtime.CodeActService.TryCreateProvider(cwd),
        };
}
