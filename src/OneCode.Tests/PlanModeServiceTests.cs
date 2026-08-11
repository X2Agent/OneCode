using OneCode.App.Services;
using OneCode.App.Services.PlanMode;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

public sealed class PlanModeServiceTests : IDisposable
{
    private readonly PlanModeService _service;
    private readonly PermissionModeProvider _modeProvider;

    public PlanModeServiceTests()
    {
        _modeProvider = new PermissionModeProvider(new ConfigManager(
            Path.Combine(Path.GetTempPath(), "OneCodePlanModeTests-" + Guid.NewGuid().ToString("N")[..8])));
        _service = new PlanModeService(_modeProvider, new PromptManager());
    }

    [Fact]
    public void EnterAndExitPlanMode_SynchronizesPermissionMode()
    {
        _service.EnterPlanMode();

        _service.IsInPlanMode.Should().BeTrue();
        _modeProvider.CurrentMode.Should().Be(PermissionMode.Plan);

        var restored = _service.ExitPlanMode();

        restored.Should().Be(PermissionMode.Default);
        _service.IsInPlanMode.Should().BeFalse();
        _modeProvider.CurrentMode.Should().Be(PermissionMode.Default);
    }

    [Fact]
    public void RepeatedEnterAndExit_AreIdempotent()
    {
        _service.EnterPlanMode();
        _service.EnterPlanMode();
        _service.ExitPlanMode();
        var restored = _service.ExitPlanMode();

        restored.Should().Be(PermissionMode.Default);
        _service.IsInPlanMode.Should().BeFalse();
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
