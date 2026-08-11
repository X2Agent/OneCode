using NSubstitute;
using OneCode.App.Tui;
using OneCode.Core.Build;
using Terminal.Gui.App;

namespace OneCode.Tests;

public sealed class TranscriptEventPresenterTests
{
    [Fact]
    public void TryPresent_TranscriptEvent_ProjectsIntoTranscript()
    {
        var transcript = new ChatTranscriptView(CreateImmediateApp());
        var presenter = new TranscriptEventPresenter(transcript);

        transcript.BeginStreaming();

        presenter.TryPresent(new TuiThinkingDelta("Inspecting the request.")).Should().BeTrue();
        presenter.TryPresent(new TuiTextDelta("Done.")).Should().BeTrue();
        presenter.TryPresent(new TuiFileChange("Feature.cs", ["new line"], ["old line"])).Should().BeTrue();

        transcript.EndStreaming();
        var text = string.Join('\n', transcript.MessageView.RenderedLines);

        text.Should().Contain("Thought for");
        text.Should().Contain("Done.");
        text.Should().Contain("Feature.cs");
        text.Should().Contain("+new line");
        text.Should().Contain("-old line");
    }

    [Fact]
    public void TryPresent_CrossRegionEvent_ReturnsFalse()
    {
        var presenter = new TranscriptEventPresenter(
            new ChatTranscriptView(CreateImmediateApp()));

        presenter.TryPresent(new TuiDone(1, 2)).Should().BeFalse();
    }

    [Fact]
    public void TryPresent_BuildRunState_IgnoresNonIncreasingSequence()
    {
        var transcript = new ChatTranscriptView(CreateImmediateApp());
        var presenter = new TranscriptEventPresenter(transcript);
        var runId = new BuildRunId("br-test");

        transcript.BeginStreaming();
        presenter.TryPresent(CreateBuildState(runId, BuildRunState.Implementing, sequence: 2));
        presenter.TryPresent(CreateBuildState(runId, BuildRunState.Verifying, sequence: 2));

        var unchanged = string.Join('\n', transcript.MessageView.RenderedLines);
        unchanged.Should().Contain("正在执行任务");
        unchanged.Should().NotContain("正在运行验证");

        presenter.TryPresent(CreateBuildState(runId, BuildRunState.Verifying, sequence: 3));

        var updated = string.Join('\n', transcript.MessageView.RenderedLines);
        updated.Should().Contain("正在运行验证");
        updated.Should().NotContain("正在执行任务");
    }

    [Fact]
    public void Reset_AllowsReplayedBuildRunSequence()
    {
        var transcript = new ChatTranscriptView(CreateImmediateApp());
        var presenter = new TranscriptEventPresenter(transcript);
        var runId = new BuildRunId("br-resume");

        transcript.BeginStreaming();
        presenter.TryPresent(CreateBuildState(runId, BuildRunState.Verifying, sequence: 5));
        presenter.Reset();
        presenter.TryPresent(CreateBuildState(runId, BuildRunState.Implementing, sequence: 1));

        var text = string.Join('\n', transcript.MessageView.RenderedLines);
        text.Should().Contain("正在执行任务");
        text.Should().NotContain("正在运行验证");
    }

    private static TuiBuildRunState CreateBuildState(
        BuildRunId runId,
        BuildRunState state,
        long sequence) =>
        new(runId, state, sequence, []);

    private static IApplication CreateImmediateApp()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);
        return app;
    }
}
