using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Shared models extracted from TeamOrchestrationService.
/// TeamConfig 描述一个已注册团队的完整配置；TeamMember 描述单个成员。
/// </summary>
internal sealed record TeamConfig(
    string TeamName,
    string FilePath,
    IReadOnlyList<TeamMember> Members,
    int MaxTurns,
    TeamOrchestrationMode Mode);

internal sealed record TeamMember(
    string AgentId,
    string? Role,
    string? SystemPrompt,
    IReadOnlyList<string>? AllowedTools = null);
