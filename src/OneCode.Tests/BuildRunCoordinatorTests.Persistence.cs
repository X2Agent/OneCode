// BuildRunCoordinatorTests partial：JsonBuildRunStore 持久化与 claim/fencing
using OneCode.Core.Build;
using OneCode.Infrastructure.Build;
namespace OneCode.Tests;

public sealed partial class BuildRunCoordinatorTests
{
    [Fact]
    public async Task JsonStore_ApprovedToolPolicy_RoundTrips()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            ApprovedToolPolicy = new ApprovedToolPolicy(["ReadFile", "Write", "Edit"]),
            PlanApprovedAt = now,
            PlanApprovalSource = "runtime-approved",
            CreatedAt = now,
            UpdatedAt = now,
        };

        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);

        var saved = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        saved!.ApprovedToolPolicy!.ToolNames.Should().BeEquivalentTo(["ReadFile", "Write", "Edit"]);
        saved.PlanApprovalSource.Should().Be("runtime-approved");
        saved.PlanApprovedAt.Should().Be(now);
        saved.State.Should().Be(BuildRunState.Implementing);
    }

    [Fact]
    public async Task JsonStore_FirstSaveWithNonZeroSequence_UsesAggregateVersionForCas()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            SequenceNumber = 7,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 0,
        };

        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);

        var saved = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        saved!.Version.Should().Be(1);
        saved.SequenceNumber.Should().Be(7);
    }

    [Fact]
    public async Task JsonStore_CorruptPrimary_RecoversValidBackup()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await store.LoadAsync(conversationId, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(
            first with { SequenceNumber = 1 },
            first.Version,
            TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, conversationId.Value, "run.json");
        File.WriteAllText(path, "{ corrupt");

        var recovered = await store.LoadAsync(conversationId, TestContext.Current.CancellationToken);

        recovered.Should().NotBeNull();
        recovered!.Id.Should().Be(run.Id);
        recovered.Version.Should().Be(2);
        recovered.SequenceNumber.Should().Be(1);
    }

    [Fact]
    public async Task JsonStore_Replay_ReturnsLatestPersistedAggregate()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            SequenceNumber = 7,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(
            first with
            {
                State = BuildRunState.Recovering,
                SequenceNumber = 8,
            },
            first.Version,
            TestContext.Current.CancellationToken);

        var events = await store.LoadEventsAsync(run.Id, TestContext.Current.CancellationToken);
        var replayed = await store.ReplayAsync(run.Id, TestContext.Current.CancellationToken);

        events.Should().HaveCount(2);
        events.Select(item => item.EventId).Should().OnlyHaveUniqueItems();
        events.Select(item => item.Version).Should().ContainInOrder(1, 2);
        events[1].FromState.Should().Be(BuildRunState.Implementing);
        events[1].ToState.Should().Be(BuildRunState.Recovering);
        replayed.Should().NotBeNull();
        replayed!.Version.Should().Be(2);
        replayed.State.Should().Be(BuildRunState.Recovering);
        replayed.SequenceNumber.Should().Be(8);
    }

    [Fact]
    public async Task JsonStore_EventSequenceWithoutCheckpoint_RecoversLatestAggregate()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, conversationId.Value, "run.json");
        File.Delete(path);

        var recovered = await store.LoadAsync(conversationId, TestContext.Current.CancellationToken);

        recovered.Should().NotBeNull();
        recovered!.Id.Should().Be(run.Id);
        recovered.Version.Should().Be(1);
        recovered.State.Should().Be(BuildRunState.Implementing);
    }

    [Fact]
    public async Task JsonStore_SeparateInstances_RejectConcurrentStaleVersion()
    {
        var firstStore = new JsonBuildRunStore(_root);
        var secondStore = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await firstStore.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var current = (await firstStore.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;

        var saves = new[]
        {
            firstStore.SaveAsync(current with { SequenceNumber = 1 }, 1, TestContext.Current.CancellationToken),
            secondStore.SaveAsync(current with { SequenceNumber = 2 }, 1, TestContext.Current.CancellationToken),
        };
        var results = await Task.WhenAll(saves.Select(async save =>
        {
            try
            {
                await save;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        results.Count(succeeded => succeeded).Should().Be(1);
        results.Count(succeeded => !succeeded).Should().Be(1);
        var events = await firstStore.LoadEventsAsync(run.Id, TestContext.Current.CancellationToken);
        events.Select(item => item.Version).Should().ContainInOrder(1, 2);
    }

    [Fact]
    public async Task JsonStore_CorruptEventPrimaryAndBackup_FailsClosed()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await store.LoadAsync(conversationId, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(first, first.Version, TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, conversationId.Value, "events.json");
        File.WriteAllText(path, "{ corrupt-primary");
        File.WriteAllText(path + ".bak", "{ corrupt-backup");

        var act = () => store.LoadAsync(conversationId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*event sequence*corrupt or incompatible*");
    }

    [Fact]
    public async Task JsonStore_CorruptPrimaryAndBackup_FailsClosed()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var conversationId = SessionId.NewId();
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var first = (await store.LoadAsync(conversationId, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(first, first.Version, TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, conversationId.Value, "run.json");
        File.WriteAllText(path, "{ corrupt-primary");
        File.WriteAllText(path + ".bak", "{ corrupt-backup");

        var act = () => store.LoadAsync(conversationId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*corrupt or incompatible*");
    }

    [Fact]
    public async Task JsonStore_ConcurrentSave_RejectsStaleVersion()
    {
        var store = new JsonBuildRunStore(_root);
        var now = DateTimeOffset.UtcNow;
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 0,
        };
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var current = (await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;
        await store.SaveAsync(current with { SequenceNumber = 1 }, 1, TestContext.Current.CancellationToken);

        var act = () => store.SaveAsync(
            current with { SequenceNumber = 2 },
            1,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Concurrency conflict*");
    }

    [Fact]
    public async Task Coordinator_ClaimedRun_RejectsMissingAndStaleAttemptTokens()
    {
        var store = new JsonBuildRunStore(_root);
        var initial = CreatePersistableRun();
        await store.SaveAsync(initial, 0, TestContext.Current.CancellationToken);
        var persisted = (await store.LoadByIdAsync(initial.Id, TestContext.Current.CancellationToken))!;
        var claimed = await store.ClaimWorkflowAsync(
            initial.Id,
            fencingToken: 8,
            persisted.Version,
            TestContext.Current.CancellationToken);
        var coordinator = CreateCoordinator(store, claimed.WorkspaceFingerprint!);

        var missing = () => coordinator.BeginVerificationAsync(
            claimed.Id,
            TestContext.Current.CancellationToken);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        var stale = () => coordinator.BeginVerificationAsync(
            claimed.Id,
            TestContext.Current.CancellationToken,
            expectedWorkflowFencingToken: 7);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        var verifying = await coordinator.BeginVerificationAsync(
            claimed.Id,
            TestContext.Current.CancellationToken,
            expectedWorkflowFencingToken: 8);
        verifying.State.Should().Be(BuildRunState.Verifying);
        verifying.WorkflowFencingToken.Should().Be(8);
    }

    [Fact]
    public async Task JsonStore_WorkflowClaim_RequiresMonotonicFencingToken()
    {
        var store = new JsonBuildRunStore(_root);
        var run = CreatePersistableRun();
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var persisted = (await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;

        var firstClaim = await store.ClaimWorkflowAsync(
            run.Id,
            fencingToken: 10,
            persisted.Version,
            TestContext.Current.CancellationToken);
        firstClaim.WorkflowFencingToken.Should().Be(10);

        var staleClaim = () => store.ClaimWorkflowAsync(
            run.Id,
            fencingToken: 9,
            firstClaim.Version,
            TestContext.Current.CancellationToken);
        await staleClaim.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        var takeover = await store.ClaimWorkflowAsync(
            run.Id,
            fencingToken: 11,
            firstClaim.Version,
            TestContext.Current.CancellationToken);
        takeover.WorkflowFencingToken.Should().Be(11);
    }

    [Fact]
    public async Task JsonStore_ClaimedRun_RejectsUnfencedAndStaleWrites()
    {
        var store = new JsonBuildRunStore(_root);
        var run = CreatePersistableRun();
        await store.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var persisted = (await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;
        var claimed = await store.ClaimWorkflowAsync(
            run.Id,
            fencingToken: 20,
            persisted.Version,
            TestContext.Current.CancellationToken);

        var unfenced = () => store.SaveAsync(
            claimed with { SequenceNumber = 1 },
            claimed.Version,
            TestContext.Current.CancellationToken);
        await unfenced.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        var stale = () => store.SaveFencedAsync(
            claimed with { SequenceNumber = 1, WorkflowFencingToken = 19 },
            claimed.Version,
            fencingToken: 19,
            TestContext.Current.CancellationToken);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*token*");

        await store.SaveFencedAsync(
            claimed with { SequenceNumber = 1 },
            claimed.Version,
            fencingToken: 20,
            TestContext.Current.CancellationToken);
        var saved = await store.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        saved!.SequenceNumber.Should().Be(1);
        saved.WorkflowFencingToken.Should().Be(20);
    }

    private BuildRun CreatePersistableRun()
    {
        var now = DateTimeOffset.UtcNow;
        return new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            IntakePrompt = "implement",
            WorkingDirectory = _root,
            WorkspaceFingerprint = "fingerprint",
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}







