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
}
