using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Agents.AI.Workflows.InProc;
using Microsoft.Extensions.AI;

namespace OneCode.Tests.TestSupport;

/// <summary>
/// Cross-process MAF workflow probe used by <see cref="CrossProcessCheckpointRecoveryTests"/>,
/// <see cref="RunLeaseFencingTests"/>, and <see cref="CheckpointStoreFaultInjectionTests"/>.
///
/// Invoked as a separate process via <c>dotnet exec OneCode.Tests.dll --probe &lt;args&gt;</c>
/// to validate cross-process checkpoint recovery, lease fencing, and store fault tolerance.
/// </summary>
internal static class WorkflowRecoveryProbe
{
    private const string CustomScenario = "custom";
    private const string SequentialScenario = "sequential";
    private const string GroupChatScenario = "groupchat";
    private const string MagenticScenario = "magentic";
    private const string SideEffectScenario = "sideeffect";
    private const string StoreScenario = "store";
    private const string LeaseScenario = "lease";
    private const string WorkflowName = "onecode-cross-process-recovery-probe-v1";
    private const string StartId = "probe-start-v1";
    private const string StateId = "probe-state-v1";
    private const string RequestPortId = "probe-approval-v1";
    private const string FinishId = "probe-finish-v1";
    private const string SharedScope = "probe-shared-v1";
    private const string SharedKey = "shared-count";
    private const string ExecutorStateKey = "probe-executor-count";
    private const string RequestId = "probe-request-v1";
    private static DirectoryInfo? s_sideEffectRoot;
    private static bool s_crashAfterSideEffect;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5
            || args[0] is not ("write" or "resume" or "resume-wrong-request" or "resume-wrong-port"
                or "resume-duplicate" or "resume-wrong-session" or "resume-wrong-checkpoint" or "resume-crash"
                or "store-hold" or "store-open" or "lease-hold" or "lease-try" or "lease-acquire"
                or "lease-write" or "lease-complete" or "lease-cleanup")
            || args[1] is not (CustomScenario or SequentialScenario or GroupChatScenario or MagenticScenario
                or SideEffectScenario or StoreScenario or LeaseScenario))
        {
            Console.Error.WriteLine(
                "Usage: OneCode.Tests.dll --probe <write|resume|resume-wrong-request|resume-wrong-port|resume-duplicate|resume-wrong-session|resume-wrong-checkpoint|resume-crash|store-hold|store-open|lease-hold|lease-try|lease-acquire|lease-write|lease-complete|lease-cleanup> <custom|sequential|groupchat|magentic|sideeffect|store|lease> <storeDirectory> <sessionId> <resultFile>");
            return 64;
        }

        try
        {
            var mode = args[0];
            var scenario = args[1];
            var storeDirectory = new DirectoryInfo(Path.GetFullPath(args[2]));
            var sessionId = args[3];
            var resultFile = Path.GetFullPath(args[4]);
            storeDirectory.Create();
            Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
            s_sideEffectRoot = storeDirectory.Parent ?? storeDirectory;
            s_crashAfterSideEffect = mode == "resume-crash";

            if (mode is "store-hold" or "store-open")
                return await RunStoreOwnershipProbeAsync(mode, storeDirectory, resultFile).ConfigureAwait(false);
            if (mode.StartsWith("lease-", StringComparison.Ordinal))
                return await RunLeaseProbeAsync(mode, storeDirectory, sessionId, resultFile).ConfigureAwait(false);

            var result = mode == "write"
                ? await WriteCheckpointAsync(scenario, storeDirectory, sessionId).ConfigureAwait(false)
                : await ResumeCheckpointAsync(mode, scenario, storeDirectory, sessionId).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                resultFile,
                JsonSerializer.Serialize(result, ProbeJsonContext.Default.ProbeResult)).ConfigureAwait(false);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> RunStoreOwnershipProbeAsync(
        string mode,
        DirectoryInfo storeDirectory,
        string resultFile)
    {
        using var store = new FileSystemJsonCheckpointStore(storeDirectory);
        await File.WriteAllTextAsync(resultFile, "opened").ConfigureAwait(false);
        if (mode == "store-hold")
        {
            var releasePath = resultFile + ".release";
            while (!File.Exists(releasePath))
                await Task.Delay(20).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> RunLeaseProbeAsync(
        string mode,
        DirectoryInfo root,
        string runId,
        string resultFile)
    {
        var registry = new ProbeRunLeaseRegistry(root);
        switch (mode)
        {
            case "lease-hold":
            {
                using var lease = registry.Acquire(runId);
                await WriteLeaseResultAsync(resultFile, true, lease.FencingToken, "Active").ConfigureAwait(false);
                var releasePath = resultFile + ".release";
                while (!File.Exists(releasePath))
                    await Task.Delay(20).ConfigureAwait(false);
                return 0;
            }
            case "lease-try":
            {
                using var lease = registry.TryAcquire(runId);
                var acquired = lease is not null;
                await WriteLeaseResultAsync(
                    resultFile,
                    acquired,
                    lease?.FencingToken ?? 0,
                    acquired ? "Active" : "Contended").ConfigureAwait(false);
                return acquired ? 0 : 2;
            }
            case "lease-acquire":
            {
                using var lease = registry.Acquire(runId);
                await WriteLeaseResultAsync(resultFile, true, lease.FencingToken, "Active").ConfigureAwait(false);
                return 0;
            }
            case "lease-write":
            {
                var tokenHint = ReadTokenHint(resultFile);
                registry.WriteEvidence(runId, tokenHint, "evidence");
                await WriteLeaseResultAsync(resultFile, true, tokenHint, "EvidenceWritten").ConfigureAwait(false);
                return 0;
            }
            case "lease-complete":
            {
                var tokenHint = ReadTokenHint(resultFile);
                registry.Complete(runId, tokenHint);
                await WriteLeaseResultAsync(resultFile, true, tokenHint, "Completed").ConfigureAwait(false);
                return 0;
            }
            case "lease-cleanup":
            {
                var tokenHint = ReadTokenHint(resultFile);
                registry.Cleanup(runId, tokenHint);
                await WriteLeaseResultAsync(resultFile, true, tokenHint, "Completed").ConfigureAwait(false);
                return 0;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static long ReadTokenHint(string resultFile)
    {
        var tokenPath = resultFile + ".token";
        return File.Exists(tokenPath)
            ? long.Parse(File.ReadAllText(tokenPath), CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Lease token file '{tokenPath}' is missing.");
    }

    private static async Task WriteLeaseResultAsync(string path, bool acquired, long token, string state)
    {
        var temporaryPath = path + $".{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(new LeaseProbeResult(acquired, token, state))).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task<ProbeResult> WriteCheckpointAsync(
        string scenario,
        DirectoryInfo storeDirectory,
        string sessionId)
    {
        using var store = new FileSystemJsonCheckpointStore(storeDirectory);
        var manager = CheckpointManager.CreateJson(store, ProbeJsonContext.Default.Options);
        var environment = InProcessExecution.Lockstep.WithCheckpointing(manager);
        var definition = BuildDefinition(scenario);

        await using var run = scenario == MagenticScenario
            ? await StartMagenticRunAsync(environment, definition, sessionId).ConfigureAwait(false)
            : await StartRunAsync(environment, definition, sessionId).ConfigureAwait(false);

        ExternalRequest? pendingRequest = null;
        object? output = null;
        var observedEvents = new List<string>();
        await foreach (var workflowEvent in run.WatchStreamAsync(blockOnPendingRequest: false).ConfigureAwait(false))
        {
            observedEvents.Add(workflowEvent.GetType().Name);
            if (workflowEvent is RequestInfoEvent requestInfo)
                pendingRequest = requestInfo.Request;
            else if (workflowEvent is WorkflowOutputEvent outputEvent)
                output = outputEvent.Data;
            else if (workflowEvent is WorkflowErrorEvent workflowError)
                throw new InvalidOperationException($"Scenario '{scenario}' workflow failed.", workflowError.Exception);
            else if (workflowEvent is ExecutorFailedEvent executorFailed)
                throw new InvalidOperationException(
                    $"Scenario '{scenario}' executor '{executorFailed.ExecutorId}' failed: {executorFailed.Data}");
        }

        if (run.Checkpoints.Count == 0)
            throw new InvalidOperationException($"Scenario '{scenario}' did not persist a checkpoint.");
        if (scenario is CustomScenario or MagenticScenario or SideEffectScenario && pendingRequest is null)
        {
            throw new InvalidOperationException(
                $"Scenario '{scenario}' did not emit its expected pending request. Events: {string.Join(", ", observedEvents)}");
        }

        var checkpoint = scenario is SequentialScenario or GroupChatScenario
            ? run.Checkpoints[0]
            : run.LastCheckpoint!;
        await File.WriteAllTextAsync(GetSelectionPath(storeDirectory, sessionId), checkpoint.CheckpointId)
            .ConfigureAwait(false);

        return CreateResult(
            success: true,
            phase: "write",
            scenario,
            definition.DefinitionHash,
            checkpoint,
            pendingRequest,
            output,
            executorCount: null,
            sharedCount: null);
    }

    private static async ValueTask<StreamingRun> StartMagenticRunAsync(
        InProcessExecutionEnvironment environment,
        ProbeDefinition definition,
        string sessionId)
    {
        var run = await environment.OpenStreamingAsync(definition.Workflow, sessionId, CancellationToken.None)
            .ConfigureAwait(false);
        var messages = (List<ChatMessage>)definition.Input;
        _ = await run.TrySendMessageAsync(messages).ConfigureAwait(false);
        _ = await run.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false);
        return run;
    }

    private static ValueTask<StreamingRun> StartRunAsync(
        InProcessExecutionEnvironment environment,
        ProbeDefinition definition,
        string sessionId)
        => definition.Input switch
        {
            ProbeStart start => environment.RunStreamingAsync(definition.Workflow, start, sessionId),
            ChatMessage message => environment.RunStreamingAsync(definition.Workflow, message, sessionId),
            List<ChatMessage> messages => environment.RunStreamingAsync(definition.Workflow, messages, sessionId),
            _ => throw new InvalidOperationException($"Unsupported probe input type '{definition.Input.GetType()}'."),
        };

    private static async Task<ProbeResult> ResumeCheckpointAsync(
        string mode,
        string scenario,
        DirectoryInfo storeDirectory,
        string sessionId)
    {
        using var store = new FileSystemJsonCheckpointStore(storeDirectory);
        var manager = CheckpointManager.CreateJson(store, ProbeJsonContext.Default.Options);
        var selectedCheckpointId = await File.ReadAllTextAsync(GetSelectionPath(storeDirectory, sessionId))
            .ConfigureAwait(false);
        var checkpoint = mode switch
        {
            "resume-wrong-session" => new CheckpointInfo(sessionId + "-wrong", selectedCheckpointId),
            "resume-wrong-checkpoint" => new CheckpointInfo(sessionId, selectedCheckpointId + "-wrong"),
            _ => new CheckpointInfo(sessionId, selectedCheckpointId),
        };
        var environment = InProcessExecution.Lockstep.WithCheckpointing(manager);
        var definition = BuildDefinition(scenario);

        await using var run = await environment
            .ResumeStreamingAsync(definition.Workflow, checkpoint, CancellationToken.None)
            .ConfigureAwait(false);

        ExternalRequest? replayedRequest = null;
        object? output = null;
        await foreach (var workflowEvent in run.WatchStreamAsync(blockOnPendingRequest: false).ConfigureAwait(false))
        {
            if (workflowEvent is RequestInfoEvent requestInfo)
                replayedRequest = requestInfo.Request;
            else if (workflowEvent is WorkflowOutputEvent outputEvent)
                output = outputEvent.Data;
            else if (workflowEvent is WorkflowErrorEvent workflowError)
                throw new InvalidOperationException($"Restored scenario '{scenario}' workflow failed.", workflowError.Exception);
            else if (workflowEvent is ExecutorFailedEvent executorFailed)
                throw new InvalidOperationException(
                    $"Restored scenario '{scenario}' executor '{executorFailed.ExecutorId}' failed: {executorFailed.Data}");
        }

        if (scenario is CustomScenario or SideEffectScenario)
        {
            if (replayedRequest is null)
                throw new InvalidOperationException("The restored custom workflow did not replay the pending request.");
            var response = mode switch
            {
                "resume-wrong-request" => new ExternalResponse(
                    replayedRequest.PortInfo,
                    replayedRequest.RequestId + "-wrong",
                    new PortableValue(new ProbeApproval(true))),
                "resume-wrong-port" => new ExternalResponse(
                    new RequestPortInfo(
                        replayedRequest.PortInfo.RequestType,
                        replayedRequest.PortInfo.ResponseType,
                        replayedRequest.PortInfo.PortId + "-wrong"),
                    replayedRequest.RequestId,
                    new PortableValue(new ProbeApproval(true))),
                _ => replayedRequest.CreateResponse(new ProbeApproval(true)),
            };
            await run.SendResponseAsync(response).ConfigureAwait(false);
            if (mode == "resume-duplicate")
                await run.SendResponseAsync(response).ConfigureAwait(false);
            output = await DrainForOutputAsync(run).ConfigureAwait(false);
        }
        else if (scenario == MagenticScenario)
        {
            if (replayedRequest is null || !replayedRequest.IsDataOfType<MagenticPlanReviewRequest>())
                throw new InvalidOperationException("The restored Magentic workflow did not replay its plan review request.");
            await run.SendResponseAsync(replayedRequest.CreateResponse(new MagenticPlanReviewResponse([])))
                .ConfigureAwait(false);
            output = await DrainForOutputAsync(run).ConfigureAwait(false);
        }

        var customOutput = output as ProbeOutput;
        var success = scenario switch
        {
            CustomScenario or SideEffectScenario => customOutput is { ExecutorCount: 1, SharedCount: 1, Approved: true },
            MagenticScenario => output is List<ChatMessage> messages
                && messages.Any(message => message.Text?.Contains("magentic-final", StringComparison.Ordinal) == true),
            SequentialScenario or GroupChatScenario => true,
            _ => false,
        };

        return CreateResult(
            success,
            "resume",
            scenario,
            definition.DefinitionHash,
            checkpoint,
            replayedRequest,
            output,
            customOutput?.ExecutorCount,
            customOutput?.SharedCount);
    }

    private static async Task<object?> DrainForOutputAsync(StreamingRun run)
    {
        object? output = null;
        await foreach (var workflowEvent in run.WatchStreamAsync(blockOnPendingRequest: false).ConfigureAwait(false))
        {
            if (workflowEvent is WorkflowOutputEvent outputEvent)
                output = outputEvent.Data;
        }
        return output;
    }

    private static ProbeDefinition BuildDefinition(string scenario)
        => scenario switch
        {
            CustomScenario => BuildCustomDefinition(),
            SequentialScenario => BuildSequentialDefinition(),
            GroupChatScenario => BuildGroupChatDefinition(),
            MagenticScenario => BuildMagenticDefinition(),
            SideEffectScenario => BuildSideEffectDefinition(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static ProbeDefinition BuildCustomDefinition()
    {
        var start = new ProbeStartExecutor(StartId);
        var state = new ProbeStateExecutor(StateId);
        var requestPort = RequestPort.Create<ProbeApprovalRequest, ProbeApproval>(RequestPortId);
        var request = requestPort.BindAsExecutor();
        var finish = new ProbeFinishExecutor(FinishId);
        var workflow = new WorkflowBuilder(start)
            .WithName(WorkflowName)
            .AddEdge(start, state, "probe-edge-start-state-v1", false)
            .AddEdge(state, request, "probe-edge-state-request-v1", false)
            .AddEdge(request, finish, "probe-edge-request-finish-v1", false)
            .WithOutputFrom(finish)
            .Build(validateOrphans: true);
        return new ProbeDefinition(workflow, new ProbeStart("seed"), ComputeDefinitionHash(CustomScenario));
    }

    private static ProbeDefinition BuildSideEffectDefinition()
    {
        var start = new ProbeStartExecutor(StartId);
        var state = new ProbeStateExecutor(StateId);
        var requestPort = RequestPort.Create<ProbeApprovalRequest, ProbeApproval>(RequestPortId);
        var request = requestPort.BindAsExecutor();
        var effect = new ProbeSideEffectExecutor(FinishId);
        var workflow = new WorkflowBuilder(start)
            .WithName("onecode-side-effect-replay-probe-v1")
            .AddEdge(start, state, "probe-edge-start-state-v1", false)
            .AddEdge(state, request, "probe-edge-state-request-v1", false)
            .AddEdge(request, effect, "probe-edge-request-side-effect-v1", false)
            .WithOutputFrom(effect)
            .Build(validateOrphans: true);
        return new ProbeDefinition(workflow, new ProbeStart("seed"), ComputeDefinitionHash(SideEffectScenario));
    }

    private static ProbeDefinition BuildSequentialDefinition()
    {
        AIAgent[] agents =
        [
            new DeterministicAgent("sequential-agent-a", "SequentialA", AgentKind.Worker),
            new DeterministicAgent("sequential-agent-b", "SequentialB", AgentKind.Worker),
        ];
        var workflow = new SequentialWorkflowBuilder(agents).Build();
        return new ProbeDefinition(
            workflow,
            new ChatMessage(ChatRole.User, "sequential-probe"),
            ComputeDefinitionHash(SequentialScenario));
    }

    private static ProbeDefinition BuildGroupChatDefinition()
    {
        AIAgent[] agents =
        [
            new DeterministicAgent("group-agent-a", "GroupA", AgentKind.Worker),
            new DeterministicAgent("group-agent-b", "GroupB", AgentKind.Worker),
        ];
        var workflow = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                participants => new RoundRobinGroupChatManager(
                    participants,
                    (roundRobin, _, _) => ValueTask.FromResult(roundRobin.IterationCount >= 2)))
            .AddParticipants(agents)
            .WithName("groupchat-recovery-probe-v1")
            .Build();
        return new ProbeDefinition(
            workflow,
            new ChatMessage(ChatRole.User, "groupchat-probe"),
            ComputeDefinitionHash(GroupChatScenario));
    }

    private static ProbeDefinition BuildMagenticDefinition()
    {
        var manager = new DeterministicAgent("magentic-manager-v1", "MagenticManager", AgentKind.Manager);
        var worker = new DeterministicAgent("magentic-worker-v1", "MagenticWorker", AgentKind.Worker);
        var workflow = new MagenticWorkflowBuilder(manager)
            .AddParticipants([worker])
            .RequirePlanSignoff(true)
            .WithMaxRounds(2)
            .WithMaxStalls(1)
            .WithMaxResets(1)
            .Build();
        return new ProbeDefinition(
            workflow,
            new List<ChatMessage> { new(ChatRole.User, "magentic-probe") },
            ComputeDefinitionHash(MagenticScenario));
    }

    private static string ComputeDefinitionHash(string scenario)
    {
        var canonical = scenario switch
        {
            CustomScenario => string.Join('|', WorkflowName, StartId, StateId, RequestPortId, FinishId,
                SharedScope, SharedKey, ExecutorStateKey, typeof(ProbeStart).FullName,
                typeof(ProbeApprovalRequest).FullName, typeof(ProbeApproval).FullName, typeof(ProbeOutput).FullName),
            SequentialScenario => "sequential-v1|sequential-agent-a|sequential-agent-b|ChatMessage|AgentSession",
            GroupChatScenario => "groupchat-v1|group-agent-a|group-agent-b|RoundRobin|termination:iteration>=2",
            MagenticScenario => "magentic-v1|magentic-manager-v1|magentic-worker-v1|plan-review:true|max-rounds:2|max-stalls:1|max-resets:1",
            SideEffectScenario => "sideeffect-v1|run/task/attempt/file-write|request-port|receipt-ledger|sha256",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string GetSelectionPath(DirectoryInfo storeDirectory, string sessionId)
        => Path.Combine(storeDirectory.Parent?.FullName ?? storeDirectory.FullName, $"{sessionId}.selected-checkpoint");

    private static ProbeResult CreateResult(
        bool success,
        string phase,
        string scenario,
        string definitionHash,
        CheckpointInfo checkpoint,
        ExternalRequest? request,
        object? output,
        int? executorCount,
        int? sharedCount)
        => new(
            success,
            phase,
            scenario,
            Environment.ProcessId,
            definitionHash,
            checkpoint.SessionId,
            checkpoint.CheckpointId,
            request?.RequestId,
            request?.PortInfo.PortId,
            request is not null && request.TryGetDataAs<ProbeApprovalRequest>(out var approvalRequest)
                ? approvalRequest?.CommandId
                : null,
            executorCount,
            sharedCount,
            FormatOutput(output));

    private static string? FormatOutput(object? output)
        => output switch
        {
            null => null,
            ProbeOutput custom => JsonSerializer.Serialize(custom, ProbeJsonContext.Default.ProbeOutput),
            List<ChatMessage> messages => string.Join(" | ", messages.Select(message => $"{message.AuthorName}:{message.Text}")),
            _ => output.ToString(),
        };

    private sealed record ProbeDefinition(Workflow Workflow, object Input, string DefinitionHash);

    private sealed class ProbeStartExecutor(string id) : Executor<ProbeStart, ProbeStart>(id)
    {
        public override ValueTask<ProbeStart> HandleAsync(
            ProbeStart message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(message);
    }

    private sealed class ProbeStateExecutor(string id)
        : StatefulExecutor<ProbeExecutorState, ProbeStart, ProbeApprovalRequest>(
            id,
            static () => new ProbeExecutorState(0),
            new StatefulExecutorOptions { StateKey = ExecutorStateKey, ScopeName = SharedScope })
    {
        public override async ValueTask<ProbeApprovalRequest> HandleAsync(
            ProbeStart message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var state = await ReadStateAsync(context, skipCache: false, cancellationToken).ConfigureAwait(false);
            var nextExecutorCount = state.Count + 1;
            await QueueStateUpdateAsync(new ProbeExecutorState(nextExecutorCount), context, cancellationToken)
                .ConfigureAwait(false);
            var shared = await context.ReadOrInitStateAsync(
                SharedKey, static () => 0, SharedScope, cancellationToken).ConfigureAwait(false);
            await context.QueueStateUpdateAsync(
                SharedKey, shared + 1, SharedScope, cancellationToken).ConfigureAwait(false);
            return new ProbeApprovalRequest(RequestId, $"executor={nextExecutorCount};shared={shared + 1}");
        }
    }

    private sealed class ProbeFinishExecutor(string id) : Executor<ProbeApproval, ProbeOutput>(id)
    {
        public override async ValueTask<ProbeOutput> HandleAsync(
            ProbeApproval message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var executorState = await context.ReadStateAsync<ProbeExecutorState>(
                ExecutorStateKey, SharedScope, cancellationToken).ConfigureAwait(false);
            var sharedCount = await context.ReadStateAsync<int>(
                SharedKey, SharedScope, cancellationToken).ConfigureAwait(false);
            return new ProbeOutput(executorState?.Count ?? -1, sharedCount, message.Approved);
        }
    }

    private sealed class ProbeSideEffectExecutor(string id) : Executor<ProbeApproval, ProbeOutput>(id)
    {
        private const string OperationId = "sideeffect-run/sideeffect-task/attempt-1/file-write";

        public override async ValueTask<ProbeOutput> HandleAsync(
            ProbeApproval message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var root = s_sideEffectRoot
                ?? throw new InvalidOperationException("Side-effect probe root is not configured.");
            var ledger = new ProbeFileOperationLedger(root);
            var receipt = await ledger.ExecuteOnceAsync(
                OperationId,
                async ct =>
                {
                    var counterPath = Path.Combine(root.FullName, "side-effect-count.txt");
                    var count = File.Exists(counterPath)
                        ? int.Parse(await File.ReadAllTextAsync(counterPath, ct).ConfigureAwait(false), CultureInfo.InvariantCulture)
                        : 0;
                    await File.WriteAllTextAsync(counterPath, (count + 1).ToString(CultureInfo.InvariantCulture), ct)
                        .ConfigureAwait(false);
                    return SHA256.HashData(Encoding.UTF8.GetBytes((count + 1).ToString(CultureInfo.InvariantCulture)));
                },
                cancellationToken).ConfigureAwait(false);
            if (s_crashAfterSideEffect)
                Environment.FailFast("Simulated crash after side effect and before MAF checkpoint reconciliation.");

            var executorState = await context.ReadStateAsync<ProbeExecutorState>(
                ExecutorStateKey, SharedScope, cancellationToken).ConfigureAwait(false);
            var sharedCount = await context.ReadStateAsync<int>(
                SharedKey, SharedScope, cancellationToken).ConfigureAwait(false);
            return new ProbeOutput(executorState?.Count ?? -1, sharedCount, message.Approved, receipt.Replayed);
        }
    }

    private sealed class ProbeFileOperationLedger(DirectoryInfo root)
    {
        public async Task<ProbeOperationReceipt> ExecuteOnceAsync(
            string operationId,
            Func<CancellationToken, Task<byte[]>> action,
            CancellationToken ct)
        {
            var ledgerDirectory = Path.Combine(root.FullName, "operation-ledger");
            Directory.CreateDirectory(ledgerDirectory);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId))).ToLowerInvariant();
            var receiptPath = Path.Combine(ledgerDirectory, key + ".receipt");
            if (File.Exists(receiptPath))
                return new ProbeOperationReceipt(operationId, await File.ReadAllTextAsync(receiptPath, ct).ConfigureAwait(false), true);

            var result = await action(ct).ConfigureAwait(false);
            var resultHash = Convert.ToHexString(result).ToLowerInvariant();
            var temporaryPath = receiptPath + $".{Environment.ProcessId}.tmp";
            await File.WriteAllTextAsync(temporaryPath, resultHash, ct).ConfigureAwait(false);
            try
            {
                File.Move(temporaryPath, receiptPath, overwrite: false);
                return new ProbeOperationReceipt(operationId, resultHash, false);
            }
            catch (IOException) when (File.Exists(receiptPath))
            {
                File.Delete(temporaryPath);
                return new ProbeOperationReceipt(operationId, await File.ReadAllTextAsync(receiptPath, ct).ConfigureAwait(false), true);
            }
        }
    }

    private sealed record ProbeOperationReceipt(string OperationId, string ResultHash, bool Replayed);

    private sealed class ProbeRunLeaseRegistry(DirectoryInfo root)
    {
        public ProbeRunLease Acquire(string runId)
            => TryAcquire(runId)
                ?? throw new InvalidOperationException($"Run '{runId}' lease is already held.");

        public ProbeRunLease? TryAcquire(string runId)
        {
            Directory.CreateDirectory(root.FullName);
            var leasePath = Path.Combine(root.FullName, SafeName(runId) + ".lease");
            FileStream stream;
            try
            {
                stream = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return null;
            }

            try
            {
                var state = LoadState(runId);
                if (state.State == "Completed")
                    throw new InvalidOperationException($"Run '{runId}' is completed and cannot be acquired.");
                var next = state with { FencingToken = state.FencingToken + 1, State = "Active" };
                SaveState(next, state.FencingToken);
                return new ProbeRunLease(stream, next.FencingToken);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void WriteEvidence(string runId, long fencingToken, string evidence)
        {
            var state = LoadState(runId);
            EnsureCurrent(state, fencingToken);
            SaveState(state with { Evidence = [.. state.Evidence, evidence] }, fencingToken);
        }

        public void Complete(string runId, long fencingToken)
        {
            var state = LoadState(runId);
            if (state.State == "Completed")
            {
                if (state.FencingToken != fencingToken)
                    throw new InvalidOperationException("Stale fencing token.");
                return;
            }
            EnsureCurrent(state, fencingToken);
            SaveState(state with { State = "Completed" }, fencingToken);
        }

        public void Cleanup(string runId, long fencingToken)
        {
            var state = LoadState(runId);
            if (state.State != "Completed")
                throw new InvalidOperationException($"Run '{runId}' is not completed.");
            if (state.FencingToken != fencingToken)
                throw new InvalidOperationException("Stale fencing token.");
            var checkpointPath = Path.Combine(root.FullName, SafeName(runId) + ".checkpoint.tmp");
            if (File.Exists(checkpointPath))
                File.Delete(checkpointPath);
        }

        private ProbeRunState LoadState(string runId)
        {
            var statePath = StatePath(runId);
            if (!File.Exists(statePath))
                return new ProbeRunState(runId, 0, "Created", []);
            return JsonSerializer.Deserialize<ProbeRunState>(File.ReadAllText(statePath))
                ?? throw new InvalidDataException($"Run state '{statePath}' is empty.");
        }

        private void SaveState(ProbeRunState state, long expectedToken)
        {
            var current = LoadState(state.RunId);
            if (current.FencingToken != expectedToken)
                throw new InvalidOperationException("Stale fencing token.");
            var statePath = StatePath(state.RunId);
            var temporaryPath = statePath + $".{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            File.Move(temporaryPath, statePath, overwrite: true);
        }

        private static void EnsureCurrent(ProbeRunState state, long fencingToken)
        {
            if (state.State != "Active")
                throw new InvalidOperationException($"Run '{state.RunId}' is not active.");
            if (state.FencingToken != fencingToken)
                throw new InvalidOperationException("Stale fencing token.");
        }

        private string StatePath(string runId)
            => Path.Combine(root.FullName, SafeName(runId) + ".run.json");

        private static string SafeName(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed class ProbeRunLease(FileStream stream, long fencingToken) : IDisposable
    {
        public long FencingToken { get; } = fencingToken;
        public void Dispose() => stream.Dispose();
    }

    private sealed record ProbeRunState(string RunId, long FencingToken, string State, IReadOnlyList<string> Evidence);

    private enum AgentKind
    {
        Worker,
        Manager,
    }

    private sealed class DeterministicAgent(string id, string name, AgentKind kind) : AIAgent
    {
        protected override string IdCore => id;
        public override string? Name => name;
        public override string? Description => $"Deterministic {kind} recovery probe agent";

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            var state = session as ProbeAgentSession ?? new ProbeAgentSession();
            state.CallCount++;
            var lastPrompt = messages.LastOrDefault()?.Text ?? string.Empty;
            var text = kind == AgentKind.Manager
                ? CreateManagerResponse(lastPrompt)
                : $"{name}:call={state.CallCount}";
            var message = new ChatMessage(ChatRole.Assistant, text)
            {
                AuthorName = name,
                MessageId = $"{id}-{state.CallCount}",
            };
            return Task.FromResult(new AgentResponse(new ChatResponse(message)));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The deterministic probe agent is batch-only.");

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(new ProbeAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(JsonSerializer.SerializeToElement(
                (ProbeAgentSession)session,
                ProbeJsonContext.Default.ProbeAgentSession));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement json,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(
                json.Deserialize(ProbeJsonContext.Default.ProbeAgentSession) ?? new ProbeAgentSession());

        private static string CreateManagerResponse(string prompt)
        {
            if (prompt.Contains("is_request_satisfied", StringComparison.Ordinal))
            {
                return """
                    {
                      "is_request_satisfied": { "answer": true, "reason": "probe complete" },
                      "is_in_loop": { "answer": false, "reason": "single deterministic round" },
                      "is_progress_being_made": { "answer": true, "reason": "probe complete" },
                      "next_speaker": { "answer": "MagenticWorker", "reason": "stable worker" },
                      "instruction_or_question": { "answer": "complete probe", "reason": "deterministic" }
                    }
                    """;
            }
            if (prompt.Contains("final answer", StringComparison.OrdinalIgnoreCase))
                return "magentic-final";
            if (prompt.Contains("plan", StringComparison.OrdinalIgnoreCase))
                return "1. Approve the deterministic recovery probe plan.\n2. Complete after recovery.";
            return "Known fact: this is a deterministic recovery probe.";
        }
    }
}

internal sealed class ProbeAgentSession : AgentSession
{
    public int CallCount { get; set; }
}

internal sealed record ProbeStart(string Value);
internal sealed record ProbeExecutorState(int Count);
internal sealed record ProbeApprovalRequest(string CommandId, string Prompt);
internal sealed record ProbeApproval(bool Approved);
internal sealed record ProbeOutput(int ExecutorCount, int SharedCount, bool Approved, bool OperationReplayed = false);
internal sealed record LeaseProbeResult(bool Acquired, long FencingToken, string State);
internal sealed record ProbeResult(
    bool Success,
    string Phase,
    string Scenario,
    int ProcessId,
    string DefinitionHash,
    string SessionId,
    string CheckpointId,
    string? RequestId,
    string? PortId,
    string? CommandId,
    int? ExecutorCount,
    int? SharedCount,
    string? Output);

[JsonSerializable(typeof(ProbeStart))]
[JsonSerializable(typeof(ProbeExecutorState))]
[JsonSerializable(typeof(ProbeApprovalRequest))]
[JsonSerializable(typeof(ProbeApproval))]
[JsonSerializable(typeof(ProbeOutput))]
[JsonSerializable(typeof(ProbeAgentSession))]
[JsonSerializable(typeof(ProbeResult))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
