using NSubstitute;
using OneCode.App.Services.Streaming;
using OneCode.App.Tui;
using Terminal.Gui.App;

namespace OneCode.Tests;

public sealed class WorkingModeTests
{
    [Fact]
    public void Cycle_BuildToPlan_ReturnsPlan()
    {
        var c = new WorkingModeController(WorkingMode.Build);
        c.CycleMode().Should().Be(WorkingMode.Plan);
        c.Mode.Should().Be(WorkingMode.Plan);
    }

    [Fact]
    public void Cycle_PlanToTeam_ReturnsTeam()
    {
        var c = new WorkingModeController(WorkingMode.Plan);
        c.CycleMode().Should().Be(WorkingMode.Team);
        c.Mode.Should().Be(WorkingMode.Team);
    }

    [Fact]
    public void Cycle_TeamToGoal_ReturnsGoal()
    {
        var c = new WorkingModeController(WorkingMode.Team);
        c.CycleMode().Should().Be(WorkingMode.Goal);
        c.Mode.Should().Be(WorkingMode.Goal);
    }

    [Fact]
    public void Cycle_GoalToBuild_ReturnsBuild()
    {
        var c = new WorkingModeController(WorkingMode.Goal);
        c.CycleMode().Should().Be(WorkingMode.Build);
        c.Mode.Should().Be(WorkingMode.Build);
    }

    [Fact]
    public void Cycle_FourTimesReturnsToStart()
    {
        var c = new WorkingModeController(WorkingMode.Build);
        c.CycleMode();
        c.CycleMode();
        c.CycleMode();
        c.CycleMode();
        c.Mode.Should().Be(WorkingMode.Build);
    }

    [Fact]
    public void ToggleStrategy_TeamMode_FlipsStrategy()
    {
        var c = new WorkingModeController(WorkingMode.Team, TeamStrategy.Magentic);
        c.ToggleStrategy().Should().Be(TeamStrategy.GroupChat);
        c.IsMagentic.Should().BeFalse();
        c.IsGroupChat.Should().BeTrue();
    }

    [Theory]
    [InlineData(WorkingMode.Build)]
    [InlineData(WorkingMode.Plan)]
    public void ToggleStrategy_OutsideTeamMode_NoOp(WorkingMode mode)
    {
        var c = new WorkingModeController(mode, TeamStrategy.Magentic);
        c.ToggleStrategy().Should().Be(TeamStrategy.Magentic);
        c.Strategy.Should().Be(TeamStrategy.Magentic);
    }

    [Fact]
    public void LeavingTeamMode_ResetsStrategyToConfig()
    {
        var c = new WorkingModeController(WorkingMode.Team, TeamStrategy.GroupChat);
        c.Mode = WorkingMode.Build;
        // leaving Team resets strategy implicitly (private field reset)
        // re-entering Team should follow the YAML-configured default strategy
        c.Mode = WorkingMode.Team;
        c.Strategy.Should().Be(TeamStrategy.Config);
        c.IsMagentic.Should().BeFalse();
        c.IsGroupChat.Should().BeFalse();
    }

    [Fact]
    public void SetStrategy_OutsideTeam_NoOp()
    {
        var c = new WorkingModeController(WorkingMode.Build);
        c.Strategy = TeamStrategy.GroupChat;
        c.Strategy.Should().Be(TeamStrategy.Config);
    }

    [Fact]
    public void SetStrategy_InsideTeam_Updates()
    {
        var c = new WorkingModeController(WorkingMode.Team);
        c.Strategy = TeamStrategy.GroupChat;
        c.Strategy.Should().Be(TeamStrategy.GroupChat);
    }

    [Fact]
    public void ModeChanged_FiresOnModeTransition()
    {
        var c = new WorkingModeController(WorkingMode.Build);
        var fired = 0;
        WorkingModeChangedEventArgs? args = null;
        c.ModeChanged += (_, e) => { fired++; args = e; };
        c.Mode = WorkingMode.Plan;
        fired.Should().Be(1);
        args!.PreviousMode.Should().Be(WorkingMode.Build);
        args.CurrentMode.Should().Be(WorkingMode.Plan);
    }

    [Fact]
    public void ModeChanged_DoesNotFireWhenModeIsUnchanged()
    {
        var c = new WorkingModeController(WorkingMode.Build);
        var fired = 0;
        c.ModeChanged += (_, _) => fired++;
        c.Mode = WorkingMode.Build;
        fired.Should().Be(0);
    }

    [Fact]
    public void ShowStrategyTag_TrueOnlyInTeam()
    {
        new WorkingModeController(WorkingMode.Build).ShowStrategyTag.Should().BeFalse();
        new WorkingModeController(WorkingMode.Plan).ShowStrategyTag.Should().BeFalse();
        new WorkingModeController(WorkingMode.Team).ShowStrategyTag.Should().BeTrue();
    }

    [Theory]
    [InlineData(WorkingMode.Build, TeamStrategy.Magentic, "BUILD")]
    [InlineData(WorkingMode.Plan, TeamStrategy.Magentic, "PLAN")]
    [InlineData(WorkingMode.Team, TeamStrategy.Config, "TEAM · Config")]
    [InlineData(WorkingMode.Team, TeamStrategy.Magentic, "TEAM · Magentic")]
    [InlineData(WorkingMode.Team, TeamStrategy.GroupChat, "TEAM · GroupChat")]
    public void ModeLabel_ReflectsModeAndStrategy(WorkingMode mode, TeamStrategy strategy, string expected)
    {
        new WorkingModeController(mode, strategy).ModeLabel.Should().Be(expected);
    }

    [Fact]
    public void TeamStrategyOverride_GroupChat_MapsToGroupChatWorkflow()
    {
        OrchestrationStreamService.ResolveTeamOverride(TeamStrategy.GroupChat)
            .Should().Be(OneCode.Core.Coordinator.TeamOrchestrationMode.GroupChat);
        OrchestrationStreamService.ResolveTeamOverride(TeamStrategy.Magentic)
            .Should().Be(OneCode.Core.Coordinator.TeamOrchestrationMode.Magentic);
        OrchestrationStreamService.ResolveTeamOverride(TeamStrategy.Config)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(0, "(no output)")]
    [InlineData(0, "")]
    [InlineData(1, "   ")]
    [InlineData(1, "  (no output)  ")]
    public void MissingTeamOutput_IsDetected(int turns, string output)
    {
        OrchestrationStreamService.IsMissingTeamOutput(
            new OneCode.Core.Coordinator.TeamRunResult("team", output, turns, false))
            .Should().BeTrue();
    }

    [Fact]
    public void AgentOutput_IsNotReportedAsMissing()
    {
        OrchestrationStreamService.IsMissingTeamOutput(
            new OneCode.Core.Coordinator.TeamRunResult("team", "done", 1, false))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(TeamStrategy.Config, "Config")]
    [InlineData(TeamStrategy.Magentic, "Magentic")]
    [InlineData(TeamStrategy.GroupChat, "GroupChat")]
    public void AgentStatusBarStrategyLabel_ReflectsConfigAndOverrides(
        TeamStrategy strategy,
        string expected)
    {
        AgentStatusBar.GetStrategyLabel(strategy).Should().Be(expected);
    }

    [Fact]
    public void AgentStatusBar_PreservesExplicitActivityWhenBusyStarts()
    {
        var app = Substitute.For<IApplication>();
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(new object());
        var statusBar = new AgentStatusBar(app, new WorkingModeController());

        statusBar.SetActivity("执行 /review");
        statusBar.SetBusy(true);

        statusBar.IsBusy.Should().BeTrue();
        statusBar.CurrentActivity.Should().Be("执行 /review");
    }

    [Fact]
    public void ModeTag_IgnoresStrategy()
    {
        new WorkingModeController(WorkingMode.Team, TeamStrategy.GroupChat)
            .ModeTag.Should().Be("TEAM");
    }
}
