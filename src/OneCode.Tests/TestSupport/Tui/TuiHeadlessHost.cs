using System.Text;

using OneCode.App.Tui;

using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Text;

namespace OneCode.Tests.TestSupport.Tui;

/// <summary>
/// L3 驱动级无头宿主：在专用线程上跑<b>真实</b> <see cref="IApplication"/> 主循环与真实
/// <c>ansi</c> 驱动，再从驱动级屏幕缓冲上读回渲染结果。
///
/// 与 L2（<see cref="TuiTestShell"/>）的分工：
/// L2 用 <see cref="IApplication"/> 替身断言界面状态，覆盖分发逻辑；
/// L3 覆盖替身结构上不可能覆盖的部分 —— 按键编解码经真实驱动、真实布局与绘制、
/// 驱动级屏幕缓冲、以及 resize / 鼠标事件的路由。启动与收尾仍走生产同一份
/// <c>TuiHost.RunLoop</c>，不是测试自己重写的等价流程。
///
/// 真实 IO 由 <see cref="TuiTestEnvironment"/> 的模块初始化器短路（不写终端、不装信号处理），
/// 因此可以把主循环搬进测试进程。
///
/// 线程模型（探针实证，非推测）：<c>Create</c> / <c>Init</c> / <c>Run</c> 必须<b>同线程</b>，
/// 跨线程 Init 会得到「循环确实在跑、但屏幕空白且注入无效」的假象；测试线程的一切驱动操作
/// 都必须经 <c>IApplication.Invoke</c> 投回循环线程 —— <c>IApplication</c> 对同线程
/// 调用同步执行、对异线程调用排入下一轮循环，这也正是生产代码改界面的唯一途径。
/// </summary>
internal sealed class TuiHeadlessHost : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly TuiTestContextBuilder _builder;
    private readonly Thread _runThread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _ct;

    private IApplication? _app;
    private OneCodeToplevel? _toplevel;
    private Exception? _failure;
    private bool _disposed;

    private TuiHeadlessHost(Action<TuiTestContextBuilder>? configure, int cols, int rows)
    {
        _ct = TestContext.Current.CancellationToken;
        _builder = new TuiTestContextBuilder();
        configure?.Invoke(_builder);
        Cols = cols;
        Rows = rows;

        // 后台线程：循环意外卡死时不得拖住测试进程退出（失败在断言处显形，而不是挂住）。
        _runThread = new Thread(RunCore) { IsBackground = true, Name = "TuiHeadlessHost.loop" };
        _runThread.Start();
    }

    /// <summary>驱动屏幕宽度（列）。</summary>
    public int Cols { get; private set; }

    /// <summary>驱动屏幕高度（行）。</summary>
    public int Rows { get; private set; }

    /// <summary>真实 <see cref="IApplication"/>（仅在循环线程上操作，断言用途请用 <see cref="OnLoopAsync{T}"/>）。</summary>
    public IApplication App => _app ?? throw new InvalidOperationException("主循环尚未初始化。");

    /// <summary>会话顶层视图。<c>TuiHost.RunLoop</c> 负责其释放，宿主不得再动它。</summary>
    public OneCodeToplevel Toplevel =>
        _toplevel ?? throw new InvalidOperationException("会话顶层视图尚未创建。");

    /// <summary>无头主循环是否已退出（<c>RequestStop</c> 之后、<see cref="TuiHost.RunLoop"/> 返回之前为 false）。</summary>
    public bool HasExited => _exit.Task.IsCompleted;

    /// <summary>启动无头主循环并等待其就绪（会话顶层视图已创建）。</summary>
    public static TuiHeadlessHost Start(Action<TuiTestContextBuilder>? configure = null, int cols = 100, int rows = 30)
    {
        var host = new TuiHeadlessHost(configure, cols, rows);
        host.WaitReady();
        return host;
    }

    /// <summary>退出码（仅在 <see cref="HasExited"/> 之后有效）。</summary>
    public int ExitCode => _exit.Task.IsCompleted ? _exit.Task.Result : throw new InvalidOperationException("主循环尚未退出。");

    /// <summary>等待主循环退出并返回退出码；超时抛 <see cref="TimeoutException"/> 而不是静默通过。</summary>
    public Task<int> WaitForExitAsync() => _exit.Task.WaitAsync(DefaultTimeout, _ct);

    // ——— 输入注入：全部经 Invoke 投回循环线程 ———

    /// <summary>注入一次按键（真实驱动解码路径）。</summary>
    public Task InjectKeyAsync(Key key) => OnLoopAsync(() =>
    {
        App.InjectKey(key);
        return true;
    });

    /// <summary>逐个字符注入一段文本（每个字符一次按键事件，与真实键入同路径）。</summary>
    public Task TypeAsync(string text) => OnLoopAsync(() =>
    {
        foreach (var ch in text)
            App.InjectKey(new Key(ch));

        return true;
    });

    /// <summary>在屏幕坐标处注入一次左键单击（真实鼠标事件的驱动级入口）。</summary>
    public Task ClickAsync(int col, int row) => OnLoopAsync(() =>
    {
        App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new System.Drawing.Point(col, row)));
        return true;
    });

    /// <summary>在屏幕坐标处注入一次滚轮事件。</summary>
    /// <remarks>
    /// 位置必须写 <see cref="Mouse.ScreenPosition"/>：ANSI 驱动把注入的鼠标事件重新编码成
    /// SGR 转义序列再走一遍输入管道，编码只看 ScreenPosition，只写 Position 会被编码成 (0,0)。
    /// </remarks>
    public Task WheelAsync(int col, int row, bool up) => OnLoopAsync(() =>
    {
        var mouse = new Mouse { ScreenPosition = new System.Drawing.Point(col, row), Flags = up ? MouseFlags.WheeledUp : MouseFlags.WheeledDown };
        App.InjectMouse(mouse);
        return true;
    });

    // ——— 屏幕与尺寸 ———

    /// <summary>
    /// 驱动级屏幕快照：每行一个字符串，行数与列数取自驱动的屏幕尺寸，因此
    /// 「全屏铺满 / 跟着 resize 变化」都是可断言的；行内保留尾部填充，列宽断言才有意义。
    ///
    /// 全角字符在缓冲区里占两格、第二格是续格填充，逐格拼接会拼出「流 式 中 文」这种
    /// 带缝的文本；这里按 <c>GetColumns</c> 跳过续格，还原成用户看到的一行。
    /// </summary>
    public Task<string[]> ScreenLinesAsync() => OnLoopAsync(() =>
    {
        var driver = App.Driver!;
        var screen = driver.Screen;
        var contents = driver.Contents!;
        var lines = new string[screen.Height];

        for (var row = 0; row < screen.Height; row++)
        {
            var builder = new StringBuilder(screen.Width);
            for (var col = 0; col < screen.Width; col++)
            {
                var grapheme = contents[row, col].Grapheme;
                builder.Append(grapheme);

                if (grapheme.Length > 0 && grapheme.GetColumns(ignoreLessThanZero: true) == 2)
                    col++;
            }

            lines[row] = builder.ToString();
        }

        return lines;
    });

    /// <summary>屏幕快照（每行去尾空，便于阅读与 <c>Contains</c> 断言）。</summary>
    public async Task<string> ScreenTextAsync()
    {
        var lines = await ScreenLinesAsync().ConfigureAwait(false);
        return string.Join('\n', lines.Select(line => line.TrimEnd()));
    }

    /// <summary>调整驱动屏幕尺寸（等价于终端窗口被拉伸）。</summary>
    public Task ResizeAsync(int cols, int rows) => OnLoopAsync(() =>
    {
        App.Driver!.SetScreenSize(cols, rows);
        Cols = cols;
        Rows = rows;
        return true;
    });

    // ——— 编排 ———

    /// <summary>在循环线程上执行任意操作（读界面状态、驱动 View API）。</summary>
    public Task<T> OnLoopAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        App.Invoke(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task.WaitAsync(DefaultTimeout, _ct);
    }

    /// <summary>在循环线程上执行一次界面操作（无返回值）。</summary>
    public Task OnLoopAsync(Action work) => OnLoopAsync(() =>
    {
        work();
        return true;
    });

    /// <summary>请求退出（等价于 Ctrl+D / <c>/quit</c> 走到的 <c>IApplication.RequestStop</c>）。</summary>
    public Task RequestStopAsync() => OnLoopAsync(() => App.RequestStop(Toplevel));

    /// <summary>
    /// 提交一条输入并等待整轮查询流水线收敛 —— 与 L2 同款三段屏障：
    /// 进入（脚本流确已开始）→ 脚本耗尽 → 屏幕稳定且流式预览已收尾。
    /// </summary>
    public async Task SubmitQueryAsync(string text)
    {
        var sessionGate = new Latch();
        var exhaustedGate = new Latch();
        _builder
            .OnSessionCreated(sessionGate.Release)
            .OnStreamExhausted(exhaustedGate.Release);

        await TypeAsync(text).ConfigureAwait(false);
        await InjectKeyAsync(Key.Enter).ConfigureAwait(false);

        await sessionGate.WaitAsync(DefaultTimeout, _ct).ConfigureAwait(false);
        await exhaustedGate.WaitAsync(DefaultTimeout, _ct).ConfigureAwait(false);
        await SettleAsync().ConfigureAwait(false);
        await WaitUntilAsync(() => !IsStreaming(), "流式预览收尾").ConfigureAwait(false);
    }

    /// <summary>等待屏幕内容满足条件（条件在循环线程外对快照求值）。</summary>
    public async Task<string> WaitForScreenAsync(Func<string, bool> predicate, string description)
    {
        var deadline = Environment.TickCount64 + (long)DefaultTimeout.TotalMilliseconds;
        while (true)
        {
            var text = await ScreenTextAsync().ConfigureAwait(false);
            if (predicate(text))
                return text;

            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"等待「{description}」超时（{DefaultTimeout.TotalSeconds:0}s）。最后屏幕：\n{text}");

            await Task.Delay(5, _ct).ConfigureAwait(false);
        }
    }

    /// <summary>等待循环线程上的条件成立；超时抛 <see cref="TimeoutException"/>。</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = Environment.TickCount64 + (long)DefaultTimeout.TotalMilliseconds;
        while (true)
        {
            if (await OnLoopAsync(condition).ConfigureAwait(false))
                return;

            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"等待「{description}」超时（{DefaultTimeout.TotalSeconds:0}s）。");

            await Task.Delay(5, _ct).ConfigureAwait(false);
        }
    }

    /// <summary>等待屏幕连续三轮稳定 —— 主循环没有 L2 那样的投递计数，用像素级收敛代替。</summary>
    public async Task SettleAsync()
    {
        var deadline = Environment.TickCount64 + (long)DefaultTimeout.TotalMilliseconds;
        var stableRounds = 0;
        string? last = null;
        while (stableRounds < 3)
        {
            var text = await ScreenTextAsync().ConfigureAwait(false);
            stableRounds = text == last ? stableRounds + 1 : 0;
            last = text;

            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"等待屏幕收敛超时（{DefaultTimeout.TotalSeconds:0}s）：画面持续变化。");

            await Task.Delay(5, _ct).ConfigureAwait(false);
        }
    }

    /// <summary>字符串在终端中占用的显示列数（东亚全角按 2 列计）。</summary>
    public static int DisplayColumns(string line) => line.GetColumns(ignoreLessThanZero: true);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 循环仍在跑就先请它停：在跑动中 Dispose 驱动会把退出流程变成异常。
        if (!HasExited)
        {
            try
            {
                RequestStopAsync().GetAwaiter().GetResult();
                _exit.Task.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // 收尾尽力而为：失败会在断言处显形，此处吞掉以免掩盖真正的失败原因。
            }
        }

        _app?.Dispose();
        _runThread.Join(TimeSpan.FromSeconds(5));
    }

    private void RunCore()
    {
        try
        {
            var app = Application.Create();
            app.ForceDriver = DriverRegistry.Names.ANSI;
            app.Init();
            app.Driver!.SetScreenSize(Cols, Rows);
            _app = app;

            var exitCode = TuiHost.RunLoop(app, loopApp =>
            {
                var toplevel = new OneCodeToplevel(_builder.Build(), loopApp);
                _toplevel = toplevel;
                _ready.TrySetResult();
                return toplevel;
            });

            _exit.TrySetResult(exitCode);
        }
        catch (Exception ex)
        {
            _failure = ex;
            _ready.TrySetResult();
            _exit.TrySetResult(-1);
        }
    }

    private void WaitReady()
    {
        if (!_ready.Task.Wait(DefaultTimeout, _ct))
            throw new TimeoutException($"无头主循环在 {DefaultTimeout.TotalSeconds:0}s 内未就绪。");

        if (_failure is { } ex)
            throw new InvalidOperationException("无头主循环启动失败。", ex);
    }

    private bool IsStreaming() =>
        Toplevel.SubViews.OfType<ReplShell>().Single().Transcript.MessageView.IsStreaming;

    /// <summary>一次性完成信号（与 L2 宿主同一实现：脚本流观测点的收敛闸门）。</summary>
    private sealed class Latch
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _tcs.TrySetResult();

        public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _tcs.Task.WaitAsync(timeout, ct);
    }
}
