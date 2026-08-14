using System.Text.Json;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.PlanMode;
using OneCode.App.Tui;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

public sealed class WorkingModeBridgeTests
{
    [Fact]
    public void ModeController_PlanThenBuild_RestoresAdvancedPermission()
    {
        var controller = new WorkingModeController(WorkingMode.Build);
        var provider = new PermissionModeProvider(TestConfigManager.Create());
        provider.SetCurrentMode(PermissionMode.BypassPermissions);
        var plan = new PlanModeService(provider, new PromptManager());
        using var bridge = new WorkingModeBridge(controller, provider, plan);

        controller.Mode = WorkingMode.Plan;
        provider.CurrentMode.Should().Be(PermissionMode.Plan);
        controller.Mode = WorkingMode.Build;

        plan.IsInPlanMode.Should().BeFalse();
        provider.CurrentMode.Should().Be(PermissionMode.BypassPermissions);
    }

    [Fact]
    public void ApplyMode_Plan_SynchronizesPermissionThroughPlanService()
    {
        var controller = new WorkingModeController();
        var provider = Substitute.For<IPermissionModeProvider>();
        provider.CurrentMode.Returns(PermissionMode.Default);
        var plan = Substitute.For<IPlanModeService>();
        using var bridge = new WorkingModeBridge(controller, provider, plan);

        bridge.ApplyMode(WorkingMode.Plan);

        plan.Received(1).EnterPlanMode();
    }

    [Fact]
    public void PlanPermission_DeniesDynamicPowerShell()
    {
        using var input = JsonDocument.Parse("""{"command":"Remove-Item -Recurse C:\\"}""");
        var result = PermissionProfiles.Check(
            PermissionMode.Plan,
            "PowerShell",
            input.RootElement,
            new ToolPermissionContext { Mode = PermissionMode.Plan });

        result.Decision.Should().Be(PermissionDecision.Deny);
    }
}
