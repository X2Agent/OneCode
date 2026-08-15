using OneCode.App.Services.PlanMode;
using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class PlanCardPublisherTests
{
    [Fact]
    public void Publish_WithSubscriber_ForwardsExactTitleStepsAndPhase()
    {
        var sut = new PlanCardPublisher();
        var steps = new List<PlanStep>
        {
            new("Step 1"),
            new("Step 2", Assignee: "agent", Status: PlanStepStatus.Done),
        };
        string? receivedTitle = null;
        IReadOnlyList<PlanStep>? receivedSteps = null;
        PlanCardPhase? receivedPhase = null;
        sut.PlanCreated += (title, s, phase) =>
        {
            receivedTitle = title;
            receivedSteps = s;
            receivedPhase = phase;
        };

        sut.Publish("Build feature X", steps, PlanCardPhase.PendingApproval);

        receivedTitle.Should().Be("Build feature X");
        receivedSteps.Should().NotBeNull();
        receivedSteps!.Should().HaveCount(2);
        receivedSteps[0].Label.Should().Be("Step 1");
        receivedSteps[1].Label.Should().Be("Step 2");
        receivedSteps[1].Assignee.Should().Be("agent");
        receivedSteps[1].Status.Should().Be(PlanStepStatus.Done);
        receivedPhase.Should().Be(PlanCardPhase.PendingApproval);
    }

    [Fact]
    public void Publish_WithoutSubscribers_DoesNotThrow()
    {
        // The TUI subscription is optional — CreatePlanTool calls Publish
        // unconditionally when steps are present. A NullReferenceException here
        // would crash the tool in headless/CI runs where no TUI is attached.
        var sut = new PlanCardPublisher();

        var act = () => sut.Publish("title", Array.Empty<PlanStep>(), PlanCardPhase.Draft);

        act.Should().NotThrow();
    }

    [Fact]
    public void Publish_WithMultipleSubscribers_NotifiesAllWithSameTitleAndPhase()
    {
        var sut = new PlanCardPublisher();
        var steps = new List<PlanStep> { new("Only step") };
        var titles = new List<string?>();
        var phases = new List<PlanCardPhase>();
        sut.PlanCreated += (t, _, p) => { titles.Add(t); phases.Add(p); };
        sut.PlanCreated += (t, _, p) => { titles.Add(t); phases.Add(p); };

        sut.Publish("Shared plan", steps, PlanCardPhase.Draft);

        titles.Should().HaveCount(2);
        titles[0].Should().Be("Shared plan");
        titles[1].Should().Be("Shared plan");
        phases.Should().AllBeEquivalentTo(PlanCardPhase.Draft);
    }

    [Theory]
    [InlineData(PlanCardPhase.Draft)]
    [InlineData(PlanCardPhase.PendingApproval)]
    public void Publish_ForwardsPhaseToSubscriber(PlanCardPhase phase)
    {
        // SavePlan 发布 Draft（TUI 仅展示卡片）；SubmitPlan 发布 PendingApproval（弹出 InlineSelector 决策面板）
        var sut = new PlanCardPublisher();
        PlanCardPhase? receivedPhase = null;
        sut.PlanCreated += (_, _, p) => receivedPhase = p;

        sut.Publish("A plan", [new PlanStep("Step")], phase);

        receivedPhase.Should().Be(phase);
    }
}
