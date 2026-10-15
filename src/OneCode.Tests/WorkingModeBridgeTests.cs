using System.Text.Json;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.PlanMode;
using OneCode.Core.Permissions;
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
        var plan = new PlanModeService(provider);
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
    public void PlanPermission_DeniesDynamicShell()
    {
        using var input = JsonDocument.Parse("""{"command":"Remove-Item -Recurse C:\\"}""");
        var result = PermissionProfiles.Check(
            PermissionMode.Plan,
            "Bash",
            input.RootElement,
            new ToolPermissionContext { Mode = PermissionMode.Plan });

        result.Decision.Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public void PlanPermission_AllowsHarnessFileMemoryWrite()
    {
        // Harness 注入的 file_memory_* 只读写 agent 会话状态，不触碰工作区文件——
        // Plan 模式必须放行：模型能看到工具、审批也通过，最后被白名单拒掉就是死循环。
        using var input = JsonDocument.Parse("""{"path":"notes.md","content":"x"}""");
        var result = PermissionProfiles.Check(
            PermissionMode.Plan,
            "file_memory_write",
            input.RootElement,
            new ToolPermissionContext { Mode = PermissionMode.Plan });

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public void PlanPermission_StillDeniesWorkspaceWrite()
    {
        // 反证守卫：放行 file_memory_write 不得把工作区写入工具一并放进来。
        using var input = JsonDocument.Parse("""{"filePath":"notes.md","content":"x"}""");
        var result = PermissionProfiles.Check(
            PermissionMode.Plan,
            "Write",
            input.RootElement,
            new ToolPermissionContext { Mode = PermissionMode.Plan });

        result.Decision.Should().Be(PermissionDecision.Deny);
    }
}
