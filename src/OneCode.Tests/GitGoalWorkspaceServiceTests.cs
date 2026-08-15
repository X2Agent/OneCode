using NSubstitute;
using OneCode.Core.Build;
using OneCode.Core.Commands;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Goals;

namespace OneCode.Tests;

public sealed class GitGoalWorkspaceServiceTests
{
    [Fact]
    public async Task Prepare_DirtyWorkspace_ThrowsWithRepositoryRootAndDirtyCount()
    {
        var git = Substitute.For<IGitHelper>();
        var run = CreateRun() with { WorkingDirectory = "C:/repo/.dev-workspace" };
        git.GetRepositoryRootAsync(run.WorkingDirectory, Arg.Any<CancellationToken>()).Returns("C:/repo");
        git.CountPorcelainChangesAsync("C:/repo", Arg.Any<CancellationToken>()).Returns(2);
        git.RunAsync(
                Arg.Is<string[]>(args => args.SequenceEqual(new[] { "status", "--porcelain" })),
                "C:/repo",
                Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(true, " M src/A.cs\n?? .dev-workspace/notes.md\n", ""));
        var service = new GitGoalWorkspaceService(git, Substitute.For<IWorkspaceFingerprintProvider>());

        var act = () => service.PrepareAsync(run, TestContext.Current.CancellationToken);

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Contain("clean Git working tree");
        error.Which.Message.Should().Contain("C:/repo");
        error.Which.Message.Should().Contain(".dev-workspace");
        error.Which.Message.Should().Contain("2 dirty path");
        error.Which.Message.Should().Contain("src/A.cs");
        error.Which.Message.Should().Contain(".dev-workspace/notes.md");
        await git.DidNotReceive().RunAsync(
            Arg.Is<string[]>(args => args.Length > 0 && args[0] == "worktree"),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prepare_CreatesStableRunScopedWorktreeWithoutSessionState()
    {
        var git = Substitute.For<IGitHelper>();
        var fingerprints = Substitute.For<IWorkspaceFingerprintProvider>();
        var run = CreateRun();
        git.GetRepositoryRootAsync(run.WorkingDirectory, Arg.Any<CancellationToken>()).Returns("C:/repo");
        git.CountPorcelainChangesAsync("C:/repo", Arg.Any<CancellationToken>()).Returns(0);
        git.RunAsync(Arg.Any<string[]>(), "C:/repo", Arg.Any<CancellationToken>())
            .Returns(call => GitResult(call.ArgAt<string[]>(0)));
        fingerprints.ComputeAsync("C:/repo", Arg.Any<CancellationToken>()).Returns("fingerprint-a");
        var service = new GitGoalWorkspaceService(git, fingerprints);

        var result = await service.PrepareAsync(run, TestContext.Current.CancellationToken);

        result.WorkspaceId.Should().Be($"goal-{run.Id}");
        result.WorktreeBranch.Should().Be($"onecode/goal/{run.Id}");
        result.IsolatedPath.Replace('\\', '/').Should().EndWith($"/.onecode/goal-worktrees/{run.Id}");
        await git.Received().RunAsync(
            Arg.Is<string[]>(args => args.SequenceEqual(new[] { "worktree", "add", "-b", result.WorktreeBranch, result.IsolatedPath, "base-head" })),
            "C:/repo",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_ReplayUsesOperationMarkerBeforeDriftChecks()
    {
        var git = Substitute.For<IGitHelper>();
        var fingerprints = Substitute.For<IWorkspaceFingerprintProvider>();
        var run = CreateClaimedRun();
        git.RunAsync(Arg.Any<string[]>(), "C:/repo", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<string[]>(0);
                if (args[0] == "log") return new GitCommandResult(true, "published-commit", "");
                if (args[0] == "diff") return new GitCommandResult(true, "src/A.cs\ntests/A.cs", "");
                throw new InvalidOperationException($"Unexpected git call: {string.Join(' ', args)}");
            });
        var service = new GitGoalWorkspaceService(git, fingerprints);

        var receipt = await service.PublishAsync(run, 7, TestContext.Current.CancellationToken);

        receipt.Replayed.Should().BeTrue();
        receipt.ResultHash.Should().Be("published-commit");
        receipt.ChangedFiles.Should().Equal("src/A.cs", "tests/A.cs");
        await fingerprints.DidNotReceiveWithAnyArgs().ComputeAsync(default!, default);
    }

    [Fact]
    public async Task Publish_FirstExecutionCommitsAndCherryPicks()
    {
        var git = Substitute.For<IGitHelper>();
        var fingerprints = Substitute.For<IWorkspaceFingerprintProvider>();
        var run = CreateClaimedRun();
        git.CountPorcelainChangesAsync("C:/repo/.onecode/goal-worktrees/run", Arg.Any<CancellationToken>()).Returns(0);
        fingerprints.ComputeAsync("C:/repo", Arg.Any<CancellationToken>()).Returns("fingerprint-a");
        var targetHeadReads = 0;
        git.RunAsync(Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<string[]>(0);
                return args[0] switch
                {
                    "log" => new GitCommandResult(true, "", ""),
                    "symbolic-ref" => new GitCommandResult(true, "main", ""),
                    "rev-parse" when args[1] == "HEAD" && call.ArgAt<string>(1).EndsWith("run", StringComparison.Ordinal) => new GitCommandResult(true, "publish-marker-commit", ""),
                    "rev-parse" when args[1] == "HEAD" => new GitCommandResult(
                        true,
                        ++targetHeadReads == 1 ? "base-head" : "published-head",
                        ""),
                    "rev-list" => new GitCommandResult(true, "step-commit\npublish-marker-commit", ""),
                    "commit" or "cherry-pick" => new GitCommandResult(true, "", ""),
                    "diff" => new GitCommandResult(true, "src/A.cs", ""),
                    _ => throw new InvalidOperationException($"Unexpected git call: {string.Join(' ', args)}"),
                };
            });
        var service = new GitGoalWorkspaceService(git, fingerprints);

        var receipt = await service.PublishAsync(run, 7, TestContext.Current.CancellationToken);

        receipt.Replayed.Should().BeFalse();
        receipt.ChangedFiles.Should().ContainSingle("src/A.cs");
        await git.Received().RunAsync(
            Arg.Is<string[]>(args => args.SequenceEqual(new[] { "cherry-pick", "--allow-empty", "step-commit", "publish-marker-commit" })),
            "C:/repo",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StepReceipt_FirstExecutionStoresEvidenceBlobAndCommitsStableOperation()
    {
        var git = Substitute.For<IGitHelper>();
        var evidence = CreateEvidence(1);
        git.RunAsync(Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string[]>(0)[0] switch
            {
                "log" => new GitCommandResult(true, "", ""),
                "hash-object" => new GitCommandResult(true, "evidence-blob", ""),
                "add" or "commit" => new GitCommandResult(true, "", ""),
                "rev-parse" => new GitCommandResult(true, "step-commit", ""),
                _ => throw new InvalidOperationException($"Unexpected git call: {string.Join(' ', call.ArgAt<string[]>(0))}"),
            });
        var service = new GitGoalWorkspaceService(git, Substitute.For<IWorkspaceFingerprintProvider>());

        var receipt = await service.RecordStepAsync(
            CreateClaimedRun(), evidence, 7, TestContext.Current.CancellationToken);

        receipt.Replayed.Should().BeFalse();
        receipt.Commit.Should().Be("step-commit");
        await git.Received().RunAsync(
            Arg.Is<string[]>(args => args[0] == "commit"
                && args[args.Length - 1].Contains("OneCode-Operation-Id: goal/run/step/1", StringComparison.Ordinal)
                && args[args.Length - 1].Contains("OneCode-Evidence-Blob: evidence-blob", StringComparison.Ordinal)
                && !args[args.Length - 1].Contains("AgentOutput", StringComparison.Ordinal)),
            "C:/repo/.onecode/goal-worktrees/run",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StepReceipt_ReplayRestoresEvidenceWithoutNewCommit()
    {
        var git = Substitute.For<IGitHelper>();
        var evidence = CreateEvidence(1);
        var evidenceJson = System.Text.Json.JsonSerializer.Serialize(evidence);
        git.RunAsync(Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string[]>(0)[0] switch
            {
                "log" => new GitCommandResult(true, "step-commit", ""),
                "show" => new GitCommandResult(true, "evidence-blob", ""),
                "cat-file" => new GitCommandResult(true, evidenceJson, ""),
                _ => throw new InvalidOperationException($"Unexpected git call: {string.Join(' ', call.ArgAt<string[]>(0))}"),
            });
        var service = new GitGoalWorkspaceService(git, Substitute.For<IWorkspaceFingerprintProvider>());

        var receipt = await service.RecordStepAsync(
            CreateClaimedRun(), evidence, 7, TestContext.Current.CancellationToken);

        receipt.Replayed.Should().BeTrue();
        receipt.Commit.Should().Be("step-commit");
        receipt.Evidence.Should().BeEquivalentTo(evidence);
        await git.DidNotReceive().RunAsync(
            Arg.Is<string[]>(args => args[0] == "add" || args[0] == "commit"),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_StaleTokenFailsBeforeGit()
    {
        var git = Substitute.For<IGitHelper>();
        var service = new GitGoalWorkspaceService(git, Substitute.For<IWorkspaceFingerprintProvider>());
        var act = () => service.PublishAsync(CreateClaimedRun(), 8, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*");
        await git.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    private static GoalStepExecutionEvidence CreateEvidence(int goalId) => new(
        goalId,
        GoalStepState.Completed,
        1,
        10,
        5,
        "done",
        "accepted",
        ["src/A.cs"],
        [],
        [new GoalGateEvidence("test", true, false, "passed")],
        []);

    private static GoalRun CreateRun() => new()
    {
        Id = new GoalRunId("run"),
        SessionId = SessionId.NewId(),
        Goal = "goal",
        WorkingDirectory = "C:/repo",
        WorkspaceFingerprint = "fingerprint-a",
        DefinitionHash = "definition-a",
    };

    private static GoalRun CreateClaimedRun() => CreateRun() with
    {
        WorkflowFencingToken = 7,
        Workspace = new GoalWorkspaceSnapshot(
            "goal-run",
            "C:/repo",
            "C:/repo/.onecode/goal-worktrees/run",
            "onecode/goal/run",
            "main",
            "base-head",
            "fingerprint-a",
            DateTimeOffset.UtcNow),
    };

    private static GitCommandResult GitResult(string[] args) => args[0] switch
    {
        "rev-parse" => new(true, "base-head", ""),
        "symbolic-ref" => new(true, "main", ""),
        "worktree" => new(true, "", ""),
        _ => throw new InvalidOperationException($"Unexpected git call: {string.Join(' ', args)}"),
    };
}
