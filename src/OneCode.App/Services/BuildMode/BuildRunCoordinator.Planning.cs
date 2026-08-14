using OneCode.Core.Build;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// Plan/task/evidence helpers split out of <see cref="BuildRunCoordinator"/>
/// into a partial. These manipulate BuildPlan / BuildTask / evidence (linking,
/// completion, recovery, delivery-manifest) and are orthogonal to the run
/// state-machine/lifecycle methods kept in the main file.
/// </summary>
public sealed partial class BuildRunCoordinator
{

    private BuildPlan LinkPlanTasks(BuildRun run, BuildPlan plan)
    {
        var linked = new Dictionary<string, TaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var planTask in TopologicalOrder(plan.Tasks))
        {
            var dependencies = planTask.DependsOn
                .Select(id => linked[id].Id)
                .ToArray();
            linked[planTask.Id] = FindOrCreatePlanTask(run, planTask, dependencies);
        }

        return plan with
        {
            Tasks = plan.Tasks.Select(planTask => planTask with
            {
                Status = planTask.DependsOn.Count == 0
                    ? BuildTaskStatus.InProgress
                    : BuildTaskStatus.Pending,
                Evidence = planTask.Evidence ?? [],
                TaskItemId = linked[planTask.Id].Id,
            }).ToArray(),
        };
    }

    private TaskItem FindOrCreatePlanTask(
        BuildRun run,
        BuildPlanTask planTask,
        IReadOnlyList<string> blockedBy)
    {
        var existing = taskService.ListTasks(
                conversationId: run.ConversationId?.ToString(),
                buildRunId: run.Id.ToString(),
                exactScope: true)
            .SingleOrDefault(task =>
                task.Metadata?.ExtraProperties?.TryGetValue("BuildPlanTaskId", out var mappedId) == true
                && string.Equals(mappedId, planTask.Id, StringComparison.Ordinal));
        if (existing is not null)
            return existing;

        return taskService.CreateTask(
            planTask.Title,
            planTask.Description,
            $"Executing {planTask.Title}",
            status: blockedBy.Count == 0 ? TaskStatus.InProgress : TaskStatus.Pending,
            blockedBy: blockedBy,
            metadata: new TaskMetadata(
                ExtraProperties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BuildPlanTaskId"] = planTask.Id,
                }),
            conversationId: run.ConversationId?.ToString(),
            buildRunId: run.Id.ToString());
    }

    private static BuildPlan CreateQuickFixPlan(BuildScopeSnapshot scope)
        => new(
            "Execute the confirmed Build scope and produce deterministic validation evidence.",
            [new BuildPlanTask(
                "implementation",
                "Implement confirmed scope",
                scope.Goal,
                [],
                [],
                scope.AcceptanceCriteria.Select(item => item.Id).ToArray())],
            [],
            [],
            scope.OutOfScope);

    private static IReadOnlyList<BuildPlanTask> TopologicalOrder(
        IReadOnlyList<BuildPlanTask> tasks)
    {
        var byId = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<BuildPlanTask>(tasks.Count);

        void Visit(BuildPlanTask task)
        {
            if (!visited.Add(task.Id))
                return;
            foreach (var dependency in task.DependsOn)
                Visit(byId[dependency]);
            ordered.Add(task);
        }

        foreach (var task in tasks)
            Visit(task);
        return ordered;
    }

    private static bool PlansMatch(BuildPlan persisted, BuildPlan prescribed)
        => string.Equals(persisted.Summary, prescribed.Summary, StringComparison.Ordinal)
            && persisted.RequireExplicitTaskCompletion == prescribed.RequireExplicitTaskCompletion
            && persisted.Tasks.Select(TaskIdentity)
                .SequenceEqual(prescribed.Tasks.Select(TaskIdentity));

    private static string TaskIdentity(BuildPlanTask task)
        => string.Join(
            "\u001f",
            task.Id,
            task.Title,
            task.Description,
            string.Join("\u001e", task.DependsOn),
            string.Join("\u001e", task.ExpectedFiles),
            string.Join("\u001e", task.AcceptanceCriteria));

    private static BuildScopeSnapshot CreateScope(
        string prompt,
        string confirmedBy,
        DateTimeOffset now,
        BuildPlan? prescribedPlan = null)
    {
        var taskAcceptance = prescribedPlan?.Tasks
            .SelectMany(task => task.AcceptanceCriteria.Select((criterion, index) =>
                new AcceptanceCriterion(
                    $"task:{task.Id}:{index}",
                    criterion,
                    Required: true)))
            .ToArray() ?? [];
        IReadOnlyList<AcceptanceCriterion> acceptance = taskAcceptance.Length > 0
            ? [.. taskAcceptance, new AcceptanceCriterion(
                "final-validation",
                "Final deterministic validation passes after the last file change.",
                Required: true)]
            : [new AcceptanceCriterion(
                "final-validation",
                "Final deterministic validation passes after the last file change.",
                Required: true)];
        return new BuildScopeSnapshot(
            prompt.Trim(),
            [prompt.Trim()],
            [],
            ["Preserve repository conventions and pass final validation."],
            acceptance,
            confirmedBy,
            now);
    }

    private static void ValidateChangedFileAttribution(BuildRun run)
    {
        var tasks = run.Plan?.Tasks
            ?? throw new InvalidOperationException("BuildRun has no plan for file attribution.");
        if (tasks.Count <= 1)
            return;

        foreach (var changedFile in run.ChangedFiles)
        {
            var matches = tasks.Count(task => task.ExpectedFiles.Any(expected =>
                PathsMatch(expected, changedFile)));
            if (matches != 1)
            {
                throw new InvalidOperationException(
                    matches == 0
                        ? $"Changed file '{changedFile}' is not attributed to any BuildPlan task."
                        : $"Changed file '{changedFile}' is ambiguously attributed to {matches} BuildPlan tasks.");
            }
        }
    }

    private BuildRun MarkExecutionEvidencePassed(BuildRun run)
    {
        var plan = run.Plan ?? throw new InvalidOperationException("BuildRun has no plan to complete.");
        var validationEvidence = run.Validations[^1].Evidence.Select((evidence, index) =>
            new BuildTaskEvidence(
                BuildEvidenceKind.Validation,
                $"{run.Validations[^1].Id}:{index}",
                evidence)).ToArray();
        var singleTaskPlan = plan.Tasks.Count == 1;

        var completedTasks = plan.Tasks.Select(planTask =>
        {
            var taskItem = GetLinkedTask(run, planTask);
            if (plan.RequireExplicitTaskCompletion && taskItem.Status != TaskStatus.Completed)
            {
                throw new InvalidOperationException(
                    $"BuildPlan task '{planTask.Id}' has not been explicitly completed by its execution protocol.");
            }
            if (!plan.RequireExplicitTaskCompletion && taskItem.Status != TaskStatus.Completed)
            {
                if (!taskService.UpdateTask(taskItem.Id, status: TaskStatus.Completed))
                {
                    throw new InvalidOperationException(
                        $"Linked task '{taskItem.Id}' for BuildPlan task '{planTask.Id}' could not be completed.");
                }

                taskItem = GetLinkedTask(run, planTask);
            }

            var fileEvidence = run.ChangedFiles
                .Where(file => singleTaskPlan || planTask.ExpectedFiles.Any(expected =>
                    PathsMatch(expected, file)))
                .Select(file => new BuildTaskEvidence(
                    BuildEvidenceKind.FileChange,
                    file,
                    $"File changed for BuildPlan task {planTask.Id}."));
            var toolEvidence = run.ToolBatches.SelectMany(batch => batch.Calls
                .Where(call => singleTaskPlan || ToolCallBelongsToTask(call, planTask))
                .Select(call => new BuildTaskEvidence(
                    BuildEvidenceKind.ToolCall,
                    call.CallId,
                    $"{call.ToolName} completed in batch {batch.BatchId}.")));
            var persistedEvidence = ReadProjectedEvidence(taskItem)
                .Select((item, index) => new BuildTaskEvidence(
                    BuildEvidenceKind.Acceptance,
                    $"{taskItem.Id}:projection:{index}",
                    item));
            var evidence = new List<BuildTaskEvidence>
            {
                new(
                    BuildEvidenceKind.TaskCompletion,
                    taskItem.Id,
                    $"Persistent task '{taskItem.Id}' completed for BuildPlan task '{planTask.Id}'."),
            };
            evidence.AddRange(fileEvidence);
            evidence.AddRange(toolEvidence);
            evidence.AddRange(persistedEvidence);
            evidence.AddRange(validationEvidence);

            return planTask with
            {
                Status = BuildTaskStatus.Completed,
                Evidence = evidence,
            };
        }).ToArray();

        var evidenceByTask = completedTasks.ToDictionary(
            task => task.Id,
            task => task.CompletionEvidence,
            StringComparer.Ordinal);
        var scope = run.Scope! with
        {
            AcceptanceCriteria = run.Scope!.AcceptanceCriteria.Select(item =>
            {
                if (TryParseTaskAcceptanceId(item.Id, out var taskId)
                    && evidenceByTask.TryGetValue(taskId, out var taskEvidence))
                {
                    var mapped = taskEvidence.FirstOrDefault(evidence =>
                            evidence.Kind == BuildEvidenceKind.Acceptance)
                        ?? taskEvidence.FirstOrDefault(evidence =>
                            evidence.Kind == BuildEvidenceKind.FileChange)
                        ?? taskEvidence.FirstOrDefault(evidence =>
                            evidence.Kind == BuildEvidenceKind.ToolCall);
                    if (mapped is null)
                    {
                        throw new InvalidOperationException(
                            $"Acceptance criterion '{item.Id}' has no task-specific evidence for BuildPlan task '{taskId}'.");
                    }

                    return item with
                    {
                        Status = AcceptanceStatus.Passed,
                        Evidence = mapped.Summary,
                    };
                }

                return item with
                {
                    Status = AcceptanceStatus.Passed,
                    Evidence = run.Validations[^1].Evidence.FirstOrDefault() ?? "Final validation passed.",
                };
            }).ToArray(),
        };
        return run with { Plan = plan with { Tasks = completedTasks }, Scope = scope };
    }

    private static IReadOnlyList<string> ReadProjectedEvidence(TaskItem task)
        => (task.OutputLog ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("[projection:", StringComparison.Ordinal))
            .Select(line =>
            {
                var markerEnd = line.IndexOf("] ", StringComparison.Ordinal);
                return markerEnd >= 0 ? line[(markerEnd + 2)..] : line;
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool TryParseTaskAcceptanceId(string acceptanceId, out string taskId)
    {
        taskId = string.Empty;
        if (!acceptanceId.StartsWith("task:", StringComparison.Ordinal))
            return false;
        var separator = acceptanceId.LastIndexOf(':');
        if (separator <= "task:".Length)
            return false;
        taskId = acceptanceId["task:".Length..separator];
        return !string.IsNullOrWhiteSpace(taskId);
    }

    private static bool ToolCallBelongsToTask(
        OneCode.Core.Tools.CompletedToolCallRecord call,
        BuildPlanTask task)
    {
        if (string.Equals(call.ToolName, "UpdatePlanStep", StringComparison.OrdinalIgnoreCase)
            && TryReadStringArgument(call.ArgumentsJson, "stepId", out var stepId))
        {
            return string.Equals(stepId, task.Id, StringComparison.Ordinal);
        }

        return task.ExpectedFiles.Any(expected =>
            call.ArgumentsJson.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadStringArgument(
        string argumentsJson,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && property.GetString() is { } parsed)
            {
                value = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool PathsMatch(string expected, string changed)
        => string.Equals(
            expected.Replace('\\', '/').TrimStart('.', '/'),
            changed.Replace('\\', '/').TrimStart('.', '/'),
            StringComparison.OrdinalIgnoreCase);

    private TaskItem GetLinkedTask(BuildRun run, BuildPlanTask planTask)
    {
        if (string.IsNullOrWhiteSpace(planTask.TaskItemId))
        {
            throw new InvalidOperationException(
                $"BuildPlan task '{planTask.Id}' has no persistent TaskItem mapping.");
        }

        var taskItem = taskService.GetTask(planTask.TaskItemId)
            ?? throw new InvalidOperationException(
                $"Persistent task '{planTask.TaskItemId}' for BuildPlan task '{planTask.Id}' was not found.");
        var expectedConversationId = run.ConversationId?.ToString();
        if (!string.Equals(taskItem.ConversationId, expectedConversationId, StringComparison.Ordinal)
            || !string.Equals(taskItem.BuildRunId, run.Id.ToString(), StringComparison.Ordinal)
            || taskItem.Metadata?.ExtraProperties?.TryGetValue("BuildPlanTaskId", out var mappedTaskId) != true
            || !string.Equals(mappedTaskId, planTask.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Persistent task '{taskItem.Id}' does not belong to BuildPlan task '{planTask.Id}' in BuildRun '{run.Id}'.");
        }

        return taskItem;
    }

    private BuildRun ResetLinkedTasksForRecovery(BuildRun run)
    {
        if (run.Plan is null)
            return run;

        var tasks = run.Plan.Tasks.Select(planTask =>
        {
            if (string.IsNullOrWhiteSpace(planTask.TaskItemId))
                return planTask;

            var taskItem = GetLinkedTask(run, planTask);
            if (taskItem.Status == TaskStatus.Completed)
            {
                return planTask with
                {
                    Status = BuildTaskStatus.Completed,
                    Evidence = planTask.Evidence ?? [],
                };
            }

            var targetStatus = taskItem.BlockedBy.All(dependencyId =>
                taskService.GetTask(dependencyId)?.Status == TaskStatus.Completed)
                    ? TaskStatus.InProgress
                    : TaskStatus.Pending;
            var projected = taskService.ProjectTaskStatus(taskItem.Id, targetStatus);
            if (!projected.Succeeded)
            {
                throw new InvalidOperationException(
                    projected.Error
                    ?? $"Persistent task '{taskItem.Id}' could not be reset for BuildRun recovery.");
            }

            return planTask with
            {
                Status = targetStatus == TaskStatus.InProgress
                    ? BuildTaskStatus.InProgress
                    : BuildTaskStatus.Pending,
                Evidence = [],
            };
        }).ToArray();

        return run with { Plan = run.Plan with { Tasks = tasks } };
    }

    private void MarkLinkedTasksTerminal(BuildRun run, TaskStatus status)
    {
        foreach (var planTask in run.Plan?.Tasks ?? [])
        {
            if (string.IsNullOrWhiteSpace(planTask.TaskItemId))
                continue;

            var taskItem = taskService.GetTask(planTask.TaskItemId);
            if (taskItem is null
                || taskItem.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Cancelled)
            {
                continue;
            }

            _ = taskService.UpdateTask(taskItem.Id, status: status);
        }
    }

    private static BuildDeliveryManifest CreateDeliveryManifest(BuildRun run) =>
        new(
            run.ChangedFiles,
            run.Plan!.Tasks
                .Where(task => task.Status == BuildTaskStatus.Completed)
                .Select(task => task.Id)
                .ToArray(),
            run.Validations.SelectMany(validation => validation.Evidence).ToArray(),
            run.Scope!.AcceptanceCriteria
                .Where(item => !string.IsNullOrWhiteSpace(item.Evidence))
                .Select(item => item.Evidence!)
                .ToArray(),
            run.Plan.Risks,
            DateTimeOffset.UtcNow);
}