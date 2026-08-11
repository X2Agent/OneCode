using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services.BuildMode;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Infrastructure.Build;

namespace OneCode.Tests;

public sealed class BuildRunTuiReplayServiceTests
{
    [Fact]
    public async Task ReplayLatestAsync_UsesCanonicalEventReplayProjection()
    {
        var conversationId = SessionId.NewId();
        var runId = BuildRunId.New();
        var checkpoint = CreateRun(
            runId,
            conversationId,
            BuildRunState.Implementing,
            sequenceNumber: 3,
            version: 1);
        var replayed = CreateRun(
            runId,
            conversationId,
            BuildRunState.Verifying,
            sequenceNumber: 4,
            version: 2) with
        {
            Validations = [new BuildValidationRun(
                "validation-4",
                BuildValidationStatus.Pending,
                ["dotnet test"],
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)],
            ChangedFiles = ["Foo.cs"],
            Metrics = BuildRunMetrics.Empty with
            {
                TurnsCompleted = 7,
                EstimatedCost = 0.25m,
            },
        };
        var store = Substitute.For<IBuildRunStore>();
        var eventStore = Substitute.For<IBuildRunEventStore>();
        store.LoadAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(checkpoint);
        eventStore.ReplayAsync(runId, Arg.Any<CancellationToken>())
            .Returns(replayed);
        var service = new BuildRunTuiReplayService(
            store,
            eventStore,
            NullLogger<BuildRunTuiReplayService>.Instance);

        var result = await service.ReplayLatestAsync(
            conversationId,
            TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.State.Should().Be(BuildRunState.Verifying);
        result.SequenceNumber.Should().Be(4);
        result.ValidationStatus.Should().Be(BuildValidationStatus.Pending);
        result.ChangedFiles.Should().Be(1);
        result.TurnsCompleted.Should().Be(7);
        result.EstimatedCost.Should().Be(0.25m);
        await eventStore.Received(1).ReplayAsync(runId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplayLatestAsync_AcrossStoreInstances_ProjectsLatestDurableEvent()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "onecode-build-tui-replay-tests",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        try
        {
            var conversationId = SessionId.NewId();
            var runId = BuildRunId.New();
            var writer = new JsonBuildRunStore(root);
            var initial = CreateRun(
                runId,
                conversationId,
                BuildRunState.Implementing,
                sequenceNumber: 1,
                version: 0);
            await writer.SaveAsync(initial, 0, TestContext.Current.CancellationToken);
            var persisted = (await writer.LoadAsync(
                conversationId,
                TestContext.Current.CancellationToken))!;
            await writer.SaveAsync(
                persisted with
                {
                    State = BuildRunState.Accepting,
                    SequenceNumber = 2,
                    Validations = [new BuildValidationRun(
                        "validation-2",
                        BuildValidationStatus.Passed,
                        [],
                        ["all passed"],
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow)],
                },
                persisted.Version,
                TestContext.Current.CancellationToken);

            var reader = new JsonBuildRunStore(root);
            var service = new BuildRunTuiReplayService(
                reader,
                reader,
                NullLogger<BuildRunTuiReplayService>.Instance);

            var result = await service.ReplayLatestAsync(
                conversationId,
                TestContext.Current.CancellationToken);

            result.Should().NotBeNull();
            result!.RunId.Should().Be(runId);
            result.State.Should().Be(BuildRunState.Accepting);
            result.SequenceNumber.Should().Be(2);
            result.ValidationStatus.Should().Be(BuildValidationStatus.Passed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplayLatestAsync_MismatchedConversation_FailsClosed()
    {
        var conversationId = SessionId.NewId();
        var runId = BuildRunId.New();
        var store = Substitute.For<IBuildRunStore>();
        var eventStore = Substitute.For<IBuildRunEventStore>();
        store.LoadAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns(CreateRun(runId, conversationId, BuildRunState.Implementing, 1, 1));
        eventStore.ReplayAsync(runId, Arg.Any<CancellationToken>())
            .Returns(CreateRun(runId, SessionId.NewId(), BuildRunState.Verifying, 2, 2));
        var service = new BuildRunTuiReplayService(
            store,
            eventStore,
            NullLogger<BuildRunTuiReplayService>.Instance);

        var act = () => service.ReplayLatestAsync(
            conversationId,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*does not belong to conversation*");
    }

    [Fact]
    public void BuildRunStateEvent_From_PreservesScopeValidationAndProgress()
    {
        var scope = new BuildScopeSnapshot(
            "Goal",
            ["One"],
            [],
            [],
            [],
            "user",
            DateTimeOffset.UtcNow);
        var run = CreateRun(
            BuildRunId.New(),
            SessionId.NewId(),
            BuildRunState.Accepting,
            9,
            3) with
        {
            Scope = scope,
            Plan = new BuildPlan(
                "Plan",
                [new BuildPlanTask(
                    "task-1",
                    "Task",
                    "Description",
                    [],
                    [],
                    [],
                    BuildTaskStatus.Completed)],
                [],
                [],
                []),
            Validations = [new BuildValidationRun(
                "validation-1",
                BuildValidationStatus.Passed,
                [],
                ["passed"],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)],
        };

        var mapped = TuiEventMapper.MapQueryEventToTuiEvent(BuildRunStateEvent.From(run));

        mapped.Should().BeOfType<TuiBuildRunState>().Which.Should().BeEquivalentTo(
            new
            {
                Scope = scope,
                ValidationStatus = (BuildValidationStatus?)BuildValidationStatus.Passed,
                CompletedTasks = 1,
                TotalTasks = 1,
            });
    }

    private static BuildRun CreateRun(
        BuildRunId runId,
        SessionId conversationId,
        BuildRunState state,
        long sequenceNumber,
        long version) => new()
        {
            Id = runId,
            ConversationId = conversationId,
            State = state,
            SequenceNumber = sequenceNumber,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
}
