using System.Threading.Channels;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;
using OneCode.Core.Workflows;
using OneCode.Core.Domain;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

public sealed class ControlledBuildAttemptRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "controlled-build-runtime", Guid.NewGuid().ToString("N"));

    public ControlledBuildAttemptRuntimeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExecuteAsync_Success_CommitsTransactionAndCompletesBuildRun()
    {
        var file = CreateFile("success.txt", "before");
        var run = CreateClaimedRun();
        var runner = CreateRunner(async options =>
        {
            options.SharedTransaction!.Snapshot(file);
            await File.WriteAllTextAsync(file, "after", TestContext.Current.CancellationToken);
            await options.BeforeFinalValidation!(TestContext.Current.CancellationToken);
            return PassingResult([file]);
        });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.BeginVerificationAsync(run.Id, Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Verifying, Version = 2 });
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Accepting, Version = 3 });
        coordinator.ConfirmCommitAsync(run.Id, Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Completed, TransactionCommitted = true, Version = 4 });

        var result = await CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should().Be("after");
        result.TransactionCommitted.Should().BeTrue();
        result.TransactionRolledBack.Should().BeFalse();
        await coordinator.Received(1).BeginVerificationAsync(run.Id, Arg.Any<CancellationToken>(), 7);
        await coordinator.Received(1).ConfirmCommitAsync(run.Id, Arg.Any<CancellationToken>(), 7);
    }

    [Fact]
    public async Task ExecuteAsync_ValidationFailure_PreservesArtifactsForReview()
    {
        var file = CreateFile("failure.txt", "before");
        var run = CreateClaimedRun();
        var runner = CreateRunner(async options =>
        {
            options.SharedTransaction!.Snapshot(file);
            await File.WriteAllTextAsync(file, "after", TestContext.Current.CancellationToken);
            return PassingResult([file]) with
            {
                TerminalReason = RunTerminalReason.ValidationFailed,
                FinalValidationStatus = BuildValidationStatus.Failed,
            };
        });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Blocked, Version = 2 });

        var result = await CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should().Be("after",
            "机器校验失败不得丢弃产物，保留给人复核");
        result.TransactionCommitted.Should().BeFalse();
        result.TransactionRolledBack.Should().BeFalse();
        await coordinator.DidNotReceive().ConfirmCommitAsync(
            default,
            Arg.Any<CancellationToken>(),
            Arg.Any<long?>());
    }

    [Fact]
    public async Task ExecuteAsync_AgentException_PreservesArtifactsAndPersistsFailure()
    {
        var file = CreateFile("exception.txt", "before");
        var run = CreateClaimedRun();
        var runner = CreateRunner(async options =>
        {
            options.SharedTransaction!.Snapshot(file);
            await File.WriteAllTextAsync(file, "after", TestContext.Current.CancellationToken);
            throw new InvalidOperationException("agent failed");
        });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Failed, Version = 2 });

        var act = () => CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("agent failed");
        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should().Be("after",
            "agent 异常不得连带丢弃已写出的产物");
        await coordinator.Received(1).CompleteAsync(
            run.Id,
            Arg.Is<MainAgentRunResult>(result =>
                result.TerminalReason == RunTerminalReason.AgentException
                && !result.TransactionRolledBack),
            Arg.Any<CancellationToken>(),
            7);
    }

    [Fact]
    public async Task ExecuteAsync_MissingApprovedToolPolicy_FailsClosed()
    {
        var run = CreateClaimedRun() with { ApprovedToolPolicy = null };
        var runner = CreateRunner(_ => Task.FromResult(PassingResult([])));
        var coordinator = Substitute.For<IBuildRunCoordinator>();

        var act = () => CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no approved tool policy*");
        await runner.DidNotReceive().RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(),
            Arg.Any<ChannelWriter<object>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ApprovedPolicy_SuppressesInteractiveApprovalAndEnforcesWhitelist()
    {
        var run = CreateClaimedRun();
        var runner = CreateRunner(async options =>
        {
            options.SuppressToolApproval.Should().BeTrue();
            options.ApprovalBroker.Should().BeNull();
            options.IsToolAllowed.Should().NotBeNull();
            options.IsToolAllowed!("Write").Should().BeTrue();           // approved
            options.IsToolAllowed!("ReadFile").Should().BeTrue();        // read-only fallback
            options.IsToolAllowed!("todos_complete").Should().BeTrue();  // 待办是步骤进度的唯一记账通道
            options.IsToolAllowed!("file_memory_ls").Should().BeTrue();  // harness 内存草稿箱工具必须放行
            options.IsToolAllowed!("Bash").Should().BeFalse();           // not approved, not read-only
            options.ToolCapabilities!.AllowedToolNames.Should().Contain("Write");
            options.PermissionRules!.Keys.Should().Contain("build-approved-policy");
            var alwaysAllowTools = options.PermissionRules["build-approved-policy"].AlwaysAllow!
                .Select(r => r.ToolName).ToList();
            alwaysAllowTools.Should().Contain(["Write", "todos_get_remaining", "todos_complete", "file_memory_ls"]);
            return PassingResult([]);
        });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Accepting, Version = 2 });
        coordinator.ConfirmCommitAsync(run.Id, Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Completed, TransactionCommitted = true, Version = 3 });

        await CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_RestoresAmbientActivationContextDuringAgentRun()
    {
        // 受控 attempt 经 durable workflow 调度，流枚举链设置的 ambient AsyncLocal
        // （会话 ID / run ID / 能力边界）不保证延续到本层——agent 执行期间必须
        // 重建，否则依赖会话上下文的工具（待办、SubmitPlan 等）定位不到会话。
        var run = CreateClaimedRun();
        var agentRunId = $"build-{Guid.NewGuid():N}";
        string? seenConversation = null;
        string? seenRunId = null;
        var runner = CreateRunner(_ =>
        {
            seenConversation = ToolActivationContext.CurrentConversationId;
            seenRunId = OneCodeAgentRunContext.CurrentRunId;
            return Task.FromResult(PassingResult([]));
        });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Accepting, Version = 2 });
        coordinator.ConfirmCommitAsync(run.Id, Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Completed, TransactionCommitted = true, Version = 3 });

        var options = new MainAgentRunOptions { ConversationId = run.ConversationId, AgentRunId = agentRunId };
        ToolActivationContext.CurrentConversationId = "outer-context";
        try
        {
            await CreateRuntime(run, runner, coordinator, options: options).ExecuteAsync(
                CreateInput(run),
                TestContext.Current.CancellationToken);

            seenConversation.Should().Be(run.ConversationId.ToString(), "agent 执行期间会话上下文指向本次 run");
            seenRunId.Should().Be(agentRunId);
            ToolActivationContext.CurrentConversationId.Should().Be("outer-context", "执行结束后恢复外层上下文");
        }
        finally
        {
            ToolActivationContext.CurrentConversationId = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UserCancellation_RollsBackFileChanges()
    {
        // 回滚只属于用户显式取消：事务回滚、结果与落盘标记一致。
        var file = CreateFile("cancelled.txt", "before");
        var run = CreateClaimedRun();
        var runner = CreateRunner(async options =>
        {
            options.SharedTransaction!.Snapshot(file);
            await File.WriteAllTextAsync(file, "after", TestContext.Current.CancellationToken);
            throw new OperationCanceledException();
        });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Cancelled, TransactionRolledBack = true, Version = 2 });

        var act = () => CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<OperationCanceledException>();

        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should().Be("before");
        await coordinator.Received(1).CompleteAsync(
            run.Id,
            Arg.Is<MainAgentRunResult>(result =>
                result.TerminalReason == RunTerminalReason.Cancelled && result.TransactionRolledBack),
            Arg.Any<CancellationToken>(),
            7);
    }

    [Fact]
    public async Task ExecuteAsync_CoordinatorBlocksAfterAgentSuccess_KeepsNewFilesOnDisk()
    {
        // 事故复现：agent 本轮成功产出新文件（terminalReason=Completed），但 CompleteAsync
        // 在收口内部（归属/证据对账）判不通过。旧实现 dispose 会静默删除新文件；
        // 现在 runtime 必须解除自动回滚，产物保留，落盘标记保持未回滚。
        var html = Path.Combine(_root, "index.html");
        var run = CreateClaimedRun();
        var runner = CreateRunner(async options =>
        {
            options.SharedTransaction!.Snapshot(html);
            await File.WriteAllTextAsync(html, "<html/>", TestContext.Current.CancellationToken);
            await options.BeforeFinalValidation!(TestContext.Current.CancellationToken);
            return PassingResult([html]);
        });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.BeginVerificationAsync(run.Id, Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Verifying, Version = 2 });
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Blocked, Version = 2 });
        var store = CreateStore(run);

        var result = await CreateRuntime(run, runner, coordinator, store: store).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        File.Exists(html).Should().BeTrue("新建产物不得被 dispose 静默删除");
        result.TransactionRolledBack.Should().BeFalse();
        await store.DidNotReceive().SaveFencedAsync(
            Arg.Any<BuildRun>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RemainingTodos_CompletesSingleRoundWithoutCorrectiveNudgeAndPreservesArtifacts()
    {
        // 单轮直接执行：即使模型漏勾待办，也不再追加二次强制纠偏轮，产物保留并交由收口证据对账。
        var file = CreateFile("deliverable.txt", "work");
        var run = CreatePlannedRun(file);
        var prompts = new List<MainAgentRunOptions>();
        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Any<ChannelWriter<object>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                prompts.Add(call.ArgAt<MainAgentRunOptions>(0));
                return PassingResult([file]) with
                {
                    TurnCount = 2,
                    ToolCallCount = 4,
                    TotalInputTokens = 100,
                    TotalOutputTokens = 20,
                };
            });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Accepting, Version = 3 });
        coordinator.ConfirmCommitAsync(run.Id, Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Completed, TransactionCommitted = true, Version = 4 });

        var result = await CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        prompts.Should().HaveCount(1, "受控 Build 单轮直接执行，不再追加二次待办纠偏轮");
        result.TurnCount.Should().Be(2);
        result.ToolCallCount.Should().Be(4);
        result.TotalInputTokens.Should().Be(100);
        result.TotalOutputTokens.Should().Be(20);
        result.TerminalReason.Should().Be(RunTerminalReason.Completed);
        result.ModifiedFiles.Should().Equal(file);
        result.TransactionCommitted.Should().BeTrue();
        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should()
            .Be("work", "产物保留并正常提交");
    }

    [Fact]
    public async Task ExecuteAsync_NoRemainingTodos_DoesNotRunCorrectiveRound()
    {
        var file = CreateFile("deliverable.txt", "work");
        var run = CreatePlannedRun(file);
        var runner = CreateRunner(_ => Task.FromResult(PassingResult([file])));
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Accepting, Version = 2 });
        coordinator.ConfirmCommitAsync(run.Id, Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Completed, TransactionCommitted = true, Version = 3 });

        var result = await CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        await runner.Received(1).RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(),
            Arg.Any<ChannelWriter<object>>(),
            Arg.Any<CancellationToken>());
        result.TurnCount.Should().Be(1);
        result.TransactionCommitted.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_FirstRoundAtTurnLimit_SkipsCorrectiveRound()
    {
        // 首轮已烧尽轮次预算时不纠偏（纠偏轮没有预算可花），按既有 TurnLimitReached 收口。
        var file = CreateFile("deliverable.txt", "work");
        var run = CreatePlannedRun(file);
        var runner = CreateRunner(_ => Task.FromResult(PassingResult([file]) with
        {
            TurnCount = 2,
        }));
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.LimitReached, Version = 2 });
        var options = new MainAgentRunOptions
        {
            ConversationId = run.ConversationId,
            AgentRunId = $"build-{run.Id}",
            MaxTurns = 2,
        };

        var result = await CreateRuntime(run, runner, coordinator, options: options).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        await runner.Received(1).RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(),
            Arg.Any<ChannelWriter<object>>(),
            Arg.Any<CancellationToken>());
        result.TerminalReason.Should().Be(RunTerminalReason.TurnLimitReached);
        result.TransactionRolledBack.Should().BeFalse("轮数耗尽不是用户取消，产物保留");
    }

    [Fact]
    public async Task ExecuteAsync_PlannedRun_SeedsTodosFromApprovedPlanTasks()
    {
        var file = CreateFile("deliverable.txt", "work");
        var run = CreatePlannedRun(file);
        MainAgentRunOptions? captured = null;
        var runner = CreateRunner(options =>
        {
            captured = options;
            return Task.FromResult(PassingResult([file]));
        });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Accepting, Version = 2 });
        coordinator.ConfirmCommitAsync(run.Id, Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Completed, TransactionCommitted = true, Version = 3 });

        await CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        captured!.SeedTodoTasks.Should().ContainSingle().Which.Id.Should().Be("s1");
    }
    private ControlledBuildAttemptRuntime CreateRuntime(
        BuildRun run,
        IMainAgentRunner runner,
        IBuildRunCoordinator coordinator,
        MainAgentRunOptions? options = null,
        IBuildRunStore? store = null)
    {
        store ??= CreateStore(run);
        return new ControlledBuildAttemptRuntime(
            runner,
            coordinator,
            store,
            new ControlledBuildAttemptContext(
                options ?? new MainAgentRunOptions
                {
                    ConversationId = run.ConversationId,
                    AgentRunId = $"build-{run.Id}",
                },
                Channel.CreateUnbounded<object>().Writer,
                () => new EditTransaction(),
                buildRun => buildRun));
    }

    private static IBuildRunStore CreateStore(BuildRun run)
    {
        var store = Substitute.For<IBuildRunStore>();
        store.LoadByIdAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        return store;
    }

    private static IMainAgentRunner CreateRunner(
        Func<MainAgentRunOptions, Task<MainAgentRunResult>> execute)
    {
        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Any<ChannelWriter<object>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => execute(call.ArgAt<MainAgentRunOptions>(0)));
        return runner;
    }

    private static MainAgentRunResult PassingResult(IReadOnlyList<string> files)
        => new(
            "done",
            TotalInputTokens: 10,
            TotalOutputTokens: 5,
            TurnCount: 1,
            TerminalReason: RunTerminalReason.Completed,
            FinalValidationStatus: BuildValidationStatus.Passed,
            ModifiedFiles: files);

    private BuildRun CreateClaimedRun()
    {
        var now = DateTimeOffset.UtcNow;
        return new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            WorkingDirectory = _root,
            WorkspaceFingerprint = "fingerprint",
            WorkflowFencingToken = 7,
            ApprovedToolPolicy = new ApprovedToolPolicy(["ReadFile", "Write", "Edit"]),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>带批准计划的 run：计划任务会被预置为 agent 待办。</summary>
    private BuildRun CreatePlannedRun(params string[] expectedFiles)
        => CreateClaimedRun() with
        {
            Plan = new BuildPlan(
                "planned run",
                [new BuildPlanTask("s1", "Implement", "Do the work", [], expectedFiles, ["done"])],
                [],
                [],
                []),
        };

    private static ControlledBuildAttemptInput CreateInput(BuildRun run)
        => new($"build/{run.Id}/attempt/1", run.Id, 1, $"build/{run.Id}/attempt/1/agent-edit-transaction");

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}