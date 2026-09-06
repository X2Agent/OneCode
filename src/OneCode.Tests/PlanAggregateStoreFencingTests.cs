using Microsoft.Extensions.Logging;
using OneCode.App.Services.PlanMode;
using OneCode.Core.Domain;
using OneCode.Core.PlanMode;

namespace OneCode.Tests;

/// <summary>
/// Plan fencing 接入的持久化行为锚：
/// v1 信封兼容读、未知 schema fail-closed、claim/fenced 写内核语义与其余三模式一致。
/// </summary>
public sealed class PlanAggregateStoreFencingTests : IDisposable
{
    private readonly string _root;

    public PlanAggregateStoreFencingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "onecode-plan-fencing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ClaimWorkflowAsync_SetsToken_AndBumpsVersion()
    {
        var store = new PlanAggregateStore(_root);
        var (workflow, _) = await CreatePersistedAsync(store);
        var token = DateTimeOffset.UtcNow.UtcTicks;

        var claimed = await store.ClaimWorkflowAsync(
            workflow.SessionId, workflow.Id, token, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        claimed.Workflow.WorkflowFencingToken.Should().Be(token);
        claimed.Workflow.Version.Should().Be(workflow.Version + 1);
        var reloaded = await store.LoadAsync(workflow.SessionId, TestContext.Current.CancellationToken);
        reloaded!.Workflow.WorkflowFencingToken.Should().Be(token);
    }

    [Fact]
    public async Task ClaimWorkflowAsync_WithStaleToken_FailsClosed()
    {
        var store = new PlanAggregateStore(_root);
        var (workflow, _) = await CreatePersistedAsync(store);
        var firstToken = DateTimeOffset.UtcNow.UtcTicks;

        await store.ClaimWorkflowAsync(
            workflow.SessionId, workflow.Id, firstToken, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        var act = () => store.ClaimWorkflowAsync(
            workflow.SessionId, workflow.Id, firstToken, expectedVersion: workflow.Version + 1,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PlanConcurrencyException>();
    }

    [Fact]
    public async Task SaveAsync_RejectsUnfencedWrite_AfterClaim()
    {
        var store = new PlanAggregateStore(_root);
        var (workflow, aggregate) = await CreatePersistedAsync(store);
        var token = DateTimeOffset.UtcNow.UtcTicks;
        await store.ClaimWorkflowAsync(
            workflow.SessionId, workflow.Id, token, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        var act = () => store.SaveAsync(
            aggregate with
            {
                Workflow = aggregate.Workflow with { Version = workflow.Version + 2 },
            },
            expectedVersion: workflow.Version + 1,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PlanWorkflow*");
    }

    [Fact]
    public async Task SaveFencedAsync_WithMatchingToken_Persists()
    {
        var store = new PlanAggregateStore(_root);
        var (workflow, aggregate) = await CreatePersistedAsync(store);
        var token = DateTimeOffset.UtcNow.UtcTicks;
        await store.ClaimWorkflowAsync(
            workflow.SessionId, workflow.Id, token, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        var fenced = aggregate with
        {
            Workflow = aggregate.Workflow with
            {
                WorkflowFencingToken = token,
                Version = workflow.Version + 2,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };
        await store.SaveFencedAsync(fenced, expectedVersion: workflow.Version + 1, token,
            TestContext.Current.CancellationToken);

        var reloaded = await store.LoadAsync(workflow.SessionId, TestContext.Current.CancellationToken);
        reloaded!.Workflow.Version.Should().Be(workflow.Version + 2);
        reloaded.Workflow.WorkflowFencingToken.Should().Be(token);
    }

    [Fact]
    public async Task SaveFencedAsync_WithWrongToken_FailsClosed()
    {
        var store = new PlanAggregateStore(_root);
        var (workflow, aggregate) = await CreatePersistedAsync(store);
        var token = DateTimeOffset.UtcNow.UtcTicks;
        await store.ClaimWorkflowAsync(
            workflow.SessionId, workflow.Id, token, expectedVersion: workflow.Version,
            TestContext.Current.CancellationToken);

        var act = () => store.SaveFencedAsync(
            aggregate with { Workflow = aggregate.Workflow with { Version = workflow.Version + 2 } },
            expectedVersion: workflow.Version + 1,
            fencingToken: token + 1,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PlanWorkflow*");
    }

    [Fact]
    public async Task LoadAsync_AcceptsV1Envelope_WithoutFencingField()
    {
        var store = new PlanAggregateStore(_root);
        var (workflow, _) = await CreatePersistedAsync(store);
        RewriteSchemaVersion(GetAggregatePath(workflow), from: 2, to: 1);

        var reloaded = await store.LoadAsync(workflow.SessionId, TestContext.Current.CancellationToken);

        reloaded.Should().NotBeNull();
        reloaded!.Workflow.Id.Should().Be(workflow.Id);
        reloaded.Workflow.WorkflowFencingToken.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WithUnknownSchema_FailsClosed()
    {
        var store = new PlanAggregateStore(_root);
        var (workflow, _) = await CreatePersistedAsync(store);
        RewriteSchemaVersion(GetAggregatePath(workflow), from: 2, to: 3);

        var act = () => store.LoadAsync(workflow.SessionId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PlanTransitionException>()
            .WithMessage("*unsupported schema*");
    }

    [Fact]
    public async Task LoadRecoverableExecutionAsync_WarnsUnreadableAggregate_OnlyOnce()
    {
        // 模拟旧 schema 残留（永远无法反序列化）：后台恢复扫描每 5 秒遍历一次磁盘，
        // 同一损坏文件只允许记一次 WARNING，后续扫描降级为 Debug，避免日志刷屏。
        var corruptPath = Path.Combine(
            _root, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "aggregate.json");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        await File.WriteAllTextAsync(corruptPath, "{\"schemaVersion\":99}");

        var logger = new CapturingLogger();
        var store = new PlanAggregateStore(_root, logger);

        await store.LoadRecoverableExecutionAsync(TestContext.Current.CancellationToken);
        await store.LoadRecoverableExecutionAsync(TestContext.Current.CancellationToken);

        logger.Entries.Count(e => e.Level == LogLevel.Warning).Should().Be(1);
        logger.Entries.Count(e => e.Level == LogLevel.Debug).Should().Be(1);
    }

    private async Task<(PlanWorkflow Workflow, PlanAggregate Aggregate)> CreatePersistedAsync(
        PlanAggregateStore store)
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateWorkflow(sessionId);
        var aggregate = new PlanAggregate(workflow, [CreateRevision(workflow)]);
        await store.SaveAsync(aggregate, expectedVersion: -1, TestContext.Current.CancellationToken);
        return (workflow, aggregate);
    }

    private static PlanWorkflow CreateWorkflow(SessionId sessionId)
    {
        var created = PlanWorkflow.Create(sessionId);
        return created with
        {
            State = PlanWorkflowState.Executing,
            Version = 3,
            LatestRevision = 1,
            SubmittedRevision = 1,
            ApprovedRevision = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static PlanRevision CreateRevision(PlanWorkflow workflow)
        => new()
        {
            PlanId = workflow.Id,
            SessionId = workflow.SessionId,
            Revision = 1,
            Title = "Test plan",
            Markdown = "# Test plan",
            Steps = [],
            Risks = [],
            Assumptions = [],
            ContentHash = "sha256-test",
            Status = PlanRevisionStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private string GetAggregatePath(PlanWorkflow workflow)
        => Path.Combine(_root, workflow.SessionId.ToString(), workflow.Id.ToString(), "aggregate.json");

    /// <summary>信封 checksum 只覆盖聚合 payload，改写 schemaVersion 不影响校验，
    /// 可用于模拟 v1 旧文件与未知 schema 文件。</summary>
    private static void RewriteSchemaVersion(string path, int from, int to)
    {
        var content = File.ReadAllText(path);
        var rewritten = content.Replace(
            $"\"schemaVersion\": {from}",
            $"\"schemaVersion\": {to}",
            StringComparison.Ordinal);
        File.WriteAllText(path, rewritten);
    }

    private sealed class CapturingLogger : ILogger<PlanAggregateStore>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
