using OneCode.Core.Domain;
using OneCode.Core.Errors;

namespace OneCode.Core.Coordinator;

/// <summary>
/// Manages multi-agent team workflows using MAF's formal Team abstractions.
///
/// MAF provides two primary team coordination patterns:
/// 1. GroupChat (RoundRobinGroupChatManager) - peer agents share a chat context,
///    taking turns in round-robin order. Best for collaborative brainstorming.
/// 2. Magentic (MagenticWorkflowBuilder) - one Orchestrator agent delegates
///    tasks to Worker agents. Best for structured task decomposition.
///
/// Team config is stored in YAML files at ~/.onecode/teams/{name}/team.yaml,
/// using the same AgentTemplateConfig format as sub-agents.
/// </summary>
public interface ITeamOrchestrationService
{
    /// <summary>
    /// Registers a team from a YAML config file so it is ready to run.
    /// </summary>
    Task RegisterTeamAsync(string teamName, string teamFilePath, CancellationToken ct = default);

    /// <summary>
    /// 流式运行 Team — 通过 <paramref name="eventSink"/> 回调实时推送协调事件给 TUI 层。
    /// 内部利用 MAF InProcessExecution.RunStreamingAsync + AgentWorkflowEventProcessor。
    /// 事件类型包括 TuiAgentCoordination / TuiAgentMessage / TuiToolStart / TuiToolDone 等。
    /// <paramref name="eventSink"/> 为 null 时仅返回最终输出，不推送中间事件（用于广播场景）。
    /// </summary>
    /// <param name="teamName">团队名称。</param>
    /// <param name="goal">团队目标。</param>
    /// <param name="eventSink">TUI 事件回调（可为 null）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="overrideMode">
    /// 运行时覆盖编排模式（Magentic/GroupChat）。
    /// 传 null 时使用团队 YAML 配置中的 template 字段决定模式；
    /// 传非 null 值时覆盖 YAML 配置，使用户在 TUI 中切换策略能真正生效。
    /// </param>
    /// <param name="imagePaths">Optional image file paths for multimodal input.</param>
    /// <param name="sessionId">Optional 会话 Id，关联到 TeamRun 以便跨进程恢复。</param>
    Task<TeamRunResult> RunTeamStreamingAsync(
        string teamName,
        string goal,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct = default,
        TeamOrchestrationMode? overrideMode = null,
        IReadOnlyList<string>? imagePaths = null,
        SessionId? sessionId = null);

    /// <summary>Returns all currently registered team names.</summary>
    IReadOnlyList<string> RegisteredTeams { get; }

    /// <summary>
    /// 当前活跃团队名称。设置后 Team 模式将使用此团队而非 RegisteredTeams[0]。
    /// 为 null 时回退到第一个注册的团队。
    /// </summary>
    string? ActiveTeam { get; set; }

    /// <summary>
    /// 获取当前应使用的团队名（ActiveTeam 或第一个注册的团队）。
    /// </summary>
    string? ResolveActiveTeam();

    /// <summary>
    /// Removes a team from the registry (but does not delete the config file).
    /// </summary>
    Task UnregisterTeamAsync(string teamName, CancellationToken ct = default);

    /// <summary>
    /// 注册内置团队模板（从嵌入式资源加载 feature-impl/code-review/research）。
    /// 幂等：已注册的同名团队不会被覆盖。
    /// 调用时机：应用启动时调用一次；若 ActiveTeam 未设置，默认为 feature-impl。
    /// </summary>
    Task RegisterBuiltinTeamsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the team's orchestration mode: "groupchat" or "magentic".
    /// Returns null if the team is not registered.
    /// </summary>
    string? GetTeamMode(string teamName);

    /// <summary>
    /// 返回指定团队的成员信息列表（角色名）。用于 TUI 启动横幅和 /team info 命令。
    /// 返回 null 表示团队未注册或成员信息不可用。
    /// </summary>
    IReadOnlyList<TeamMemberInfo>? GetTeamMembers(string teamName);

    /// <summary>
    /// 恢复指定会话的 Team 执行（流式）。
    /// 通过共享 Durable Workflow Host 开启新执行世代：已完成任务的业务事实来自
    /// TeamRun 聚合，运行中任务按新 Attempt 重启，不恢复 MAF 内部中间游标。
    /// </summary>
    Task<TeamRunResult> ResumeTeamStreamingAsync(
        SessionId sessionId,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct = default);
}

public enum TeamOrchestrationMode
{
    GroupChat,
    Magentic,
}

/// <summary>
/// 团队成员的轻量信息（跨层传递 DTO）。
/// 不依赖 App 层的 TeamMember 内部类型，供 Core 层接口和 TUI 显示使用。
/// </summary>
public sealed record TeamMemberInfo(
    string AgentId,
    string? Role,
    bool IsOrchestrator);

public sealed record TeamRunResult(
    string TeamName,
    string Output,
    int TurnsCompleted,
    bool MaxTurnsReached,
    long InputTokens = 0,
    long OutputTokens = 0,
    AgentProblemDetails? Error = null,
    SessionId? SessionId = null,
    // 标记是否有 Agent 失败。为 true 时调用方不应提交 EditTransaction。
    bool HadFailures = false,
    TeamRunId? RunId = null,
    TeamRunStatus? Status = null,
    DeliveryReport? Delivery = null);
