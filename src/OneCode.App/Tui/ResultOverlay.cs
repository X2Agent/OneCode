namespace OneCode.App.Tui;

/// <summary>Reason why an overlay was dismissed by its host.</summary>
public enum OverlayCloseReason
{
    Escape,
    Cancelled,
    Programmatic,
    HostShutdown,
}

/// <summary>Contract for overlays whose pending result must complete before removal.</summary>
public interface IOverlayDismissible
{
    void Dismiss(OverlayCloseReason reason);
}

/// <summary>
/// Base class for result-bearing overlays. It owns cancellation registration and ensures
/// that every close path completes the result task exactly once.
/// </summary>
public abstract class ResultOverlay<TResult> : CenteredOverlay, IOverlayDismissible
{
    private readonly TaskCompletionSource<TResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _closeOverlay;
    private int _closeRequested;

    protected ResultOverlay(string title, int preferredWidth, int preferredHeight)
        : base(title, preferredWidth, preferredHeight)
    {
    }

    public async Task<TResult> ShowAsync(
        Action<View> pushOverlay,
        Action closeOverlay,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pushOverlay);
        ArgumentNullException.ThrowIfNull(closeOverlay);

        _closeOverlay = closeOverlay;
        pushOverlay(this);

        using var registration = ct.Register(
            static state => ((ResultOverlay<TResult>)state!).RequestClose(OverlayCloseReason.Cancelled),
            this);
        return await _completion.Task.ConfigureAwait(false);
    }

    public void Dismiss(OverlayCloseReason reason) => _completion.TrySetResult(GetDismissedResult(reason));

    protected abstract TResult GetDismissedResult(OverlayCloseReason reason);

    protected bool Complete(TResult result)
    {
        if (!_completion.TrySetResult(result))
            return false;

        RequestHostClose();
        return true;
    }

    protected void RequestClose(OverlayCloseReason reason)
    {
        _completion.TrySetResult(GetDismissedResult(reason));
        RequestHostClose();
    }

    protected override bool OnKeyDown(Key kb)
    {
        if (kb == Key.Esc)
        {
            RequestClose(OverlayCloseReason.Escape);
            return true;
        }

        return base.OnKeyDown(kb);
    }

    private void RequestHostClose()
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) == 0)
            _closeOverlay?.Invoke();
    }
}
