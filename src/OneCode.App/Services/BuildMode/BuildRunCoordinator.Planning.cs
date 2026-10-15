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

    // internal：BuildResumePolicy（恢复策略函数，Runtime 层参考实现）
    // 复用同一计划比对规则；prescribed plan 可能来自 ApprovedPlanSnapshot.Project() 投影
    // （快照为唯一定义源，BuildRun.Plan 为派生缓存）。
    internal static bool PlansMatch(BuildPlan persisted, BuildPlan prescribed)
        => string.Equals(persisted.Summary, prescribed.Summary, StringComparison.Ordinal)
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

    private BuildRun ValidateChangedFileAttribution(BuildRun run)
    {
        var tasks = run.Plan?.Tasks
            ?? throw new InvalidOperationException("BuildRun has no plan for file attribution.");

        // 剔除磁盘上已不存在的改动文件：临时文件（验证脚本等）在 run 内被 shell 清理是
        // 常规工作流，事务快照不感知删除。残留 key 会让归属判定把已清理的临时文件当成
        // 无归属改动（历史事故：_verify.ps1 删除后仍留在 ChangedFiles，整 run 被判失败，
        // dispose 回滚又连带删掉了正式产物 HTML）。
        var existingFiles = run.ChangedFiles.Where(ChangedFileExists).ToArray();
        if (existingFiles.Length != run.ChangedFiles.Count)
        {
            var removed = run.ChangedFiles.Except(existingFiles, StringComparer.OrdinalIgnoreCase).ToArray();
            logger.LogWarning(
                "BuildRun {RunId}: dropped {Count} deleted changed files from attribution: {Files}",
                run.Id, removed.Length, string.Join(", ", removed));
            run = run with { ChangedFiles = existingFiles };
        }

        if (tasks.Count <= 1)
            return run;

        // 无归属文件降级为警告证据，不再判失败：可能是模型顺手产生的辅助文件，构建成败
        // 由任务完成与验证证据决定；警告进最终验证证据（收尾保存时落盘），可审计可追踪。
        var unattributed = existingFiles
            .Where(changedFile => tasks.All(task => !task.ExpectedFiles.Any(expected =>
                PathsMatch(expected, changedFile))))
            .ToArray();
        if (unattributed.Length > 0)
        {
            logger.LogWarning(
                "BuildRun {RunId}: {Count} changed files are not attributed to any BuildPlan task: {Files}",
                run.Id, unattributed.Length, string.Join(", ", unattributed));
            var warnings = unattributed
                .Select(file => $"Unattributed changed file: {file}")
                .ToArray();
            var lastValidation = run.Validations[^1];
            run = run with
            {
                Validations =
                [
                    // IReadOnlyList 不支持 Range 切片，用 SkipLast(1) 取除末项外的全部验证记录
                    .. run.Validations.SkipLast(1),
                    lastValidation with { Evidence = [.. lastValidation.Evidence, .. warnings] },
                ],
            };
        }

        return run;
    }

    /// <summary>改动文件是否仍在磁盘上。相对路径按当前工作目录解析（与写入工具同一基准）。</summary>
    private static bool ChangedFileExists(string file) => File.Exists(file);

    /// <summary>
    /// 交付物是否已在磁盘落地。相对路径锚定 run 工作目录解析（与写入工具同一基准），
    /// 不依赖进程 CWD——受控 attempt 的宿主进程工作目录与 run 工作目录不必然一致。
    /// </summary>
    private static bool ExpectedFileExists(BuildRun run, string expected)
        => run.WorkingDirectory is { } workingDirectory && !Path.IsPathRooted(expected)
            ? File.Exists(Path.Combine(workingDirectory, expected))
            : File.Exists(expected);

    /// <summary>
    /// 校验计划声明的交付物落盘状态：
    /// - 当计划声明了 <c>ExpectedFiles</c>，但**全部**预期交付物均未在磁盘落地且整个运行期间没有任何文件改动
    ///   （<c>ChangedFiles.Count == 0</c>）时，判定为「零交付物」（例如模型仅输出反问或口头总结即停机），
    ///   抛出 <see cref="InvalidOperationException"/> 由外层转 <see cref="BuildRunState.Blocked"/> 阻断假阳性完成；
    /// - 当已有文件改动或部分交付物已落盘（模型可能以不同文件名落盘）时，缺失项仅记为可审计警告证据。
    /// </summary>
    private BuildRun RecordMissingDeliverableWarnings(BuildRun run)
    {
        var expectedFiles = run.Plan!.Tasks
            .SelectMany(task => task.ExpectedFiles)
            .Where(expected => !string.IsNullOrWhiteSpace(expected))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (expectedFiles.Length == 0)
            return run;

        var missingFiles = expectedFiles
            .Where(expected => !ExpectedFileExists(run, expected))
            .ToArray();
        if (missingFiles.Length == 0)
            return run;

        if (missingFiles.Length == expectedFiles.Length && run.ChangedFiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"Planned deliverables were not produced and no files were modified: {string.Join(", ", missingFiles)}");
        }

        logger.LogWarning(
            "BuildRun {RunId}: planned deliverables not found on disk: {Files}",
            run.Id, string.Join(", ", missingFiles));
        var lastValidation = run.Validations[^1];
        return run with
        {
            Validations =
            [
                .. run.Validations.SkipLast(1),
                lastValidation with
                {
                    Evidence = [.. lastValidation.Evidence, .. missingFiles.Select(file => $"Planned deliverable not found: {file}")],
                },
            ],
        };
    }

    private BuildRun MarkExecutionEvidencePassed(BuildRun run)
    {
        var plan = run.Plan ?? throw new InvalidOperationException("BuildRun has no plan to complete.");
        run = RecordMissingDeliverableWarnings(run);

        var validationEvidence = run.Validations[^1].Evidence.Select((evidence, index) =>
            new BuildTaskEvidence(
                BuildEvidenceKind.Validation,
                $"{run.Validations[^1].Id}:{index}",
                evidence)).ToArray();
        var singleTaskPlan = plan.Tasks.Count == 1;

        var completedTasks = plan.Tasks.Select(planTask =>
        {
            var taskItem = taskLinker.GetLinkedTask(run, planTask);
            if (taskItem.Status != TaskStatus.Completed)
            {
                // 补账：守卫通过后，剩余未上报任务要么交付物已落地（显式协议）、要么协议
                // 未要求显式上报——统一由代码推进持久任务完成，不依赖模型记账。
                taskLinker.CompleteLinkedTask(run, planTask);
                taskItem = taskLinker.GetLinkedTask(run, planTask);
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
                            evidence.Kind == BuildEvidenceKind.ToolCall)
                        // 纯调研/无落盘变更的任务没有专属证据：退到最终验证证据，
                        // 再退到补账产生的任务完成证据（恒存在）。验收不得因缺少口头记账而 Blocked。
                        ?? taskEvidence.FirstOrDefault(evidence =>
                            evidence.Kind == BuildEvidenceKind.Validation)
                        ?? taskEvidence.First(evidence =>
                            evidence.Kind == BuildEvidenceKind.TaskCompletion);

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
        => task.ExpectedFiles.Any(expected =>
            call.ArgumentsJson.Contains(expected, StringComparison.OrdinalIgnoreCase));

    private static bool PathsMatch(string expected, string changed)
        => string.Equals(
            expected.Replace('\\', '/').TrimStart('.', '/'),
            changed.Replace('\\', '/').TrimStart('.', '/'),
            StringComparison.OrdinalIgnoreCase);

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