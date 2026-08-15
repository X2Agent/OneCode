using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Coordinator;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Teams;

namespace OneCode.Tests;

public sealed class TeamRunStateMachineTests
{
    private readonly TeamRunStateMachine _sut = new();

    [Fact]
    public void CanCommit_RequiredGateFailed_ReturnsFalse()
    {
        var run = CreateRun(
            TeamRunPhase.Delivery,
            TeamRunStatus.Running,
            [Task("implementation", TeamToolPolicy.WriteAllowed, TeamTaskStatus.Succeeded)]) with
        {
            GateResults =
            [
                new QualityGateResult(
                    "build",
                    QualityGateKind.Build,
                    Required: true,
                    QualityGateStatus.Failed,
                    "failed",
                    [],
                    TimeSpan.Zero),
            ],
        };

        _sut.CanCommit(run).Should().BeFalse();
    }

    [Fact]
    public void Transition_ToSucceededWithoutDeliveryAndCommit_Throws()
    {
        var run = CreateRun(
            TeamRunPhase.Delivery,
            TeamRunStatus.Running,
            [Task("implementation", TeamToolPolicy.WriteAllowed, TeamTaskStatus.Succeeded)]) with
        {
            GateResults =
            [
                new QualityGateResult(
                    "build",
                    QualityGateKind.Build,
                    Required: true,
                    QualityGateStatus.Passed,
                    "passed",
                    [],
                    TimeSpan.Zero),
            ],
        };

        var act = () => _sut.Transition(
            run,
            TeamRunPhase.Completed,
            TeamRunStatus.Succeeded,
            DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Succeeded TeamRun requires*");
    }

    internal static TeamRun CreateRun(
        TeamRunPhase phase,
        TeamRunStatus status,
        IReadOnlyList<TeamTaskState> tasks)
    {
        var now = DateTimeOffset.UtcNow;
        return new TeamRun
        {
            Id = TeamRunId.NewId(),
            TeamName = "test-team",
            OriginalRequest = "Implement feature",
            WorkingDirectory = Environment.CurrentDirectory,
            Phase = phase,
            Status = status,
            Plan = Plan(tasks.Select(t => t.Definition).ToList()),
            TaskGraph = new TeamTaskGraph(tasks),
            PlanApproved = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    internal static TeamTaskState Task(
        string id,
        TeamToolPolicy policy,
        TeamTaskStatus? status,
        IReadOnlyList<string>? dependsOn = null)
        => new(
            new TeamTaskDefinition(
                id,
                id,
                TeamTaskKind.Implementation,
                "executor",
                dependsOn ?? [],
                ["Task succeeds."],
                policy),
            status);

    internal static ImplementationPlan Plan(IReadOnlyList<TeamTaskDefinition>? tasks = null)
        => new(
            "Implement approved change",
            tasks ?? [Task("implementation", TeamToolPolicy.WriteAllowed, null).Definition],
            [new QualityGateDefinition("build", QualityGateKind.Build, Required: true, "Build passes")],
            [],
            []);
}

public sealed class TeamRequirementServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_BroadRequest_UsesGeneratorQuestionsAndIntake()
    {
        var generator = Substitute.For<IClarificationQuestionGenerator>();
        generator.GenerateAsync(
                Arg.Any<string>(),
                Arg.Any<OneCode.Core.Build.RequirementAssessment>(),
                Arg.Any<CancellationToken>())
            .Returns(new OneCode.App.Services.RequirementIntake(
                ["第一期要优先落地哪个产研环节？"],
                [],
                ["产研平台完成验收"],
                ["技术栈不限制"]));
        var sut = new TeamRequirementService(
            new OneCode.App.Services.BuildMode.RequirementAssessmentService(),
            generator);

        var result = await sut.AnalyzeAsync(
            "开发一个产研、测试和发布一体化 AI 平台",
            TestContext.Current.CancellationToken);

        result.CanProceedWithoutClarification.Should().BeFalse();
        result.Questions.Should().ContainSingle(question => question.Blocking);
        result.Questions[0].Question.Should().Contain("产研");
        result.Questions[0].Question.Should().NotContain("可观察行为");
        result.Questions[0].Question.Should().NotContain("公共接口");
        result.Draft.OpenQuestions.Should().BeEquivalentTo(result.Questions.Select(question => question.Question));
        result.Draft.AcceptanceCriteria.Should().Equal("产研平台完成验收");
        result.Draft.Constraints.Should().Equal("技术栈不限制");
        result.Draft.InScope.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_GeneratorFailure_PropagatesWithoutFallback()
    {
        var generator = Substitute.For<IClarificationQuestionGenerator>();
        generator.GenerateAsync(
                Arg.Any<string>(),
                Arg.Any<OneCode.Core.Build.RequirementAssessment>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<OneCode.App.Services.RequirementIntake>(
                new InvalidOperationException("澄清问题生成失败：模型不可用")));
        var sut = new TeamRequirementService(
            new OneCode.App.Services.BuildMode.RequirementAssessmentService(),
            generator);

        var act = () => sut.AnalyzeAsync("开发一个产研 AI 系统", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*澄清问题生成失败*");
    }

    [Fact]
    public async Task AnalyzeAsync_UsesGeneratedQuestionsInsteadOfTemplates()
    {
        var generator = Substitute.For<IClarificationQuestionGenerator>();
        generator.GenerateAsync(
                Arg.Any<string>(),
                Arg.Any<OneCode.Core.Build.RequirementAssessment>(),
                Arg.Any<CancellationToken>())
            .Returns(new OneCode.App.Services.RequirementIntake(
                ["第一期要先做成哪个可演示的产研环节？", "哪些角色是第一期用户，哪些明确不做？"],
                [], [], []));
        var sut = new TeamRequirementService(
            new OneCode.App.Services.BuildMode.RequirementAssessmentService(),
            generator);

        var result = await sut.AnalyzeAsync(
            "我想开发一个产研+AI的系统，技术栈不限制",
            TestContext.Current.CancellationToken);

        result.CanProceedWithoutClarification.Should().BeFalse();
        result.Questions.Select(question => question.Question).Should().Equal(
            "第一期要先做成哪个可演示的产研环节？",
            "哪些角色是第一期用户，哪些明确不做？");
        result.Questions.Should().NotContain(question => question.Question.Contains("可观察行为"));
    }

    [Fact]
    public async Task AnalyzeAsync_AfterClarificationAnswer_ProceedsWithoutAskingAgain()
    {
        var generator = Substitute.For<IClarificationQuestionGenerator>();
        var sut = new TeamRequirementService(
            new OneCode.App.Services.BuildMode.RequirementAssessmentService(),
            generator);
        var clarified = """
            我想开发一个产研+AI的系统
            Clarification response:
            第一期做需求文档生成，用户是产品经理，不做代码仓库托管。
            """;

        var result = await sut.AnalyzeAsync(clarified, TestContext.Current.CancellationToken);

        result.CanProceedWithoutClarification.Should().BeTrue();
        result.Questions.Should().BeEmpty();
        await generator.DidNotReceive().GenerateAsync(
            Arg.Any<string>(),
            Arg.Any<OneCode.Core.Build.RequirementAssessment>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateImplementationPlan_BoundedRequest_CreatesDependencyGraphAndIndependentGates()
    {
        var generator = Substitute.For<IClarificationQuestionGenerator>();
        var sut = new TeamRequirementService(
            new OneCode.App.Services.BuildMode.RequirementAssessmentService(),
            generator);
        var analysis = await sut.AnalyzeAsync(
            "修复 Foo.cs 空引用并运行测试验证不再抛出异常",
            TestContext.Current.CancellationToken);

        var plan = sut.CreateImplementationPlan(analysis);

        plan.Tasks.Should().HaveCount(3);
        plan.Tasks.Select(task => task.Id).Should().Equal("analysis", "implementation", "validation");
        plan.Tasks.Single(task => task.Id == "implementation").DependsOn.Should().Contain("analysis");
        plan.Tasks.Single(task => task.Id == "validation").DependsOn.Should().Contain("implementation");
        plan.Tasks.Single(task => task.Id == "implementation").RequiredTools.Should().Contain("Edit");
        plan.RequiredGates.Select(gate => gate.Kind).Should().Contain([
            QualityGateKind.LspDiagnostics,
            QualityGateKind.Build,
            QualityGateKind.UnitTest,
            QualityGateKind.AcceptanceCriteria,
        ]);
    }
}

public sealed class TeamQualityGateRunnerTests
{
    [Fact]
    public async Task RunAsync_OrdersAndExecutesIndependentGateValidators()
    {
        var calls = new List<QualityGateKind>();
        var build = CreateValidator(QualityGateKind.Build, calls);
        var tests = CreateValidator(QualityGateKind.UnitTest, calls);
        var lsp = CreateValidator(QualityGateKind.LspDiagnostics, calls);
        var sut = new TeamQualityGateRunner([build, tests, lsp]);
        var run = TeamRunStateMachineTests.CreateRun(
            TeamRunPhase.Verification,
            TeamRunStatus.Running,
            [TeamRunStateMachineTests.Task("implementation", TeamToolPolicy.WriteAllowed, TeamTaskStatus.Succeeded)]);
        using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);

        var results = await sut.RunAsync(
            [
                new QualityGateDefinition("test", QualityGateKind.UnitTest, true, "tests"),
                new QualityGateDefinition("build", QualityGateKind.Build, true, "build"),
                new QualityGateDefinition("lsp", QualityGateKind.LspDiagnostics, false, "lsp"),
            ],
            Environment.CurrentDirectory,
            transaction,
            run,
            TestContext.Current.CancellationToken);

        calls.Should().ContainInOrder(
            QualityGateKind.LspDiagnostics,
            QualityGateKind.Build,
            QualityGateKind.UnitTest);
        results.Select(result => result.GateId).Should().ContainInOrder("lsp", "build", "test");
    }

    [Fact]
    public async Task RunAsync_RequiredFailure_SkipsOnlyDownstreamGates()
    {
        var build = Substitute.For<ITeamQualityGateValidator>();
        build.Kind.Returns(QualityGateKind.Build);
        build.ValidateAsync(
                Arg.Any<QualityGateDefinition>(),
                Arg.Any<TeamQualityGateContext>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Result((QualityGateDefinition)call[0], QualityGateStatus.Failed));
        var tests = CreateValidator(QualityGateKind.UnitTest, []);
        var sut = new TeamQualityGateRunner([build, tests]);
        var run = TeamRunStateMachineTests.CreateRun(
            TeamRunPhase.Verification,
            TeamRunStatus.Running,
            [TeamRunStateMachineTests.Task("implementation", TeamToolPolicy.WriteAllowed, TeamTaskStatus.Succeeded)]);
        using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);

        var results = await sut.RunAsync(
            [
                new QualityGateDefinition("test", QualityGateKind.UnitTest, true, "tests"),
                new QualityGateDefinition("build", QualityGateKind.Build, true, "build"),
            ],
            Environment.CurrentDirectory,
            transaction,
            run,
            TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
        results[0].Status.Should().Be(QualityGateStatus.Failed);
        results[1].Status.Should().Be(QualityGateStatus.SkippedByDependency);
        await tests.DidNotReceive().ValidateAsync(
            Arg.Any<QualityGateDefinition>(),
            Arg.Any<TeamQualityGateContext>(),
            Arg.Any<CancellationToken>());
    }

    private static ITeamQualityGateValidator CreateValidator(
        QualityGateKind kind,
        ICollection<QualityGateKind> calls)
    {
        var validator = Substitute.For<ITeamQualityGateValidator>();
        validator.Kind.Returns(kind);
        validator.ValidateAsync(
                Arg.Any<QualityGateDefinition>(),
                Arg.Any<TeamQualityGateContext>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add(kind);
                return Result((QualityGateDefinition)call[0], QualityGateStatus.Passed);
            });
        return validator;
    }

    private static QualityGateResult Result(
        QualityGateDefinition definition,
        QualityGateStatus status)
        => new(
            definition.Id,
            definition.Kind,
            definition.Required,
            status,
            status.ToString(),
            [],
            TimeSpan.Zero);
}

public sealed class JsonTeamRunStoreTests
{
    [Fact]
    public async Task TrySaveAsync_StaleExpectedVersion_ReturnsFalse()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonTeamRunStore(directory);
            var run = TeamRunStateMachineTests.CreateRun(
                TeamRunPhase.Execution,
                TeamRunStatus.Running,
                [TeamRunStateMachineTests.Task("implementation", TeamToolPolicy.WriteAllowed, null)]);

            (await store.TrySaveAsync(run, expectedVersion: 0, TestContext.Current.CancellationToken)).Should().BeTrue();
            var stale = run with { Version = 2 };

            (await store.TrySaveAsync(stale, expectedVersion: 0, TestContext.Current.CancellationToken)).Should().BeFalse();
            (await store.LoadAsync(run.Id, TestContext.Current.CancellationToken))!.Version.Should().Be(1);
        }
        finally
        {
            SafeDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadActiveAsync_ReturnsNewestNonTerminalRunForWorkspace()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonTeamRunStore(directory);
            var first = TeamRunStateMachineTests.CreateRun(
                TeamRunPhase.Execution,
                TeamRunStatus.Running,
                [TeamRunStateMachineTests.Task("one", TeamToolPolicy.WriteAllowed, null)]);
            var second = TeamRunStateMachineTests.CreateRun(
                TeamRunPhase.Verification,
                TeamRunStatus.Running,
                [TeamRunStateMachineTests.Task("two", TeamToolPolicy.WriteAllowed, TeamTaskStatus.Succeeded)]) with
            {
                UpdatedAt = first.UpdatedAt.AddSeconds(1),
            };

            (await store.TrySaveAsync(first, 0, TestContext.Current.CancellationToken)).Should().BeTrue();
            (await store.TrySaveAsync(second, 0, TestContext.Current.CancellationToken)).Should().BeTrue();

            var active = await store.LoadActiveAsync(first.WorkingDirectory, TestContext.Current.CancellationToken);
            active!.Id.Should().Be(second.Id);
        }
        finally
        {
            SafeDeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"TeamRunStoreTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}

public sealed class TeamRunApplicationServiceTests
{
    [Fact]
    public async Task TaskLifecycle_PersistsAttemptAndSucceededStates()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonTeamRunStore(directory);
            var sut = CreateSut(store, CreateVerificationProvider(success: true, skipped: false));
            var run = await sut.BeginApprovedExecutionAsync(
                TeamRunId.NewId(),
                "test-team",
                "Implement feature",
                directory,
                TeamRunStateMachineTests.Plan(),
                TestContext.Current.CancellationToken);

            var started = await sut.StartTaskAsync(
                run,
                "implementation",
                TestContext.Current.CancellationToken);
            var completed = await sut.CompleteTaskAsync(
                started,
                "implementation",
                SuccessfulExecution(),
                TestContext.Current.CancellationToken);

            // StartTaskAsync increments Attempt but leaves Status null (no terminal outcome yet).
            started.TaskGraph!.Tasks.Single().Status.Should().BeNull();
            started.TaskGraph.Tasks.Single().Attempt.Should().Be(1);
            completed.TaskGraph!.Tasks.Single().Status.Should().Be(TeamTaskStatus.Succeeded);
            completed.TaskGraph.Tasks.Single().Summary.Should().Be("implemented");
            (await store.LoadAsync(run.Id, TestContext.Current.CancellationToken))!
                .TaskGraph!.Tasks.Single().Status.Should().Be(TeamTaskStatus.Succeeded);
        }
        finally
        {
            SafeDeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0, "(no output)")]
    [InlineData(0, "")]
    public async Task CompleteTaskAsync_NoAgentResponse_MarksTaskFailed(
        int turnsCompleted,
        string output)
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonTeamRunStore(directory);
            var sut = CreateSut(store, CreateVerificationProvider(success: true, skipped: false));
            var run = await sut.BeginApprovedExecutionAsync(
                TeamRunId.NewId(),
                "test-team",
                "Implement feature",
                directory,
                TeamRunStateMachineTests.Plan(),
                TestContext.Current.CancellationToken);
            run = await sut.StartTaskAsync(run, "implementation", TestContext.Current.CancellationToken);

            var completed = await sut.CompleteTaskAsync(
                run,
                "implementation",
                new TeamRunResult("test-team", output, turnsCompleted, MaxTurnsReached: false),
                TestContext.Current.CancellationToken);

            var task = completed.TaskGraph!.Tasks.Single();
            task.Status.Should().Be(TeamTaskStatus.Failed);
            task.Failure.Should().NotBeNull();
            task.Failure!.Detail.Should().Contain("without any agent response");
            completed.Failure.Should().Be(task.Failure);
        }
        finally
        {
            SafeDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CompleteExecutionAsync_PassedGate_CommitsAndPersistsDelivery()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new JsonTeamRunStore(directory);
            var verification = CreateVerificationProvider(success: true, skipped: false);
            var sut = CreateSut(store, verification);
            var runId = TeamRunId.NewId();
            var run = await sut.BeginApprovedExecutionAsync(
                runId,
                "test-team",
                "Implement feature",
                directory,
                TeamRunStateMachineTests.Plan(),
                TestContext.Current.CancellationToken);
            using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);
            run = await CompleteImplementationTaskAsync(sut, run);

            var completed = await sut.CompleteExecutionAsync(
                run,
                SuccessfulExecution(),
                transaction,
                [],
                TestContext.Current.CancellationToken);

            completed.Status.Should().Be(TeamRunStatus.Succeeded);
            completed.TransactionCommitted.Should().BeTrue();
            completed.Delivery.Should().NotBeNull();
            completed.Delivery!.Committed.Should().BeTrue();
            transaction.IsCommitted.Should().BeTrue();
            (await store.LoadAsync(runId, TestContext.Current.CancellationToken))!.Version.Should().Be(completed.Version);
        }
        finally
        {
            SafeDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CompleteExecutionAsync_FinalPersistenceConflict_DoesNotCommit()
    {
        var store = new ConflictOnFinalSaveStore();
        var directory = CreateTempDirectory();
        try
        {
            var sut = CreateSut(store, CreateVerificationProvider(success: true, skipped: false));
            var run = await sut.BeginApprovedExecutionAsync(
                TeamRunId.NewId(),
                "test-team",
                "Implement feature",
                directory,
                TeamRunStateMachineTests.Plan(),
                TestContext.Current.CancellationToken);
            using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);
            run = await CompleteImplementationTaskAsync(sut, run);

            var act = async () => await sut.CompleteExecutionAsync(
                run,
                SuccessfulExecution(),
                transaction,
                [],
                TestContext.Current.CancellationToken);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*version conflict*");
            transaction.IsCommitted.Should().BeFalse();
        }
        finally
        {
            SafeDeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task CompleteExecutionAsync_FailedOrSkippedGate_RollsBack(
        bool success,
        bool skipped)
    {
        var directory = CreateTempDirectory();
        var changedFile = Path.Combine(directory, "changed.txt");
        await File.WriteAllTextAsync(changedFile, "before", TestContext.Current.CancellationToken);
        try
        {
            var store = new JsonTeamRunStore(Path.Combine(directory, "runs"));
            var sut = CreateSut(store, CreateVerificationProvider(success, skipped));
            var run = await sut.BeginApprovedExecutionAsync(
                TeamRunId.NewId(),
                "test-team",
                "Implement feature",
                directory,
                TeamRunStateMachineTests.Plan(),
                TestContext.Current.CancellationToken);
            using var transaction = new EditTransaction(NullLogger<EditTransaction>.Instance);
            run = await CompleteImplementationTaskAsync(sut, run);
            transaction.Snapshot(changedFile);
            await File.WriteAllTextAsync(changedFile, "after", TestContext.Current.CancellationToken);

            var completed = await sut.CompleteExecutionAsync(
                run,
                SuccessfulExecution(),
                transaction,
                [new FileChange(changedFile, ["after"], ["before"])],
                TestContext.Current.CancellationToken);

            completed.Status.Should().Be(TeamRunStatus.RolledBack);
            completed.TransactionCommitted.Should().BeFalse();
            completed.Delivery!.Committed.Should().BeFalse();
            if (skipped)
            {
                completed.Delivery.Gates.Single().Evidence.Should().Contain(
                    evidence => evidence.Contains($"workingDirectory={directory}", StringComparison.Ordinal));
            }
            transaction.IsCommitted.Should().BeFalse();
            (await File.ReadAllTextAsync(changedFile, TestContext.Current.CancellationToken)).Should().Be("before");
        }
        finally
        {
            SafeDeleteDirectory(directory);
        }
    }

    private static TeamRunApplicationService CreateSut(
        ITeamRunStore store,
        IVerificationProvider verification)
        => new(
            store,
            new TeamRunStateMachine(),
            new TeamQualityGateRunner(
            [
                new TeamBuildQualityGateValidator(verification),
                new TeamUnitTestQualityGateValidator(verification),
                new TeamIntegrationTestQualityGateValidator(verification),
                new TeamAcceptanceCriteriaQualityGateValidator(),
            ]),
            new DeliveryReportBuilder());

    private static IVerificationProvider CreateVerificationProvider(bool success, bool skipped)
    {
        var result = new VerificationResult
        {
            Success = success,
            Skipped = skipped,
            Errors = success && !skipped
                ? []
                : [new VerificationError("test.cs", 1, 1, "error", "verification failed")],
            Duration = TimeSpan.FromMilliseconds(10),
        };
        var provider = Substitute.For<IVerificationProvider>();
        provider.VerifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(result);
        provider.VerifyTestsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(result);
        provider.VerifyIntegrationTestsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return provider;
    }

    private static async Task<TeamRun> CompleteImplementationTaskAsync(
        TeamRunApplicationService sut,
        TeamRun run)
    {
        var running = await sut.StartTaskAsync(
            run,
            "implementation",
            TestContext.Current.CancellationToken);
        return await sut.CompleteTaskAsync(
            running,
            "implementation",
            SuccessfulExecution(),
            TestContext.Current.CancellationToken);
    }

    private static TeamRunResult SuccessfulExecution()
        => new("test-team", "implemented", 1, MaxTurnsReached: false);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"TeamRunApplicationTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed class ConflictOnFinalSaveStore : ITeamRunStore
    {
        private TeamRun? _run;
        private int _saveCount;

        public Task<TeamRun?> LoadAsync(TeamRunId runId, CancellationToken ct = default)
            => Task.FromResult(_run?.Id == runId ? _run : null);

        public Task<TeamRun?> LoadActiveAsync(string workingDirectory, CancellationToken ct = default)
            => Task.FromResult(_run);

        public Task<IReadOnlyList<TeamRun>> ListActiveAsync(CancellationToken ct = default)
        {
            TeamRun[] active = _run is { } run
                && run.Status is TeamRunStatus.Running or TeamRunStatus.Blocked
                ? [run]
                : [];
            return Task.FromResult<IReadOnlyList<TeamRun>>(active);
        }

        public Task<TeamRun> ClaimWorkflowAsync(
            TeamRunId runId,
            long fencingToken,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var run = _run ?? throw new InvalidOperationException("No run to claim.");
            if ((run.Version) != expectedVersion)
                throw new InvalidOperationException("Version conflict while claiming workflow.");
            var claimed = run with
            {
                WorkflowFencingToken = fencingToken,
                Version = checked(run.Version + 1),
            };
            _run = claimed;
            return Task.FromResult(claimed);
        }

        public Task SaveFencedAsync(
            TeamRun run,
            long expectedVersion,
            long expectedFencingToken,
            CancellationToken ct = default)
        {
            if (run.WorkflowFencingToken != expectedFencingToken)
                throw new InvalidOperationException("Fencing token mismatch.");
            return TrySaveAsync(run, expectedVersion, ct);
        }

        public Task<bool> TrySaveAsync(TeamRun run, long expectedVersion, CancellationToken ct = default)
        {
            _saveCount++;
            if (_saveCount == 6) // create, task start, task complete, verification, delivery, final commit evidence
                return Task.FromResult(false);
            if ((_run?.Version ?? 0) != expectedVersion)
                return Task.FromResult(false);
            _run = run;
            return Task.FromResult(true);
        }
    }
}
