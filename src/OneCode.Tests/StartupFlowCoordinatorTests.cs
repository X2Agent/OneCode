using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class StartupFlowCoordinatorTests
{
    [Fact]
    public async Task RunInteractiveAsync_SkipsTrust_WhenAlreadyAccepted()
    {
        var ensureCalls = 0;
        var coordinator = new StartupFlowCoordinator(
            shouldShowTrustPrompt: () => false,
            ensureTrustAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(true);
            });

        var result = await coordinator.RunInteractiveAsync(TestContext.Current.CancellationToken);

        result.ShouldContinue.Should().BeTrue();
        result.TrustConfirmed.Should().BeTrue();
        ensureCalls.Should().Be(0);
    }

    [Fact]
    public async Task RunInteractiveAsync_Stops_WhenTrustDeclined()
    {
        var coordinator = new StartupFlowCoordinator(
            shouldShowTrustPrompt: () => true,
            ensureTrustAsync: _ => Task.FromResult(false));

        var result = await coordinator.RunInteractiveAsync(TestContext.Current.CancellationToken);

        result.ShouldContinue.Should().BeFalse();
        result.TrustConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task RunInteractiveAsync_TrustAccepts()
    {
        var ensureCalls = 0;
        var coordinator = new StartupFlowCoordinator(
            shouldShowTrustPrompt: () => true,
            ensureTrustAsync: _ =>
            {
                ensureCalls++;
                return Task.FromResult(true);
            });

        var result = await coordinator.RunInteractiveAsync(TestContext.Current.CancellationToken);

        result.ShouldContinue.Should().BeTrue();
        result.TrustConfirmed.Should().BeTrue();
        ensureCalls.Should().Be(1);
    }
}
