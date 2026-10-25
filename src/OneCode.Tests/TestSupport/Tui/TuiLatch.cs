namespace OneCode.Tests.TestSupport.Tui;

/// <summary>
/// 一次性完成信号（脚本流观测点的收敛闸门）：<see cref="Arm"/> 之后 <see cref="Release"/> 才生效；
/// 未武装时 <see cref="WaitAsync"/> 直接完成（该闸门不参与当前场景）。
/// </summary>
internal sealed class TuiLatch
{
    private volatile TaskCompletionSource? _tcs;

    public void Arm() =>
        _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _tcs?.TrySetResult();

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _tcs is { } tcs ? tcs.Task.WaitAsync(timeout, ct) : Task.CompletedTask;
}
