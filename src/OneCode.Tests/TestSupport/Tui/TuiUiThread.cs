using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace OneCode.Tests.TestSupport.Tui;

/// <summary>
/// L2 宿主的 UI 线程。
///
/// 真实 TUI 里所有视图只活在一个线程上：<c>IApplication.Invoke</c> 的语义就是把变更
/// 投递回该线程（Terminal.Gui v2 的主循环队列）。测试进程不能拿 xUnit 的测试线程充当
/// 这条线程 —— xUnit v3 会让 <c>await</c> 续体落到不同的线程池线程上，而 Terminal.Gui 的
/// 编辑控件（<c>TextDocument</c>）校验的是「只能被创建它的那个线程访问」，
/// 于是续体一旦换线程，界面变更就会抛 <c>Call from invalid thread</c>，
/// 或与查询流水线线程并发改动同一份行集合（实测：审批收尾的
/// <c>EndTailRegion</c> 会因此抛 <c>RemoveRange</c> 越界）。
///
/// 因此这里自建一条常驻线程承载全部界面工作：视图在它上面创建、按键在它上面派发、
/// <c>Invoke</c> 投递的动作也在它上面执行。单线程串行化之后行集合不再被交叉改写，
/// 界面状态变成确定性的，断言也就不再靠「碰巧落在同一条线程上」。
/// </summary>
internal sealed class TuiUiThread : IDisposable
{
    private readonly Channel<Action> _queue =
        Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Thread _thread;
    private readonly List<Exception> _failures = [];
    private int _posted;

    public TuiUiThread()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "onecode-tui-test-ui",
        };
        _thread.Start();
    }

    /// <summary>已投递的界面动作总数 —— 供「界面是否已收敛」判断使用。</summary>
    public int PostedCount => Volatile.Read(ref _posted);

    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>在 UI 线程上同步执行并返回结果；工作抛出时原样回抛给调用方。</summary>
    public T Send<T>(Func<T> work)
    {
        if (IsCurrentThread)
            return work();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Send(Action work) => Send(() => { work(); return true; });

    /// <summary>
    /// 投递界面变更且不阻塞调用方 —— 与生产 <c>IApplication.Invoke</c> 一致。
    /// 已在 UI 线程时直接执行，保持「同一线程上的嵌套 Invoke 立即生效」的语义。
    /// </summary>
    public void Post(Action work)
    {
        if (IsCurrentThread)
        {
            RunGuarded(work);
            return;
        }

        Interlocked.Increment(ref _posted);
        _queue.Writer.TryWrite(() => RunGuarded(work));
    }

    /// <summary>
    /// 顺序屏障：等到 UI 线程把此前投递的动作全部执行完，然后抛出界面线程上的异常。
    ///
    /// 抛出而不是吞掉是刻意的：界面动作跑在流水线的 fire-and-forget 路径上，
    /// 谁都不看它的结果，异常一旦被吞，「界面没更新」就会被断言成「功能不对」，
    /// 缺陷由此隐身（本次调试正是踩了这个坑）。
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (!IsCurrentThread)
        {
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Writer.TryWrite(() => barrier.TrySetResult());
            await barrier.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        ThrowFailures();
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Loop()
    {
        while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_queue.Reader.TryRead(out var action))
                RunGuarded(action);
        }
    }

    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            lock (_failures)
                _failures.Add(ex);
        }
    }

    private void ThrowFailures()
    {
        Exception? first;
        lock (_failures)
        {
            first = _failures.Count > 0 ? _failures[0] : null;
            _failures.Clear();
        }

        if (first is not null)
            ExceptionDispatchInfo.Capture(first).Throw();
    }
}
