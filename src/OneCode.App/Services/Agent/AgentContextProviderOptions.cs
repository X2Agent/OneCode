using Microsoft.Extensions.AI;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Controls which shared providers <see cref="SharedContextProviderBuilder.BuildCommon"/> includes.
/// </summary>
public sealed record AgentContextProviderOptions
{
    public required string WorkingDirectory { get; init; }
    public SessionId? ConversationId { get; init; }
    public bool IncludeSessionMemory { get; init; } = true;
    public bool IncludeLspDiagnostics { get; init; } = true;
    public bool IncludeShellEnvironment { get; init; } = true;
    public bool IncludeCodeAct { get; init; } = true;
    public IReadOnlyList<AITool>? CodeActTools { get; init; }
    public IChatClient? ChatClient { get; init; }
}
