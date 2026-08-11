using OneCode.App.Tui;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OneCode.Tests;

public sealed class OverlayHostTests
{
    [Fact]
    public void Push_FocusesDeclaredInitialControl()
    {
        var host = new OverlayHost(() => { });
        var overlay = new TestOverlay("first");

        host.Push(overlay);

        host.Depth.Should().Be(1);
        overlay.InitialControl.HasFocus.Should().BeTrue();
    }

    [Fact]
    public void CloseTop_WhenNested_RestoresUnderlyingOverlayFocus()
    {
        var backgroundFocusCount = 0;
        var host = new OverlayHost(() => backgroundFocusCount++);
        var first = new TestOverlay("first");
        var second = new TestOverlay("second");

        host.Push(first);
        host.Push(second);
        host.CloseTop(OverlayCloseReason.Programmatic);

        host.Depth.Should().Be(1);
        host.Top.Should().BeSameAs(first);
        first.InitialControl.HasFocus.Should().BeTrue();
        backgroundFocusCount.Should().Be(0);
    }

    [Fact]
    public void CloseTop_WhenLastOverlay_OnlyThenRestoresBackgroundFocus()
    {
        var backgroundFocusCount = 0;
        var host = new OverlayHost(() => backgroundFocusCount++);
        var overlay = new TestOverlay("only");

        host.Push(overlay);
        host.CloseTop(OverlayCloseReason.Programmatic);

        host.Depth.Should().Be(0);
        host.IsOverlayVisible.Should().BeFalse();
        backgroundFocusCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleEsc_CompletesPendingResultBeforeRemovingOverlay()
    {
        var host = new OverlayHost(() => { });
        var overlay = new TestResultOverlay();
        var resultTask = overlay.ShowAsync(host.Push, () => host.Pop());

        host.HandleEsc().Should().BeTrue();

        (await resultTask).Should().Be("dismissed:Escape");
        host.Depth.Should().Be(0);
    }

    [Fact]
    public async Task CancellationToken_RemainsRegisteredUntilOverlayCompletes()
    {
        var host = new OverlayHost(() => { });
        var overlay = new TestResultOverlay();
        using var cts = new CancellationTokenSource();
        var resultTask = overlay.ShowAsync(host.Push, () => host.Pop(), cts.Token);

        cts.Cancel();

        (await resultTask).Should().Be("dismissed:Cancelled");
        host.Depth.Should().Be(0);
    }

    private sealed class TestOverlay : CenteredOverlay
    {
        public TextField InitialControl { get; } = new() { Text = "focus" };

        protected override View? InitialFocusView => InitialControl;

        public TestOverlay(string title)
            : base(title)
        {
            Add(InitialControl);
        }
    }

    private sealed class TestResultOverlay : ResultOverlay<string>
    {
        public TestResultOverlay()
            : base("result", 40, 10)
        {
        }

        protected override string GetDismissedResult(OverlayCloseReason reason) => $"dismissed:{reason}";
    }
}
