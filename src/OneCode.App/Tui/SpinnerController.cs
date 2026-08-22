namespace OneCode.App.Tui;

/// <summary>
/// 可复用的 Braille spinner 动画控制器。
///
/// 封装帧索引推进与 <see cref="IApplication.AddTimeout"/> 调度逻辑，
/// 供 <see cref="AgentStatusBar"/> 等需要 spinner 动画的视图组合使用，
/// 避免在多个 View 中重复实现 timeout/tick 代码。
///
/// 线程安全：仅在 Terminal.Gui 主循环上调用。
/// </summary>
internal sealed class SpinnerController
{
    /// <summary>
    /// Braille 盲文 spinner 帧序列（10 帧，循环播放）。
    /// 对齐 TS Ink spinner 的视觉表现。
    /// </summary>
    private static readonly string[] Frames =
    [
        "\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2836", "\u2837", "\u2807", "\u280f",
    ];

    /// <summary>
    /// 帧切换间隔（毫秒）。150ms 提供平滑且不分散注意力的节奏。
    /// </summary>
    private const int CadenceMs = 150;

    private readonly IApplication _app;
    private readonly Action _onFrameAdvanced;
    private int _frameIndex;
    private object? _timeoutToken;

    /// <param name="app">Terminal.Gui 应用实例，用于 timeout 调度。</param>
    /// <param name="onFrameAdvanced">每次帧推进后调用的回调。
    /// 通常是所属 View 的 <c>() => SetNeedsDraw()</c>。</param>
    public SpinnerController(IApplication app, Action onFrameAdvanced)
    {
        _app = app;
        _onFrameAdvanced = onFrameAdvanced;
    }

    /// <summary>动画 timeout 是否处于活跃状态。</summary>
    public bool IsRunning => _timeoutToken is not null;

    /// <summary>当前 Braille spinner 帧字符。</summary>
    public string CurrentFrame => Frames[_frameIndex % Frames.Length];

    /// <summary>
    /// 启动动画：将帧索引重置为 0，并按 <see cref="CadenceMs"/> 周期调度 tick。
    /// 若动画已在运行，则为空操作（不重置帧索引，避免视觉跳变）。
    /// </summary>
    public void Start()
    {
        if (_timeoutToken is not null) return;
        _frameIndex = 0;
        _timeoutToken = _app.AddTimeout(
            TimeSpan.FromMilliseconds(CadenceMs),
            OnTick);
    }

    /// <summary>
    /// 停止动画并移除已调度的 timeout。
    /// 若动画未运行，则为空操作。
    /// </summary>
    public void Stop()
    {
        if (_timeoutToken is null) return;
        _app.RemoveTimeout(_timeoutToken);
        _timeoutToken = null;
    }

    private bool OnTick()
    {
        if (_timeoutToken is null) return false;
        _frameIndex = (_frameIndex + 1) % Frames.Length;
        _onFrameAdvanced();
        return true;
    }
}
