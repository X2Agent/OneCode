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
    public async Task ExecuteAsync_TerminalFailure_RollsBackTransaction()
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
            .Returns(run with { State = BuildRunState.Failed, TransactionRolledBack = true, Version = 2 });

        var result = await CreateRuntime(run, runner, coordinator).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should().Be("before");
        result.TransactionCommitted.Should().BeFalse();
        result.TransactionRolledBack.Should().BeTrue();
        await coordinator.DidNotReceive().ConfirmCommitAsync(
            default,
            Arg.Any<CancellationToken>(),
            Arg.Any<long?>());
    }

    [Fact]
    public async Task ExecuteAsync_AgentException_RollsBackAndPersistsFailure()
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
        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should().Be("before");
        await coordinator.Received(1).CompleteAsync(
            run.Id,
            Arg.Is<MainAgentRunResult>(result =>
                result.TerminalReason == RunTerminalReason.AgentException
                && result.TransactionRolledBack),
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
            options.IsToolAllowed!("Write").Should().BeTrue();      // approved
            options.IsToolAllowed!("ReadFile").Should().BeTrue();   // read-only fallback
            options.IsToolAllowed!("Bash").Should().BeFalse();      // not approved, not read-only
            options.ToolCapabilities!.AllowedToolNames.Should().Contain("Write");
            options.PermissionRules!.Keys.Should().Contain("build-approved-policy");
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
        // 重建，否则 UpdatePlanStep 等工具定位不到会话而报
        // "Plan execution requires an explicit agent run conversation."。
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
    public async Task ExecuteAsync_Success_CommitsLedgerTransaction()
    {
        var file = CreateFile("ledger-commit.txt", "before");
        var run = CreateClaimedRun();
        var ledger = new OneCode.Infrastructure.Workflows.FileOperationLedger(Path.Combine(_root, "ledger"));
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

        await CreateRuntime(run, runner, coordinator, ledger).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);

        var input = CreateInput(run);
        var txn = await ledger.LoadAsync(input.OperationId, TestContext.Current.CancellationToken);
        txn.Should().NotBeNull();
        txn!.IsCommitted.Should().BeTrue();
        txn.Evidence.Should().Contain("build-attempt");
    }

    [Fact]
    public async Task ExecuteAsync_AgentFailure_LeavesUncommittedTransactionForNextGeneration()
    {
        var file = CreateFile("ledger-fail.txt", "before");
        var run = CreateClaimedRun();
        var ledger = new OneCode.Infrastructure.Workflows.FileOperationLedger(Path.Combine(_root, "ledger"));
        var runner = CreateRunner(async options =>
        {
            options.SharedTransaction!.Snapshot(file);
            await File.WriteAllTextAsync(file, "after", TestContext.Current.CancellationToken);
            throw new InvalidOperationException("agent failed");
        });
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.CompleteAsync(run.Id, Arg.Any<MainAgentRunResult>(), Arg.Any<CancellationToken>(), 7)
            .Returns(run with { State = BuildRunState.Failed, Version = 2 });

        var act = () => CreateRuntime(run, runner, coordinator, ledger).ExecuteAsync(
            CreateInput(run),
            TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var input = CreateInput(run);
        var txn = await ledger.LoadAsync(input.OperationId, TestContext.Current.CancellationToken);
        txn.Should().NotBeNull();
        txn!.IsCommitted.Should().BeFalse();
        // 新世代 ReconcileRunAsync 清掉本 Run 的未提交残留（内存已回滚，此处文件已恢复，幂等无害）。
        var results = await ledger.ReconcileRunAsync($"build/{run.Id}", TestContext.Current.CancellationToken);
        results.Should().ContainSingle(result => result.OperationId == input.OperationId);
        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should().Be("before");
    }

    private ControlledBuildAttemptRuntime CreateRuntime(
        BuildRun run,
        IMainAgentRunner runner,
        IBuildRunCoordinator coordinator,
        OneCode.Core.Workflows.IOperationLedger? ledger = null,
        MainAgentRunOptions? options = null)
    {
        var store = Substitute.For<IBuildRunStore>();
        store.LoadByIdAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
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
                buildRun => buildRun,
                Ledger: ledger));
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