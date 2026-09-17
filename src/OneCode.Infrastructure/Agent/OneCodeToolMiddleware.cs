using Microsoft.Agents.AI;
using OneCode.Core.Domain;
using OneCode.Infrastructure.Middleware;
using OneCode.Infrastructure.Middleware.Invariants;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Single source of truth for mounting OneCode's product policy middleware onto a MAF agent.
/// </summary>
/// <remarks>
/// <para>
/// Sibling of <see cref="OneCodeHarnessDefaults"/>: the latter owns <see cref="HarnessAgentOptions"/>
/// while this type owns <b>middleware install order</b>. Any host that builds an agent outside
/// <see cref="AgentPipelineBuilder"/> (currently the AutoDream background service) must call this so
/// the safety and result-budget gates cannot drift apart between paths.
/// </para>
/// <para>
/// This deliberately mounts only the two gates that are meaningful without an interactive host:
/// safety invariants (Layer 0 hard blocks) and the tool-result character budget. Permission checks,
/// approval, edit transactions, edit guards and the state machine stay out — they require a user
/// session, a working-directory transaction or an approval broker that AutoDream does not have.
/// </para>
/// <para>
/// Install order (outermost first): SafetyInvariant → ToolExecutionBudget.
/// Safety invariants must stay outermost so read-only tools are still checked against sensitive
/// path sequences (<c>id_rsa</c>, <c>.env</c>, <c>.aws/credentials</c>) even when only
/// Read/Glob/Grep are exposed.
/// </para>
/// </remarks>
public static class OneCodeToolMiddleware
{
    /// <summary>
    /// Mounts the product policy middleware for a non-interactive agent.
    /// </summary>
    /// <param name="builder">MAF agent builder to mount onto.</param>
    /// <param name="workingDirectory">Working directory used to scope filesystem safety invariants.</param>
    /// <param name="loggerFactory">Logger factory for middleware diagnostics.</param>
    /// <returns>The same builder for chaining.</returns>
    public static AIAgentBuilder Apply(
        AIAgentBuilder builder,
        string workingDirectory,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        builder = builder.Use(SafetyInvariantMiddleware.Create(
            CreateDefaultSafetyInvariants(workingDirectory),
            loggerFactory.CreateLogger("SafetyInvariantMiddleware")));

        builder = builder.Use(new ToolExecutionBudgetMiddleware(
            maxResultChars: ToolExecutionBudgetMiddleware.DefaultMaxResultChars,
            logger: loggerFactory.CreateLogger<ToolExecutionBudgetMiddleware>()).CreateDelegate());

        return builder;
    }

    /// <summary>
    /// Default Layer 0 invariants. Mirrors <c>AgentPipelineBuilder.CreateDefaultSafetyInvariants</c>;
    /// both hosts must stay aligned.
    /// </summary>
    internal static IReadOnlyList<ISafetyInvariant> CreateDefaultSafetyInvariants(string workingDirectory) =>
    [
        new FileSystemInvariant(workingDirectory),
        new BashCommandInvariant(),
        new ResourceInvariant(),
    ];
}
