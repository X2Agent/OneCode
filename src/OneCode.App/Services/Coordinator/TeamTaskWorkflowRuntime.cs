using OneCode.Core.Coordinator;
using OneCode.Core.Errors;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Production runtime for the Team task DAG. Binds one claimed TeamRun and its fencing
/// token, then executes each approved task as an idempotent unit through
/// <see cref="TeamWorkflowRunner"/> while persisting task state transitions through
/// <see cref="TeamRunApplicationService"/> with the bound token. The shared
/// <see cref="EditTransaction"/> covers the whole run; the completion phase
/// (quality gates + commit/rollback) is driven by the caller after the DAG ends.
/// </summary>
internal sealed class TeamTaskWorkflowRuntime(
    TeamRunApplicationService runService,
    ITeamTaskWorkflowRunner workflowRunner,
    TeamConfig config,
    string workingDirectory,
    Action<OrchestrationEvent>? eventSink,
    IReadOnlyList<string>? imagePaths,
    Func<EditTransaction> transactionFactory,
    OneCode.Core.Workflows.IOperationLedger? ledger = null) : ITeamTaskWorkflowRuntime, IDisposable
{
    private TeamRun? _run;
    private long _fencingToken;
    private EditTransaction? _transaction;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private string? _runOperationId;

    /// <summary>Whether <see cref="BindAsync"/> has bound this runtime to a claimed run.</summary>
    public bool IsBound => _run is not null;

    /// <summary>The run-scoped shared transaction; created during <see cref="BindAsync"/>.</summary>
    public EditTransaction Transaction
        => _transaction ?? throw new InvalidOperationException("Team runtime is not bound to a run.");

    /// <summary>The durable Operation Ledger OperationId bound to this run's file transaction.</summary>
    public string? RunOperationId => _runOperationId;

    /// <summary>The fencing token bound at lease acquisition.</summary>
    public long FencingToken
        => _run is null
            ? throw new InvalidOperationException("Team runtime is not bound to a run.")
            : _fencingToken;

    public TeamRun BoundRun
        => _run ?? throw new InvalidOperationException("Team runtime is not bound to a run.");

    public async Task BindAsync(TeamRun run, long fencingToken, CancellationToken ct)
    {
        if (_run is not null)
            throw new InvalidOperationException("Team runtime instances cannot be rebound to another run.");
        _run = run;
        _fencingToken = fencingToken;
        _transaction = transactionFactory();

        // S-04: 持久化 Operation Ledger——新世代前回滚本 Run 上一世代遗留的未提交文件副作用，
        // 然后开启 run 级持久化事务；task 内每次文件编辑的 intent 由 EditTransactionMiddleware 落盘。
        if (ledger is not null)
        {
            await ledger.ReconcileRunAsync($"team/{run.Id}", ct).ConfigureAwait(false);
            _runOperationId = $"team/{run.Id}/fence/{fencingToken}";
            await ledger.BeginTransactionAsync(
                _runOperationId,
                "file-transaction",
                fencingToken,
                ct).ConfigureAwait(false);
            _transaction.PersistTo(ledger, _runOperationId, fencingToken);
        }
    }

    public async Task<TeamRunResult> ExecuteTaskAsync(TeamTaskDefinition task, CancellationToken ct)
    {
        await _executionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var run = BoundRun;
            _run = await runService.StartTaskAsync(run.Id, task.Id, _fencingToken, ct).ConfigureAwait(false);
        }
        finally
        {
            _executionGate.Release();
        }

        // C1: run 级共享事务的快照跨任务累积；以本任务开始前的版本为界，
        // 越界检查只归属本任务新增的改动，前序任务的合法写入不再被误判。
        var changeVersion = Transaction.CaptureChangeVersion();

        TeamRunResult result;
        try
        {
            result = await workflowRunner.RunTaskAsync(
                config,
                task,
                Transaction,
                workingDirectory,
                eventSink,
                ct,
                imagePaths).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a resumable interruption. Leave the task Running so
            // the next execution generation can retry it from the business aggregate.
            Transaction.Rollback();
            throw;
        }

        var outOfScope = FindOutOfScopeChanges(task, changeVersion);
        if (outOfScope.Count > 0)
        {
            Transaction.Rollback();
            result = result with
            {
                HadFailures = true,
                Error = AgentProblemDetails.ToolExecutionFailed(
                    $"Task '{task.Id}' modified paths outside its approved scope: {string.Join(", ", outOfScope)}.",
                    toolName: "TeamTaskExecution"),
            };
        }

        await _executionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = BoundRun;
            _run = await runService.CompleteTaskAsync(
                current.Id, task.Id, result, _fencingToken, ct).ConfigureAwait(false);
        }
        finally
        {
            _executionGate.Release();
        }
        return result;
    }

    private IReadOnlyList<string> FindOutOfScopeChanges(TeamTaskDefinition task, long changeVersion)
    {
        if (task.AllowedPaths is not { Count: > 0 })
            return [];

        var roots = task.AllowedPaths
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(workingDirectory, path)))
            .Select(EnsureTrailingSeparator)
            .ToArray();
        return Transaction.GetModifiedFilesSince(changeVersion)
            .Select(Path.GetFullPath)
            .Where(path => !roots.Any(root =>
                path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    public void Dispose()
    {
        (_transaction as IDisposable)?.Dispose();
        _executionGate.Dispose();
    }
}
