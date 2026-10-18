// BuildRunCoordinatorTests partial：运行完成与提交确认
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.Core.Build;
using OneCode.Core.Workflows;
using OneCode.Core.Domain;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;
namespace OneCode.Tests;

public sealed partial class BuildRunCoordinatorTests
{
    [Fact]
    public async Task CompleteAsync_PassingEvidence_ProducesCompletedRun()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        // 真实落盘的工作区文件：归属剔除会移除磁盘上不存在的改动文件，
        // 用假文件名会让 ChangedFiles 被清空、误判"零改动即提交"。
        var foo = CreateWorkspaceFile("Foo.cs");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: [foo]));
        var completed = await sut.ConfirmCommitAsync(accepting.Id);

        accepting.State.Should().Be(BuildRunState.Accepting);
        accepting.TransactionCommitted.Should().BeFalse();
        accepting.CommitWorkspaceFingerprint.Should().Be("fingerprint-a");
        completed.State.Should().Be(BuildRunState.Completed);
        completed.TransactionCommitted.Should().BeTrue();
        completed.Plan!.Tasks.Should().OnlyContain(task =>
            task.Status == BuildTaskStatus.Completed && task.CompletionEvidence.Count > 0);
        completed.DeliveryManifest.Should().NotBeNull();
        completed.DeliveryManifest!.ChangedFiles.Should().Contain(foo);
        completed.Scope!.AcceptanceCriteria.Should().OnlyContain(item =>
            item.Status == AcceptanceStatus.Passed && !string.IsNullOrWhiteSpace(item.Evidence));
    }

    [Fact]
    public async Task CompleteAsync_PersistentTaskCompletionBecomesTaskSpecificEvidence()
    {
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var foo = CreateWorkspaceFile("Foo.cs");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: [foo]));
        var completed = await sut.ConfirmCommitAsync(accepting.Id);

        var planTask = completed.Plan!.Tasks.Should().ContainSingle().Subject;
        tasks.GetTask(planTask.TaskItemId!)!.Status.Should().Be(TaskStatus.Completed);
        planTask.CompletionEvidence.Should().ContainSingle(evidence =>
            evidence.Kind == BuildEvidenceKind.TaskCompletion
            && evidence.Reference == planTask.TaskItemId);
    }

    [Fact]
    public async Task ConfirmCommitAsync_RepeatedCall_IsIdempotent()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var foo = CreateWorkspaceFile("Foo.cs");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);
        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: [foo]));

        var first = await sut.ConfirmCommitAsync(accepting.Id);
        var second = await sut.ConfirmCommitAsync(accepting.Id);

        first.State.Should().Be(BuildRunState.Completed);
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task ConfirmCommitAsync_WorkspaceChangedAfterValidation_BlocksRun()
    {
        var store = new InMemoryBuildRunStore();
        var provider = Substitute.For<IWorkspaceFingerprintProvider>();
        provider.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult("fingerprint-a"),
                Task.FromResult("fingerprint-a"),
                Task.FromResult("fingerprint-a"),
                Task.FromResult("fingerprint-b"));
        var sut = CreateCoordinator(store, provider);
        var foo = CreateWorkspaceFile("Foo.cs");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);
        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: [foo]));

        var blocked = await sut.ConfirmCommitAsync(accepting.Id);

        blocked.State.Should().Be(BuildRunState.Blocked);
        blocked.TransactionCommitted.Should().BeFalse();
        blocked.FailureSummary.Should().Contain("after final validation");
    }

    [Fact]
    public async Task CompleteAsync_CancelledRun_PersistsCancelledTerminalState()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var cancelled = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                null,
                0,
                0,
                1,
                TerminalReason: RunTerminalReason.Cancelled,
                TransactionRolledBack: true,
                FinalValidationStatus: BuildValidationStatus.Cancelled));

        cancelled.State.Should().Be(BuildRunState.Cancelled);
        cancelled.TerminalReason.Should().Be(RunTerminalReason.Cancelled);
        cancelled.TransactionRolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteAsync_AgentException_PersistsFailedTerminalState()
    {
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var failed = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                null,
                0,
                0,
                1,
                TerminalReason: RunTerminalReason.AgentException,
                TransactionRolledBack: true,
                FinalValidationStatus: BuildValidationStatus.Cancelled,
                ValidationFailureSummary: "provider failed"));

        failed.State.Should().Be(BuildRunState.Failed);
        failed.TerminalReason.Should().Be(RunTerminalReason.AgentException);
        failed.FailureSummary.Should().Be("provider failed");
    }

    [Fact]
    public async Task CompleteAsync_SkippedValidation_EntersAcceptingInsteadOfFailed()
    {
        // Skipped（验证不适用）不得把成功执行的任务误标为 Failed。
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var foo = CreateWorkspaceFile("Foo.cs");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs line 42 and run FooTests.",
            Path.GetTempPath());
        run = await ApprovePlanAsync(sut, run);

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                null,
                0,
                0,
                1,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Skipped,
                ModifiedFiles: [foo],
                ValidationFailureSummary: "No verification profile."));

        accepting.State.Should().Be(BuildRunState.Accepting);
        accepting.Validations[^1].Status.Should().Be(BuildValidationStatus.Skipped);
    }

    [Fact]
    public async Task CompleteAsync_DeletedTempFile_IsDroppedFromAttributionAndRunStaysOnTrack()
    {
        // 事故场景：验证脚本等临时文件在 run 内被 shell 清理，但仍留在 ModifiedFiles。
        // 归属判定必须剔除已删除文件，否则多任务计划整 run 被判失败、事务回滚连带
        // 删除正式产物。走真实协调器 + 真实磁盘文件（§3.4 契约测试要求）。
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var foo = CreateWorkspaceFile("Foo.cs");
        var fooTests = CreateWorkspaceFile("FooTests.cs");
        var verifyScript = Path.Combine(_root, "_verify.ps1"); // 模拟已被清理的临时脚本
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs and add FooTests.cs.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: CreateMultiTaskPlan(foo, fooTests));
        run = await ApprovePlanAsync(sut, run);

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: [foo, fooTests, verifyScript]));

        accepting.State.Should().Be(BuildRunState.Accepting, "已删除的临时文件不构成归属失败");
        accepting.ChangedFiles.Should().NotContain(verifyScript, "磁盘上不存在的文件必须从 ChangedFiles 剔除");
        accepting.ChangedFiles.Should().Contain(foo, fooTests);
    }

    [Fact]
    public async Task CompleteAsync_UnattributedExistingFile_AddsWarningEvidenceInsteadOfFailing()
    {
        // 无归属文件降级为警告证据：构建成败由任务完成与验证证据决定，警告必须进
        // 最终验证证据落盘（可审计），而不是把整 run 判失败。
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var foo = CreateWorkspaceFile("Foo.cs");
        var fooTests = CreateWorkspaceFile("FooTests.cs");
        var scratch = CreateWorkspaceFile("scratch-notes.md"); // 存在但无任务归属
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs and add FooTests.cs.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: CreateMultiTaskPlan(foo, fooTests));
        run = await ApprovePlanAsync(sut, run);

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: [foo, fooTests, scratch]));

        accepting.State.Should().Be(BuildRunState.Accepting, "无归属文件降级为警告，不判失败");
        accepting.Validations[^1].Evidence.Should().Contain(
            evidence => evidence.Contains("Unattributed changed file") && evidence.Contains("scratch-notes.md"),
            "警告必须写入最终验证证据");
        var completed = await sut.ConfirmCommitAsync(accepting.Id);
        completed.State.Should().Be(BuildRunState.Completed);
        completed.DeliveryManifest!.ChangedFiles.Should().Contain(scratch);
    }

    [Fact]
    public async Task CompleteAsync_UnreportedTaskWithDeliverableOnDisk_AutoCompletes()
    {
        // 证据补账：模型没勾选待办但交付物已真实落盘——收口由代码代为完成
        // 持久任务并留证据，run 正常提交（事故：56 轮产物因未上报被判 Failed 整体
        // 回滚，纠偏轮也无法让弱模型上报——成败必须看可验证产物，不看口头记账）。
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var foo = CreateWorkspaceFile("Foo.cs");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: CreateSingleTaskPlan(foo));
        run = await ApprovePlanAsync(sut, run);
        // 不做任何任务完成上报：交付物已在磁盘。

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: [foo]));
        var completed = await sut.ConfirmCommitAsync(accepting.Id);

        accepting.State.Should().Be(BuildRunState.Accepting, "交付物在盘即由收口补账，不判失败");
        completed.State.Should().Be(BuildRunState.Completed);
        var planTask = completed.Plan!.Tasks.Should().ContainSingle().Subject;
        planTask.Status.Should().Be(BuildTaskStatus.Completed);
        planTask.CompletionEvidence.Should().Contain(evidence =>
            evidence.Kind == BuildEvidenceKind.TaskCompletion, "补账必须留可审计证据");
        tasks.GetTask(run.Plan!.Tasks[0].TaskItemId!)!.Status.Should().Be(TaskStatus.Completed,
            "补账同步推进持久任务状态");
    }

    [Fact]
    public async Task CompleteAsync_PlannedDeliverableMissing_RecordsWarningInsteadOfFailing()
    {
        // 成败看最终验证与产物，不看口头记账：计划里的交付物缺失只作为可审计警告证据，
        // 不再把整个 run 判失败回滚。
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var foo = CreateWorkspaceFile("Foo.cs");
        var missing = Path.Combine(_root, "NeverWritten.cs"); // 刻意不创建：模拟交付物未落地
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Fix Foo.cs.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: new BuildPlan(
                "plan with missing deliverable",
                [new BuildPlanTask("impl", "Implement fix", "Fix Foo.cs", [], [foo, missing], ["Foo behavior is fixed"])],
                ["dotnet test"],
                [],
                []));
        run = await ApprovePlanAsync(sut, run);

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: [foo]));

        accepting.State.Should().Be(BuildRunState.Accepting, "交付物缺失降级为警告，不判失败");
        accepting.Validations[^1].Evidence.Should().Contain(
            evidence => evidence.Contains("Planned deliverable not found") && evidence.Contains("NeverWritten.cs"),
            "缺失交付物必须写入最终验证证据");
        accepting.Validations[^1].Evidence.Should().NotContain(
            evidence => evidence.Contains("Foo.cs") && evidence.Contains("not found"),
            "在盘的交付物不得误报");
    }

    [Fact]
    public async Task CompleteAsync_AllPlannedDeliverablesMissingAndNoFilesChanged_BlocksRunInsteadOfCompleting()
    {
        // 事故复现：计划声明了 ExpectedFiles（如 sunyuchen-timeline.html），但模型整个 run 0 文件落盘、
        // 仅输出反问即停机——收口必须阻断假阳性完成，转为 Blocked (ValidationFailed)。
        var store = new InMemoryBuildRunStore();
        var tasks = new TaskService();
        var sut = CreateCoordinator(store, "fingerprint-a", tasks);
        var missingHtml = Path.Combine(_root, "sunyuchen-timeline.html");
        var missingSources = Path.Combine(_root, "sunyuchen-timeline-sources.md");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Generate sunyuchen-timeline.html.",
            _root,
            TestContext.Current.CancellationToken,
            prescribedPlan: new BuildPlan(
                "generate timeline html",
                [new BuildPlanTask("s1", "Write HTML", "Write timeline files", [], [missingSources, missingHtml], ["HTML exists"])],
                [],
                [],
                []));
        run = await ApprovePlanAsync(sut, run);

        var blocked = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "Three blockers to resolve before I write anything...",
                100,
                50,
                5,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Skipped,
                ModifiedFiles: []));

        blocked.State.Should().Be(BuildRunState.Blocked, "零文件改动且计划交付物全部未落盘时严禁假阳性完成");
        blocked.TerminalReason.Should().Be(RunTerminalReason.ValidationFailed);
        blocked.FailureSummary.Should().Contain("Planned deliverables were not produced");
        blocked.FailureSummary.Should().Contain("sunyuchen-timeline.html");
        tasks.GetTask(run.Plan!.Tasks[0].TaskItemId!)!.Status.Should().Be(TaskStatus.Failed,
            "零交付物阻断时不得将未完成任务假阳性补账为 Completed");
    }
    [Fact]
    public async Task CompleteAsync_ResearchOnlyTaskWithoutFilesOrToolCalls_StillCompletes()
    {
        // 调研类步骤既不落盘也没有归属到它的工具调用：验收证据退到最终验证证据，
        // 不得因"找不到任务专属证据"把整个 run 判 Blocked。
        var store = new InMemoryBuildRunStore();
        var sut = CreateCoordinator(store, "fingerprint-a");
        var foo = CreateWorkspaceFile("Foo.cs");
        var run = await sut.BeginOrResumeAsync(
            SessionId.NewId(),
            "Research then fix Foo.cs.",
            Path.GetTempPath(),
            TestContext.Current.CancellationToken,
            prescribedPlan: new BuildPlan(
                "research then implement",
                [
                    new BuildPlanTask("research", "Read the module", "Understand Foo", [], [], ["Findings noted"]),
                    new BuildPlanTask("impl", "Implement fix", "Fix Foo.cs", ["research"], [foo], ["Foo behavior is fixed"]),
                ],
                ["dotnet test"],
                [],
                []));
        run = await ApprovePlanAsync(sut, run);

        var accepting = await sut.CompleteAsync(
            run.Id,
            new MainAgentRunResult(
                "done",
                100,
                50,
                3,
                TerminalReason: RunTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed,
                ModifiedFiles: [foo]));
        var completed = await sut.ConfirmCommitAsync(accepting.Id);

        accepting.State.Should().Be(BuildRunState.Accepting, "调研步骤缺少专属证据不得阻塞收口");
        completed.State.Should().Be(BuildRunState.Completed);
        completed.Plan!.Tasks.Should().OnlyContain(task => task.Status == BuildTaskStatus.Completed);
        completed.Scope!.AcceptanceCriteria.Should().OnlyContain(
            item => item.Status == AcceptanceStatus.Passed && !string.IsNullOrWhiteSpace(item.Evidence));
    }

    private string CreateWorkspaceFile(string name)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "content");
        return path;
    }

    /// <summary>双任务计划（impl → foo，tests → fooTests）。</summary>
    private static BuildPlan CreateMultiTaskPlan(string foo, string fooTests) => new(
        "two-file plan",
        [
            new BuildPlanTask("impl", "Implement fix", "Fix Foo.cs", [], [foo], ["Foo behavior is fixed"]),
            new BuildPlanTask(
                "tests",
                "Add tests",
                "Add FooTests.cs",
                ["impl"],
                [fooTests],
                ["Foo tests pass"]),
        ],
        ["dotnet test"],
        [],
        []);

    /// <summary>单任务计划。</summary>
    private static BuildPlan CreateSingleTaskPlan(string foo) => new(
        "single-task plan",
        [new BuildPlanTask("impl", "Implement fix", "Fix Foo.cs", [], [foo], ["Foo behavior is fixed"])],
        ["dotnet test"],
        [],
        []);
}



