namespace OneCode.Core.Workflows;

/// <summary>
/// 四模式 durable run 聚合（BuildRun / PlanWorkflow / TeamRun / GoalRun）的共享词汇
/// （共享内核）。以 compile-time 形式实现，
/// 不改变任何持久化 schema（兼容红线：字段只加不删不改型）。
/// </summary>
public interface IWorkflowRun
{
    /// <summary>乐观并发版本号。</summary>
    long Version { get; }

    /// <summary>当前 Workflow lease 令牌；null 表示尚未被 Durable Workflow Host claim。</summary>
    long? WorkflowFencingToken { get; }

    DateTimeOffset UpdatedAt { get; }
}

/// <summary>
/// 统一 durable run store 契约：
/// Save(expectedVersion CAS) / ClaimWorkflowAsync(fencing) / SaveFencedAsync 收编为一份签名。
/// 各模式的 Load 键类型不同（Build=ConversationId、Goal/Plan=SessionId、Team=RunId），由各模式接口自行保留。
/// 实现必须保持既有文件布局与 JSON schema 字节级不变（持久化兼容红线）。
/// </summary>
/// <typeparam name="TRun">模式聚合根类型。</typeparam>
/// <typeparam name="TId">模式强类型 run Id。</typeparam>
public interface IWorkflowRunStore<TRun, TId>
    where TRun : IWorkflowRun
    where TId : notnull
{
    /// <summary>乐观并发保存：磁盘版本与 <paramref name="expectedVersion"/> 不一致时 CAS 失败；已 claim 的 run 拒绝无令牌写入。</summary>
    Task SaveAsync(TRun run, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// 原子声明 Workflow 持有权：新令牌必须严格大于磁盘当前令牌。
    /// Claim 成功后，不带令牌的 <see cref="SaveAsync"/> 一律拒绝。
    /// </summary>
    Task<TRun> ClaimWorkflowAsync(
        TId runId,
        long fencingToken,
        long expectedVersion,
        CancellationToken ct = default);

    /// <summary>携带当前 FencingToken 的保存；令牌与磁盘不一致时 fail-closed。</summary>
    Task SaveFencedAsync(TRun run, long expectedVersion, long fencingToken, CancellationToken ct = default);
}

/// <summary>
/// 列出所有非终态 run（按更新时间倒序），供恢复扫描使用。
/// 仅具备恢复扫描语义的模式实现（Goal / Team）。
/// </summary>
public interface IActiveWorkflowRunStore<TRun>
    where TRun : IWorkflowRun
{
    Task<IReadOnlyList<TRun>> ListActiveAsync(CancellationToken ct = default);
}

/// <summary>
/// Workflow fencing 校验共享 helper——收敛 Build/Goal store 中
/// 逐字同构的 <c>ValidateFencing</c> 私有实现。语义：
/// claim 时令牌必须严格递增；fenced 写要求磁盘令牌与候选令牌一致；
/// 未 claim 的 run 拒绝任何带令牌写入。
/// </summary>
public static class WorkflowFencing
{
    /// <param name="currentToken">磁盘当前令牌（null 表示未 claim）。</param>
    /// <param name="candidateToken">候选聚合携带的令牌。</param>
    /// <param name="requiredFencingToken">调用方要求的令牌（SaveFenced 传入，Save 传 null）。</param>
    /// <param name="isClaim">是否为 Claim 写入。</param>
    /// <param name="runKind">聚合名（用于异常消息，保持各模式原消息文本）。</param>
    public static void Validate(
        long? currentToken,
        long? candidateToken,
        long? requiredFencingToken,
        bool isClaim,
        string runKind)
    {
        if (isClaim)
        {
            if (requiredFencingToken is not { } claimToken || candidateToken != claimToken)
                throw new InvalidOperationException($"{runKind} workflow claim has an invalid fencing token.");
            if (currentToken is { } existing && claimToken <= existing)
                throw new InvalidOperationException($"Stale {runKind} workflow fencing token.");
            return;
        }

        if (currentToken is { } fencedToken)
        {
            if (requiredFencingToken != fencedToken || candidateToken != fencedToken)
                throw new InvalidOperationException($"Stale {runKind} workflow fencing token.");
            return;
        }

        if (requiredFencingToken is not null || candidateToken is not null)
            throw new InvalidOperationException($"{runKind} must be claimed before fenced writes.");
    }
}
