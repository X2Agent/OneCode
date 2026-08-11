using OneCode.Core.Build;
using OneCode.Core.Goals;

namespace OneCode.App.Services.GoalMode;

public interface IGoalRunApplicationService
{
    Task<GoalRun?> GetAsync(GoalRunId runId, CancellationToken ct = default);
    Task<GoalRun?> GetBySessionAsync(SessionId sessionId, CancellationToken ct = default);

    Task<GoalRun> BeginAsync(
        SessionId sessionId,
        string goal,
        string workingDirectory,
        string modelId,
        string systemPromptHash,
        string toolCapabilityHash,
        CancellationToken ct = default);
}

public sealed class GoalRunApplicationService(
    IGoalRunStore store,
    IGoalWorkspaceService workspaceService,
    IWorkspaceFingerprintProvider fingerprintProvider) : IGoalRunApplicationService
{
    public Task<GoalRun?> GetAsync(GoalRunId runId, CancellationToken ct = default)
        => store.LoadByIdAsync(runId, ct);

    public Task<GoalRun?> GetBySessionAsync(SessionId sessionId, CancellationToken ct = default)
        => store.LoadBySessionAsync(sessionId, ct);

    public async Task<GoalRun> BeginAsync(
        SessionId sessionId,
        string goal,
        string workingDirectory,
        string modelId,
        string systemPromptHash,
        string toolCapabilityHash,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goal))
            throw new ArgumentException("Goal is required.", nameof(goal));
        var normalizedDirectory = Path.GetFullPath(workingDirectory);
        var existing = await store.LoadBySessionAsync(sessionId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.Goal, goal, StringComparison.Ordinal)
                || !string.Equals(Path.GetFullPath(existing.WorkingDirectory), normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Session '{sessionId}' already owns a different GoalRun.");
            }
            // Workspace drift check: recompute the fingerprint and reject recovery if the
            // workspace has changed since the run was created. This is a fail-closed safety
            // invariant per design §6.4: "恢复前和提交前校验工作区漂移".
            var currentFingerprint = await fingerprintProvider.ComputeAsync(normalizedDirectory, ct).ConfigureAwait(false);
            if (!string.Equals(existing.WorkspaceFingerprint, currentFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Workspace fingerprint drift detected for GoalRun '{existing.Id}': "
                    + $"stored='{existing.WorkspaceFingerprint}', current='{currentFingerprint}'. "
                    + "Recovery is rejected to prevent inconsistent state.");
            var expectedHash = GoalWorkflowCompiler.ComputeDefinitionHash(
                existing, modelId, systemPromptHash, toolCapabilityHash);
            if (!string.Equals(existing.DefinitionHash, expectedHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Goal workflow definition changed for an existing run.");
            return existing;
        }

        var fingerprint = await fingerprintProvider.ComputeAsync(normalizedDirectory, ct).ConfigureAwait(false);
        var provisional = new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = sessionId,
            Goal = goal,
            WorkingDirectory = normalizedDirectory,
            WorkspaceFingerprint = fingerprint,
            DefinitionHash = "pending",
        };
        var workspace = await workspaceService.PrepareAsync(provisional, ct).ConfigureAwait(false);
        var withWorkspace = provisional with { Workspace = workspace };
        var run = withWorkspace with
        {
            DefinitionHash = GoalWorkflowCompiler.ComputeDefinitionHash(
                withWorkspace, modelId, systemPromptHash, toolCapabilityHash),
        };
        await store.SaveAsync(run, expectedVersion: 0, ct).ConfigureAwait(false);
        return await store.LoadByIdAsync(run.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GoalRun '{run.Id}' disappeared after creation.");
    }
}
