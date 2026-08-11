using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using OneCode.Core.Workflows;
using OneCode.Infrastructure;

namespace OneCode.App.Services.Agent;

/// <summary>Creates one exclusively owned MAF checkpoint store per durable workflow run.</summary>
public interface IWorkflowCheckpointStoreFactory
{
    Task<WorkflowCheckpointStoreHandle> OpenAsync(
        WorkflowRunRecord run,
        JsonSerializerOptions serializerOptions,
        CancellationToken ct = default);
}

/// <summary>
/// Owns both the MAF checkpoint manager and its process-exclusive file store.
/// The handle must remain alive for the complete workflow execution or resume operation.
/// </summary>
public sealed class WorkflowCheckpointStoreHandle : IAsyncDisposable
{
    private readonly FileSystemJsonCheckpointStore _store;

    internal WorkflowCheckpointStoreHandle(
        string runId,
        DirectoryInfo directory,
        FileSystemJsonCheckpointStore store,
        CheckpointManager manager)
    {
        RunId = runId;
        Directory = directory;
        _store = store;
        Manager = manager;
    }

    public string RunId { get; }
    public DirectoryInfo Directory { get; }
    public CheckpointManager Manager { get; }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Production factory for MAF's built-in JSON checkpoint store. It never discovers runs by
/// scanning checkpoint directories; callers must supply a durable active Registry record.
/// </summary>
public sealed class WorkflowCheckpointStoreFactory : IWorkflowCheckpointStoreFactory
{
    private readonly string _root;

    public WorkflowCheckpointStoreFactory(string? basePath = null)
    {
        _root = basePath ?? Path.Combine(PathsHelper.GetUserConfigDir(), "workflow-checkpoints");
        Directory.CreateDirectory(_root);
    }

    public Task<WorkflowCheckpointStoreHandle> OpenAsync(
        WorkflowRunRecord run,
        JsonSerializerOptions serializerOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ct.ThrowIfCancellationRequested();
        if (run.State != WorkflowRunState.Active)
            throw new InvalidOperationException($"Workflow run '{run.RunId}' is not active.");
        if (string.IsNullOrWhiteSpace(run.DefinitionHash))
            throw new InvalidOperationException($"Workflow run '{run.RunId}' has no DefinitionHash.");

        var directory = new DirectoryInfo(Path.Combine(_root, SafeName(run.RunId)));
        directory.Create();
        var store = new FileSystemJsonCheckpointStore(directory);
        try
        {
            var manager = CheckpointManager.CreateJson(store, serializerOptions);
            return Task.FromResult(new WorkflowCheckpointStoreHandle(run.RunId, directory, store, manager));
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private static string SafeName(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
