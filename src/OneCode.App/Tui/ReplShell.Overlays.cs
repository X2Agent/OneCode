namespace OneCode.App.Tui;

/// <summary>
/// Overlay management for <see cref="ReplShell"/>.
/// Extracted as a partial to keep the main file under the 300-line guideline.
///
/// Hosts review/diff overlays and the completion popup lifecycle.
/// </summary>
public sealed partial class ReplShell
{
    private void ShowOverlay(View overlay)
    {
        _overlayHost.Visible = true;
        // Host was often layout-skipped while Visible=false; nudge layout so
        // Position() can read a real Viewport (or SuperView Frame fallback).
        SetNeedsLayout();
        _overlayHost.Push(overlay);
        _overlayHost.RepositionAll();
    }

    /// <summary>
    /// 异步获取 git 变更文件列表后显示 ReviewOverlay，避免在 UI 线程同步执行 git diff。
    /// </summary>
    public Task ShowReviewOverlayAsync()
    {
        return ShowReviewOverlayCoreAsync();
    }

    private async Task ShowReviewOverlayCoreAsync()
    {
        IReadOnlyList<ReviewFileEntry> files = _gitHelper is null
            ? []
            : await _gitHelper.GetPendingDiffStatAsync(ct: default).ConfigureAwait(true);
        _app.Invoke(() =>
        {
            var review = new ReviewOverlay(files);
            review.FileSelected += file =>
            {
                _ = LoadDiffAndShowAsync(file.Path);
            };
            ShowOverlay(review);
        });
    }

    private async Task LoadDiffAndShowAsync(string filePath)
    {
        var diffText = _gitHelper is null
            ? ""
            : await _gitHelper.GetFileDiffAgainstHeadAsync(filePath).ConfigureAwait(true);
        _app.Invoke(() =>
        {
            var overlay = new DiffDetailOverlay(_app, filePath, diffText ?? "");
            ShowOverlay(overlay);
            overlay.SetNeedsDraw();
        });
    }

    private void OnCompletionStateChanged(bool visible, int height)
    {
        if (visible && !_completionVisible)
        {
            _completionVisible = true;
            Add(_completionOverlay);
            PositionCompletionOverlay(height);
            _completionOverlay.SetNeedsDraw();
        }
        else if (visible && _completionVisible)
        {
            PositionCompletionOverlay(height);
            _completionOverlay.SetNeedsDraw();
        }
        else if (!visible && _completionVisible)
        {
            _completionVisible = false;
            Remove(_completionOverlay);
            SetNeedsDraw();
        }
    }

    private void PositionCompletionOverlay(int height)
    {
        var inputY = _chatInput.Frame.Y;
        _completionOverlay.X = 1;
        _completionOverlay.Y = inputY - height;
        _completionOverlay.Width = Dim.Fill() - 1;
        _completionOverlay.Height = height;
    }
}
