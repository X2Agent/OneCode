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

    [Fact]
    public void TryPresent_TeamClarificationProgress_ShowsHeaderWithoutQuestionDetails()
    {
        // 澄清请求只保留摘要行；问题明细由澄清向导组件完整展示，
        // transcript 中重复打印问题全文会造成干扰（用户反馈）。
        var transcript = new ChatTranscriptView(CreateImmediateApp());
        var presenter = new TranscriptEventPresenter(transcript);

        transcript.BeginStreaming();
        presenter.TryPresent(new TuiTeamProgress(
            "团队 'impl' 需要澄清 2 个问题后才能规划",
            new List<(string Label, string Detail, string Status)>
            {
                ("问题 1", "第一个里程碑想验证什么核心能力？", "待回答"),
                ("问题 2", "交付物是什么？", "待回答"),
            },
            "请在随后的澄清向导中回答")).Should().BeTrue();
        transcript.EndStreaming();

        var text = string.Join('\n', transcript.MessageView.RenderedLines);
        text.Should().Contain("团队 'impl' 需要澄清 2 个问题后才能规划");
        text.Should().NotContain("里程碑");
        text.Should().NotContain("交付物是什么");
    }

    [Fact]
    public void TryPresent_GoalPlan_RendersNumberedStepList()
    {
        // P1：Goal 分解结果渲染为编号清单（一次性块），执行前用户可见计划。
        var transcript = new ChatTranscriptView(CreateImmediateApp());
        var presenter = new TranscriptEventPresenter(transcript);

        transcript.BeginStreaming();
        presenter.TryPresent(new TuiGoalPlan(["实现解析器", "补充单测"])).Should().BeTrue();
        transcript.EndStreaming();

        var text = string.Join('\n', transcript.MessageView.RenderedLines);
        text.Should().Contain("已分解为 2 个子目标");
        text.Should().Contain("1. 实现解析器");
        text.Should().Contain("2. 补充单测");
    }

    [Fact]
    public void TryPresent_GoalResult_RendersSummaryCard()
    {
        // P1：TuiGoalResult 不再被静默吞掉——渲染步骤结论 + 验证摘要。
        var transcript = new ChatTranscriptView(CreateImmediateApp());
        var presenter = new TranscriptEventPresenter(transcript);

        transcript.BeginStreaming();
        presenter.TryPresent(new TuiGoalResult(
            Completed: 2, Failed: 1, Skipped: 0, Total: 3, Committed: true,
            CompletedGoals: ["a", "b"], FailedGoals: ["c"], SkippedGoals: [],
            ValidationSummary: "[PASS] build: ok\n[FAIL] test: 1 failed")).Should().BeTrue();
        transcript.EndStreaming();

        var text = string.Join('\n', transcript.MessageView.RenderedLines);
        text.Should().Contain("✓ 完成 2 · ✗ 失败 1 · ○ 跳过 0");
        text.Should().Contain("[PASS] build: ok");
        text.Should().Contain("[FAIL] test: 1 failed");
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
