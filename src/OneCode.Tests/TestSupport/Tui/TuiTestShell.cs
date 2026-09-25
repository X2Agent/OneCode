using NSubstitute;

using OneCode.App.Tui;

using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OneCode.Tests.TestSupport.Tui;

/// <summary>
/// L2 交互测试宿主：用 <see cref="IApplication"/> 替身驱动真实的 <see cref="OneCodeToplevel"/>，
/// 从按键入口一路断言到界面状态。
///
/// 线程模型见 <see cref="TuiUiThread"/>：全部界面工作（创建视图、派发按键、执行
/// <c>Invoke</c> 动作）都跑在宿主自建的 UI 线程上，测试线程只负责编排与断言。
/// 因此本类型的每个界面访问都经 UI 线程转发，替身下「界面状态在断言时刻是稳定的」。
///
/// 唯一的异步来源是查询流水线本身（<c>_ = HandleSubmitAsync(...)</c> 是 fire-and-forget，
/// 生产代码没有对外暴露「一轮结束」事件）。宿主用三个收敛点覆盖它：
/// 先等 <c>CreateSession</c>（流水线确已进入，消除「错过开始」竞态），
/// 再等脚本耗尽（<c>finally</c> 已排空，取消路径同样释放），
/// 最后等界面收敛（连续若干轮无新投递，界面补画完毕）。
/// </summary>
internal sealed class TuiTestShell : IDisposable
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    private readonly TuiUiThread _ui;
    private readonly IApplication _app;
    private readonly CancellationToken _ct;
    private readonly Latch _sessionGate;
    private readonly Latch _commandGate;
    private readonly Latch _streamExhaustedGate;

    private TuiTestShell(
        TuiUiThread ui,
        IApplication app,
        OneCodeToplevel toplevel,
        TuiTestContextBuilder builder,
        Latch sessionGate,
        Latch commandGate,
        Latch streamExhaustedGate)
    {
        _ui = ui;
        _app = app;
        _ct = TestContext.Current.CancellationToken;
        Toplevel = toplevel;
        Builder = builder;
        _sessionGate = sessionGate;
        _commandGate = commandGate;
        _streamExhaustedGate = streamExhaustedGate;
    }

    public OneCodeToplevel Toplevel { get; }

    public TuiTestContextBuilder Builder { get; }

    /// <summary>构成 TUI 的 <see cref="IApplication"/> 替身（用于断言 <c>RequestStop</c> 等宿主交互）。</summary>
    public IApplication App => _app;

    /// <summary>真实 <see cref="ReplShell"/>。<c>OneCodeToplevel</c> 未暴露 Shell 访问器，从其子视图中取。</summary>
    public ReplShell Shell => Toplevel.SubViews.OfType<ReplShell>().Single();

    public ChatInputView Input => Shell.ChatInput;

    public ChatTranscriptView Transcript => Shell.Transcript;

    public OverlayHost Overlays => Shell.Overlays;

    /// <summary>已提交到会话区的行（在 UI 线程上取快照，不含尚未收尾的流式预览）。</summary>
    public IReadOnlyList<string> RenderedLines =>
        _ui.Send(() => Transcript.MessageView.RenderedLines.ToList());

    public string TranscriptText => string.Join('\n', RenderedLines);

    public static TuiTestShell Create(Action<TuiTestContextBuilder>? configure = null)
    {
        var sessionGate = new Latch();
        var commandGate = new Latch();
        var streamExhaustedGate = new Latch();

        var builder = new TuiTestContextBuilder();
        builder
            .OnSessionCreated(sessionGate.Release)
            .OnCommandExecuted(commandGate.Release)
            .OnStreamExhausted(streamExhaustedGate.Release);
        configure?.Invoke(builder);

        var ui = new TuiUiThread();
        var app = CreateApplication(ui);
        // 视图必须诞生在 UI 线程上：Terminal.Gui 的文本控件只允许创建它的线程访问。
        var toplevel = ui.Send(() =>
        {
            var instance = new OneCodeToplevel(builder.Build(), app);
            // 真实运行循环（IApplication.Run）会把自身回填到 Runnable.App；
            // 缺了这一步，Runnable.RequestStop() 无处可去（生产代码里 /quit、Ctrl+D 都走它）。
            instance.App = app;
            return instance;
        });
        return new TuiTestShell(ui, app, toplevel, builder, sessionGate, commandGate, streamExhaustedGate);
    }

    /// <summary>
    /// 提交一条输入（等价于在输入框里打字后回车）。
    ///
    /// 对「立即执行」命令（<c>/diff</c>、<c>/keybindings list</c>）而言，调用返回即已完成：
    /// 该通道不经过查询流，<c>Invoke</c> 在 UI 线程上同步执行，可直接断言。
    /// </summary>
    public void Submit(string text) => _ui.Send(() =>
    {
        Input.SetInputText(text);
        Input.DispatchInputKey(Key.Enter);
    });

    /// <summary>提交查询输入，等到查询流水线确已进入（<c>CreateSession</c> 已完成），但不等到结束。</summary>
    public async Task SubmitQueryBeginAsync(string text)
    {
        _sessionGate.Arm();
        _streamExhaustedGate.Arm();
        Submit(text);

        await _sessionGate.WaitAsync(SettleTimeout, _ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 等待当前这一轮查询结束 —— 三个屏障缺一不可：
    /// 1) 流式脚本已耗尽（取消路径经 <c>finally</c> 同样释放），事件已全部投递给界面；
    /// 2) 界面收敛（连续若干轮无新投递），流水线 <c>finally</c> 的收尾动作确已执行；
    /// 3) 流式预览已收尾（<c>IsStreaming</c> 回到 false），正式行才落进
    ///    <c>RenderedLines</c>（见 <c>ChatTranscriptView.Streaming.EndStreaming</c>：
    ///    预览文本只在此刻提交为正式行）。
    ///
    /// 屏障 1 保证「开始过」，屏障 2/3 保证「已结束」，因此不会把「尚未开始」误判成「已结束」。
    /// </summary>
    public async Task AwaitQueryEndAsync()
    {
        await _streamExhaustedGate.WaitAsync(SettleTimeout, _ct).ConfigureAwait(false);
        await SettleAsync().ConfigureAwait(false);
        await WaitForStreamingAsync(expected: false).ConfigureAwait(false);
    }

    /// <summary>提交查询输入并等待整轮流水线结束。</summary>
    public async Task SubmitQueryAsync(string text)
    {
        await SubmitQueryBeginAsync(text).ConfigureAwait(false);
        await AwaitQueryEndAsync().ConfigureAwait(false);
    }

    /// <summary>提交走命令执行通道的输入，并等待结果文本落屏。</summary>
    public async Task SubmitCommandAsync(string text)
    {
        _commandGate.Arm();
        _streamExhaustedGate.Arm();
        Submit(text);

        await _commandGate.WaitAsync(SettleTimeout, _ct).ConfigureAwait(false);
        await SettleAsync().ConfigureAwait(false);
        await WaitForStreamingAsync(expected: false).ConfigureAwait(false);
    }

    /// <summary>按一次键，经 Toplevel 的完整分发链（Shell → 输入框 / 弹层）。</summary>
    public void PressKey(Key key) => _ui.Send(() => Toplevel.NewKeyDownEvent(key));

    /// <summary>在输入框上按一次键（走 ChatInputView 的分发链）。</summary>
    public void TypeKey(Key key) => _ui.Send(() => Input.DispatchInputKey(key));

    /// <summary>在 Shell 层派发一次按键，返回是否被消费 —— 用于验证弹层拦截链。</summary>
    public bool DispatchKeyDown(Key key) => _ui.Send(() => Shell.DispatchKeyDown(key));

    /// <summary>在 UI 线程上执行任意界面操作（诊断或宿主未覆盖的交互入口）。</summary>
    public T OnUiThread<T>(Func<T> work) => _ui.Send(work);

    /// <summary>等待流式状态到达期望值；超出 <see cref="SettleTimeout"/> 视为流水线卡死。</summary>
    public async Task WaitForStreamingAsync(bool expected)
    {
        var deadline = Environment.TickCount64 + (long)SettleTimeout.TotalMilliseconds;
        while (true)
        {
            await _ui.SyncAsync(_ct).ConfigureAwait(false);

            if (_ui.Send(() => Transcript.MessageView.IsStreaming) == expected)
                return;

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"等待流式状态变为 {expected} 超时（{SettleTimeout.TotalSeconds:0}s）：" +
                    "查询流水线未按预期收敛。");
            }

            await Task.Delay(1, _ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 等待界面状态满足条件 —— 供 fire-and-forget 的异步 opener 使用
    /// （<c>/diff</c> 的审查 overlay 由 <c>_ = ShowReviewOverlayAsync()</c> 推送，
    /// 提交返回时不一定已落栈）。
    ///
    /// 条件在 UI 线程上求值：断言读到的必须是「界面已处理完此前全部投递」之后的状态。
    /// 超时抛 <see cref="TimeoutException"/> 而非静默通过：条件未成立时测试必须红，
    /// 否则「弹层没出来」会被断言成「弹层内容不对」。
    /// </summary>
    public async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = Environment.TickCount64 + (long)SettleTimeout.TotalMilliseconds;
        while (true)
        {
            await _ui.SyncAsync(_ct).ConfigureAwait(false);

            if (_ui.Send(condition))
                return;

            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"等待「{description}」超时（{SettleTimeout.TotalSeconds:0}s）。");

            await Task.Delay(1, _ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 等待界面收敛：连续三轮既无新的界面投递、队列也已排空。
    ///
    /// 查询流水线是「读跑在前面、界面按投递顺序追赶」的（生产亦然），因此
    /// 「流已耗尽」不等于「界面已画完」—— 收尾动作（提交预览、解除忙碌、回焦输入框）
    /// 都排在最后一个事件之后。界面线程若抛出异常，在这里显形而不是被静默吞掉。
    /// </summary>
    public async Task SettleAsync()
    {
        var deadline = Environment.TickCount64 + (long)SettleTimeout.TotalMilliseconds;
        var stableRounds = 0;
        var lastPosted = -1;

        while (stableRounds < 3)
        {
            await _ui.SyncAsync(_ct).ConfigureAwait(false);

            var posted = _ui.PostedCount;
            stableRounds = posted == lastPosted ? stableRounds + 1 : 0;
            lastPosted = posted;

            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"等待界面收敛超时（{SettleTimeout.TotalSeconds:0}s）：仍有持续的界面投递。");

            await Task.Delay(1, _ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 读取 overlay 内 <see cref="ListView"/> 的真实数据源行 —— 弹层列表的内容契约
    /// （文件条目 / 生效绑定 / 配置警告）由它承载，比断言构造参数更贴近用户所见。
    /// </summary>
    public static IReadOnlyList<string> ListRowsOf(View overlay) =>
        overlay.SubViews.OfType<ListView>()
            .Single()
            .Source!
            .ToList()
            .Cast<object>()
            .Select(row => row?.ToString() ?? string.Empty)
            .ToList();

    public void Dispose()
    {
        _ui.Send(() => Toplevel.Dispose());
        _ui.Dispose();
    }

    private static IApplication CreateApplication(TuiUiThread ui)
    {
        var app = Substitute.For<IApplication>();
        // 生产 Invoke 把变更投递回 UI 线程；替身照抄这一点，界面才不会被别的线程改写
        // （详见 TuiUiThread 的说明）。
        app.When(a => a.Invoke(Arg.Any<Action>())).Do(c => ui.Post(c.Arg<Action>()));
        app.When(a => a.Invoke(Arg.Any<Action<IApplication>>()))
            .Do(c => ui.Post(() => c.Arg<Action<IApplication>>()(app)));
        // 防抖 / 延迟重绘在替身下没有时钟，直接视为已执行（KeyRoutingTests 同款做法）。
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(new object());
        return app;
    }

    /// <summary>一次性完成信号：<see cref="Arm"/> 之后 <see cref="Release"/> 才生效。</summary>
    private sealed class Latch
    {
        private volatile TaskCompletionSource? _tcs;

        public void Arm() =>
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _tcs?.TrySetResult();

        public Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
            _tcs is { } tcs ? tcs.Task.WaitAsync(timeout, ct) : Task.CompletedTask;
    }
}
