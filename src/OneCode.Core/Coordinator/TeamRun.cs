using OneCode.Core.Domain;
using OneCode.Core.Errors;

namespace OneCode.Core.Coordinator;

public readonly record struct TeamRunId(string Value)
{
    public static TeamRunId NewId() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}

public enum TeamRunPhase
{
    Intake,
    Clarification,
    Planning,
    AwaitingApproval,
    Execution,
    Verification,
    Delivery,
    Completed,
}

public enum TeamRunStatus
{
    Created,
    Running,
    WaitingForUser,
    Blocked,
    Succeeded,
    Failed,
    Cancelled,
    RolledBack,
}

public enum TeamTaskStatus
{
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    Blocked,
}

public enum TeamTaskKind
{
    Analysis,
    Planning,
    Implementation,
    Test,
    Review,
    Acceptance,
}

public enum TeamToolPolicy
{
    ReadOnly,
    WriteAllowed,
}

public enum QualityGateKind
{
    WorkspaceCleanliness,
    Build,
    UnitTest,
    IntegrationTest,
    LspDiagnostics,
    AcceptanceCriteria,
    ChangeScope,
    Security,
}

public enum QualityGateStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    SkippedByDependency,
}

public sealed record RequirementBaseline(
    string ProductGoal,
    IReadOnlyList<string> InScope,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OpenQuestions,
    bool RequiresApproval);

public sealed record TeamTaskDefinition(
    string Id,
    string Title,
    TeamTaskKind Kind,
    string AssigneeRole,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> AcceptanceCriteria,
    TeamToolPolicy ToolPolicy,
    bool Required = true,
    IReadOnlyList<string>? RequiredTools = null,
    IReadOnlyList<string>? AllowedPaths = null,
    IReadOnlyList<string>? RequiredGates = null,
    IReadOnlyList<string>? ExpectedOutputs = null,
    int MaxAttempts = 1,
    TaskRetryPolicy? RetryPolicy = null);

/// <summary>
/// 瞬时异常重试策略。MAF 1.15.0 未提供通用业务 RetryPolicy，
/// OneCode 使用标准 Resilience/Polly Pipeline 包装 Executor 调用。
/// Retry 只包围无副作用调用；文件/发布操作依赖 OperationId 幂等 Ledger，不能自动盲重试。
/// </summary>
public sealed record TaskRetryPolicy(
    int MaxAttempts = 1,
    TimeSpan InitialDelay = default,
    TimeSpan MaxDelay = default,
    double BackoffMultiplier = 2.0,
    IReadOnlyList<string>? RetryableErrorFingerprints = null)
{
    public static TaskRetryPolicy Default { get; } = new(
        MaxAttempts: 3,
        InitialDelay: TimeSpan.FromSeconds(2),
        MaxDelay: TimeSpan.FromSeconds(30),
        BackoffMultiplier: 2.0);
}

public sealed record TeamTaskState(
    TeamTaskDefinition Definition,
    TeamTaskStatus? Status,
    int Attempt = 0,
    string? Summary = null,
    AgentProblemDetails? Failure = null,
    string? ErrorFingerprint = null);

public sealed record QualityGateDefinition(
    string Id,
    QualityGateKind Kind,
    bool Required,
    string Description);

public sealed record QualityGateResult(
    string GateId,
    QualityGateKind Kind,
    bool Required,
    QualityGateStatus Status,
    string Summary,
    IReadOnlyList<string> Evidence,
    TimeSpan Duration);

public sealed record ImplementationPlan(
    string Summary,
    IReadOnlyList<TeamTaskDefinition> Tasks,
    IReadOnlyList<QualityGateDefinition> RequiredGates,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> NonGoals);

public sealed record TeamTaskGraph(IReadOnlyList<TeamTaskState> Tasks)
{
    public IReadOnlyList<TeamTaskState> RequiredTasks => Tasks.Where(t => t.Definition.Required).ToList();
}

public sealed record ChangeSetSummary(
    IReadOnlyList<FileChange> Files,
    int AddedLineCount,
    int RemovedLineCount);

public sealed record DeliveryReport(
    TeamRunId RunId,
    string TeamName,
    bool Committed,
    string Summary,
    IReadOnlyList<TeamTaskState> Tasks,
    IReadOnlyList<QualityGateResult> Gates,
    ChangeSetSummary Changes,
    IReadOnlyList<string> Risks,
    DateTimeOffset GeneratedAt);

public sealed record TeamRun
{
    public required TeamRunId Id { get; init; }
    public required string TeamName { get; init; }
    public required string OriginalRequest { get; init; }
    public required string WorkingDirectory { get; init; }
    public required TeamRunPhase Phase { get; init; }
    public required TeamRunStatus Status { get; init; }
    public RequirementBaseline? Requirements { get; init; }
    public ImplementationPlan? Plan { get; init; }
    public TeamTaskGraph? TaskGraph { get; init; }
    public IReadOnlyList<QualityGateResult> GateResults { get; init; } = [];
    public ChangeSetSummary Changes { get; init; } = new([], 0, 0);
    public AgentProblemDetails? Failure { get; init; }
    public DeliveryReport? Delivery { get; init; }
    public bool PlanApproved { get; init; }
    public bool TransactionCommitted { get; init; }

    /// <summary>
    /// 发起该 TeamRun 的前台会话 ID（可为空，例如测试/后台宿主）。
    /// /checkpoint resume 通过它把用户会话映射到可恢复的 TeamRun。
    /// </summary>
    public SessionId? SessionId { get; init; }

    /// <summary>
    /// 当前 Workflow Run 持有者令牌。非空表示该 TeamRun 已被 Durable Workflow Host Claim，
    /// 此后所有业务写入必须携带完全相同的令牌；新持有者接管时令牌单调递增，
    /// 旧持有者即使仍在运行也无法再写入（与 BuildRun/GoalRun 一致的双层 Fencing）。
    /// </summary>
    public long? WorkflowFencingToken { get; init; }

    /// <summary>
    /// 最后一个 Succeeded 任务落库时的工作区指纹（Build 模式 CommitWorkspaceFingerprint 的同款机制）。
    /// 恢复新世代时先回滚 ledger 未提交副作用，再比对当前指纹；不一致说明已完成任务的
    /// 文件改动已不在盘（崩溃后被 reconcile 回滚 / 工作区被外部篡改），
    /// Succeeded 任务必须降级重跑，防止聚合声称完成而文件静默丢失。
    /// 为 null 表示无指纹记录（旧数据 / provider 不可用），恢复时跳过校验。
    /// </summary>
    public string? LastTaskFingerprint { get; init; }

    /// <summary>
    /// 本次 Run 实际生效的编排模式（含 TUI overrideMode 覆盖后的结果）。
    /// Resume 用它重建 config，避免用户覆盖策略后崩溃恢复时静默回退到 YAML 默认模板模式。
    /// null 表示旧数据或未记录——恢复时按 YAML 默认处理（向后兼容）。
    /// </summary>
    public TeamOrchestrationMode? EffectiveMode { get; init; }

    public long Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public interface ITeamRunStore
{
    Task<TeamRun?> LoadAsync(TeamRunId runId, CancellationToken ct = default);
    Task<TeamRun?> LoadActiveAsync(string workingDirectory, CancellationToken ct = default);

    /// <summary>列出所有非终态 TeamRun（按更新时间倒序），供恢复扫描使用。</summary>
    Task<IReadOnlyList<TeamRun>> ListActiveAsync(CancellationToken ct = default);

    Task<bool> TrySaveAsync(TeamRun run, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// 原子声明 Workflow 持有权：新令牌必须严格大于磁盘当前令牌。
    /// Claim 成功后，不带令牌的 <see cref="TrySaveAsync"/> 一律拒绝。
    /// </summary>
    Task<TeamRun> ClaimWorkflowAsync(
        TeamRunId runId,
        long fencingToken,
        long expectedVersion,
        CancellationToken ct = default);

    /// <summary>携带当前 FencingToken 的保存；令牌与磁盘不一致时 fail-closed。</summary>
    Task SaveFencedAsync(
        TeamRun run,
        long expectedVersion,
        long expectedFencingToken,
        CancellationToken ct = default);
}
