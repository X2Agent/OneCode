using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using OneCode.Core.PlanMode;
using OneCode.Infrastructure;

namespace OneCode.App.Services.PlanMode;

/// <summary>Contains all durable product facts for one Plan aggregate.</summary>
public sealed record PlanAggregate(
    PlanWorkflow Workflow,
    IReadOnlyList<PlanRevision> Revisions)
{
    /// <summary>Returns the specified immutable revision.</summary>
    public PlanRevision? FindRevision(int revision)
        => Revisions.SingleOrDefault(candidate => candidate.Revision == revision);
}

/// <summary>Persists a complete Plan aggregate through one versioned atomic write.</summary>
public interface IPlanAggregateStore
{
    Task<PlanAggregate?> LoadAsync(SessionId sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<PlanWorkflow>> LoadRecoverableExecutionAsync(CancellationToken ct = default);
    Task SaveAsync(PlanAggregate aggregate, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// 原子声明 Workflow 持有权（与其余三模式内核语义一致）：
    /// 新令牌必须严格大于磁盘当前令牌；Claim 成功后，不带令牌的
    /// <see cref="SaveAsync"/> 一律拒绝。
    /// </summary>
    Task<PlanAggregate> ClaimWorkflowAsync(
        SessionId sessionId,
        PlanWorkflowId planId,
        long fencingToken,
        long expectedVersion,
        CancellationToken ct = default);

    /// <summary>携带当前 FencingToken 的保存；令牌与磁盘不一致时 fail-closed。</summary>
    Task SaveFencedAsync(
        PlanAggregate aggregate,
        long expectedVersion,
        long fencingToken,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the markdown projection path for a plan revision
    /// (<c>{plansRoot}/{sessionId}/{planId}/revision-{revision:0000}.md</c>), written
    /// by <see cref="SaveAsync"/>. Used to surface the document location to users.
    /// </summary>
    string GetRevisionMarkdownPath(SessionId sessionId, PlanWorkflowId planId, int revision);
}

/// <summary>
/// JSON aggregate store with cross-process exclusion, optimistic concurrency, checksums,
/// write-through replacement, last-known-good backup, and post-write verification.
/// </summary>
public sealed class PlanAggregateStore : IPlanAggregateStore
{
    /// <summary>v2 起写 WorkflowFencingToken（Plan fencing 接入）；v1 旧聚合兼容读。</summary>
    private const int SchemaVersion = 2;
    private const int LockRetryDelayMilliseconds = 25;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    private readonly string _plansRoot;
    private readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();
    private readonly ILogger<PlanAggregateStore>? _logger;

    /// <summary>
    /// 已报告过"无法读取"的聚合文件（全路径）。后台扫描每 5 秒遍历一次磁盘，
    /// 同一损坏文件不去重会以 WARNING 刷屏；首见记录 Debug 详情，重复出现静默跳过。
    /// 容量上限防御恶意目录场景；进程重启或文件重写（保存会校验）自然清零。
    /// </summary>
    private readonly HashSet<string> _reportedUnreadable = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxReportedUnreadableEntries = 64;

    public PlanAggregateStore(
        string? basePath = null,
        ILogger<PlanAggregateStore>? logger = null)
    {
        _plansRoot = basePath ?? Path.Combine(PathsHelper.GetUserConfigDir(), "plans");
        _logger = logger;
        Directory.CreateDirectory(_plansRoot);
    }

    public async Task<PlanAggregate?> LoadAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var sessionDirectory = GetSessionDirectory(sessionId);
        if (!Directory.Exists(sessionDirectory))
            return null;

        var aggregateFiles = Directory.GetFiles(sessionDirectory, "aggregate.json", SearchOption.AllDirectories);
        var backupFiles = Directory.GetFiles(sessionDirectory, "aggregate.json.bak", SearchOption.AllDirectories);
        if (aggregateFiles.Length == 0 && backupFiles.Length == 0)
            return null;
        if (aggregateFiles.Length > 1)
            throw new PlanTransitionException($"Session '{sessionId}' contains multiple Plan aggregates.");

        if (aggregateFiles.Length == 1)
        {
            try
            {
                return await ReadAndValidateAsync(aggregateFiles[0], ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or PlanTransitionException)
            {
                if (backupFiles.Length == 0)
                    throw;
                _logger?.LogWarning(
                    ex,
                    "Primary Plan aggregate {Path} is invalid; loading last-known-good backup {BackupPath}.",
                    aggregateFiles[0],
                    backupFiles[0]);
            }
        }

        if (backupFiles.Length > 1)
            throw new PlanTransitionException($"Session '{sessionId}' contains multiple Plan aggregate backups.");
        return await ReadAndValidateAsync(backupFiles[0], ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PlanWorkflow>> LoadRecoverableExecutionAsync(
        CancellationToken ct = default)
    {
        if (!Directory.Exists(_plansRoot))
            return [];

        List<PlanWorkflow> workflows = [];
        foreach (var file in Directory.EnumerateFiles(_plansRoot, "aggregate.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var aggregate = await ReadAndValidateAsync(file, ct).ConfigureAwait(false);
                if (aggregate.Workflow.State is PlanWorkflowState.StartingExecution
                    or PlanWorkflowState.Executing
                    or PlanWorkflowState.Verifying)
                {
                    workflows.Add(aggregate.Workflow);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or PlanTransitionException)
            {
                // 后台恢复扫描每 5 秒遍历一次磁盘：同一损坏文件只报告一次，
                // 否则 WARNING（含完整 JSON 异常堆栈）会以 5 秒周期刷屏。
                if (_reportedUnreadable.Add(file))
                {
                    if (_reportedUnreadable.Count > MaxReportedUnreadableEntries)
                        _reportedUnreadable.Clear();
                    _logger?.LogWarning(ex, "Skipping unreadable Plan aggregate {Path}", file);
                }
                else
                {
                    _logger?.LogDebug(ex, "Skipping unreadable Plan aggregate {Path} (already reported)", file);
                }
            }
        }

        return workflows
            .OrderBy(workflow => workflow.State == PlanWorkflowState.StartingExecution
                ? workflow.NextRetryAt ?? DateTimeOffset.MinValue
                : DateTimeOffset.MinValue)
            .ThenBy(workflow => workflow.UpdatedAt)
            .ToArray();
    }

    public Task SaveAsync(
        PlanAggregate aggregate,
        long expectedVersion,
        CancellationToken ct = default)
        => SaveCoreAsync(aggregate, expectedVersion, requiredFencingToken: null, isClaim: false, ct);

    public async Task<PlanAggregate> ClaimWorkflowAsync(
        SessionId sessionId,
        PlanWorkflowId planId,
        long fencingToken,
        long expectedVersion,
        CancellationToken ct = default)
    {
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));
        var current = await LoadAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new PlanTransitionException($"Plan aggregate '{planId}' was not found in session '{sessionId}'.");
        if (current.Workflow.Id != planId)
        {
            throw new PlanTransitionException(
                $"Plan aggregate in session '{sessionId}' belongs to workflow '{current.Workflow.Id}', not '{planId}'.");
        }
        if (current.Workflow.Version != expectedVersion)
        {
            throw new PlanConcurrencyException(
                $"Plan aggregate version conflict for '{planId}': expected {expectedVersion}, actual {current.Workflow.Version}.");
        }
        if (current.Workflow.WorkflowFencingToken is { } existing && fencingToken <= existing)
        {
            throw new PlanConcurrencyException(
                $"Plan workflow '{planId}' fencing token must increase monotonically (current {existing}, attempted {fencingToken}).");
        }

        var claimed = current with
        {
            Workflow = current.Workflow with
            {
                WorkflowFencingToken = fencingToken,
                Version = current.Workflow.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };
        await SaveCoreAsync(claimed, expectedVersion, fencingToken, isClaim: true, ct).ConfigureAwait(false);
        return await LoadAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new PlanTransitionException($"Plan aggregate '{planId}' disappeared after workflow claim.");
    }

    public Task SaveFencedAsync(
        PlanAggregate aggregate,
        long expectedVersion,
        long fencingToken,
        CancellationToken ct = default)
    {
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));
        return SaveCoreAsync(aggregate, expectedVersion, fencingToken, isClaim: false, ct);
    }

    private async Task SaveCoreAsync(
        PlanAggregate aggregate,
        long expectedVersion,
        long? requiredFencingToken,
        bool isClaim,
        CancellationToken ct)
    {
        ValidateAggregate(aggregate);
        var path = GetAggregatePath(aggregate.Workflow.SessionId, aggregate.Workflow.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var lease = await AcquireLeaseAsync(path + ".lock", ct).ConfigureAwait(false);
        PlanAggregate? current = null;
        if (File.Exists(path))
            current = await ReadAndValidateAsync(path, ct).ConfigureAwait(false);

        var actualVersion = current?.Workflow.Version ?? -1;
        if (actualVersion != expectedVersion)
        {
            throw new PlanConcurrencyException(
                $"Plan aggregate version conflict for '{aggregate.Workflow.Id}': expected {expectedVersion}, actual {actualVersion}.");
        }

        // fencing 内核语义与其余三模式一致：
        // 已 claim 的聚合拒绝无令牌写入；fenced 写要求磁盘令牌与候选令牌一致。
        WorkflowFencing.Validate(
            current?.Workflow.WorkflowFencingToken,
            aggregate.Workflow.WorkflowFencingToken,
            requiredFencingToken,
            isClaim,
            "PlanWorkflow");

        var envelope = CreateEnvelope(aggregate);
        var content = JsonSerializer.Serialize(envelope, _jsonOptions);
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var backupPath = path + ".bak";
        try
        {
            await WriteThroughAsync(tempPath, content, ct).ConfigureAwait(false);
            _ = await ReadAndValidateAsync(tempPath, ct).ConfigureAwait(false);

            if (File.Exists(path))
                File.Copy(path, backupPath, overwrite: true);
            File.Move(tempPath, path, overwrite: true);

            var persisted = await ReadAndValidateAsync(path, ct).ConfigureAwait(false);
            if (persisted.Workflow.Version != aggregate.Workflow.Version)
                throw new IOException($"Plan aggregate write verification failed for '{aggregate.Workflow.Id}'.");

            try
            {
                await WriteMarkdownProjectionAsync(persisted, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Markdown is a rebuildable projection. The aggregate commit is
                // authoritative, so projection failure must not make the caller
                // retry with a stale ExpectedVersion.
                _logger?.LogWarning(
                    ex,
                    "Plan aggregate {Path} committed, but Markdown projection failed.",
                    path);
            }
        }
        catch
        {
            if (File.Exists(backupPath))
                File.Copy(backupPath, path, overwrite: true);
            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private async Task<PlanAggregate> ReadAndValidateAsync(string path, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<PlanAggregateEnvelope>(json, _jsonOptions)
            ?? throw new PlanTransitionException($"Plan aggregate '{path}' is empty or malformed.");
        if (envelope.SchemaVersion is not (1 or SchemaVersion))
        {
            // 不识别的 schema 一律 fail-closed（持久化兼容红线）。
            throw new PlanTransitionException($"Plan aggregate '{path}' uses unsupported schema {envelope.SchemaVersion}.");
        }

        var payloadJson = JsonSerializer.Serialize(envelope.Aggregate, _jsonOptions);
        var checksum = ComputeChecksum(payloadJson);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(checksum),
                Encoding.ASCII.GetBytes(envelope.Checksum)))
        {
            throw new PlanTransitionException($"Plan aggregate checksum validation failed for '{path}'.");
        }

        ValidateAggregate(envelope.Aggregate);
        return envelope.Aggregate;
    }

    private static void ValidateAggregate(PlanAggregate aggregate)
    {
        var workflow = aggregate.Workflow;
        if (aggregate.Revisions.Any(revision => revision.PlanId != workflow.Id || revision.SessionId != workflow.SessionId))
            throw new PlanTransitionException("Plan aggregate contains a revision owned by another Plan or session.");

        var revisionNumbers = aggregate.Revisions.Select(revision => revision.Revision).Order().ToArray();
        if (revisionNumbers.Distinct().Count() != revisionNumbers.Length)
            throw new PlanTransitionException("Plan aggregate contains duplicate revision numbers.");
        if (revisionNumbers.Length != workflow.LatestRevision
            || revisionNumbers.Where((revision, index) => revision != index + 1).Any())
        {
            throw new PlanTransitionException(
                $"Plan aggregate revision sequence does not match LatestRevision {workflow.LatestRevision}.");
        }

        if (workflow.SubmittedRevision is { } submitted)
        {
            var submittedRevision = aggregate.FindRevision(submitted)
                ?? throw new PlanTransitionException("Submitted revision is missing from the aggregate.");
            var validStatus = submittedRevision.Status == PlanRevisionStatus.Submitted
                || (submittedRevision.Status == PlanRevisionStatus.Approved
                    && workflow.ApprovedRevision == submitted);
            if (!validStatus)
                throw new PlanTransitionException("Submitted revision has an invalid lifecycle status.");
        }
        if (workflow.ApprovedSnapshot is { } snapshot)
        {
            var revision = aggregate.FindRevision(snapshot.Revision)
                ?? throw new PlanTransitionException("Approved snapshot revision is missing from the aggregate.");
            if (!string.Equals(revision.ContentHash, snapshot.ContentHash, StringComparison.Ordinal))
                throw new PlanTransitionException("Approved snapshot content hash does not match its revision.");
        }
    }

    private async Task WriteMarkdownProjectionAsync(PlanAggregate aggregate, CancellationToken ct)
    {
        foreach (var revision in aggregate.Revisions)
        {
            var path = GetRevisionMarkdownPath(aggregate.Workflow.SessionId, aggregate.Workflow.Id, revision.Revision);
            await File.WriteAllTextAsync(path, revision.Markdown, ct).ConfigureAwait(false);
        }
    }

    public string GetRevisionMarkdownPath(SessionId sessionId, PlanWorkflowId planId, int revision)
        => Path.Combine(
            GetSessionDirectory(sessionId),
            planId.ToString(),
            $"revision-{revision:0000}.md");

    private static async Task<FileStream> AcquireLeaseAsync(string lockPath, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + LockTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(LockRetryDelayMilliseconds, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteThroughAsync(string path, string content, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private PlanAggregateEnvelope CreateEnvelope(PlanAggregate aggregate)
    {
        var payloadJson = JsonSerializer.Serialize(aggregate, _jsonOptions);
        return new PlanAggregateEnvelope(SchemaVersion, ComputeChecksum(payloadJson), aggregate);
    }

    private static string ComputeChecksum(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private string GetSessionDirectory(SessionId sessionId)
        => Path.Combine(_plansRoot, sessionId.ToString());

    private string GetAggregatePath(SessionId sessionId, PlanWorkflowId planId)
        => Path.Combine(GetSessionDirectory(sessionId), planId.ToString(), "aggregate.json");

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record PlanAggregateEnvelope(
        int SchemaVersion,
        string Checksum,
        PlanAggregate Aggregate);
}
