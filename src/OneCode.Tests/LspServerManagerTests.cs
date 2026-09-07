using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Lsp;
using OneCode.Core.Lsp;

namespace OneCode.Tests;

// LspServerManager — 启动失败原因可见（P3：软失败落池，对齐 MCP LastError 语义）
// 与手动重启入口（P1/P10：/lsp restart，stop + start）。

public sealed class LspServerManagerTests
{
    private static LspServerManager CreateManager() => new(
        NullLogger<LspServerManager>.Instance,
        new LspDiagnosticRegistry(),
        NullLoggerFactory.Instance);

    private static LspServerConfig GhostConfig() => new("ghost-lsp", "definitely-not-a-real-lsp-binary-xyz", []);

    [Fact]
    public async Task StartServerAsync_MissingBinary_FailsAndSurfacesLastErrorInStatus()
    {
        await using var manager = CreateManager();

        var started = await manager.StartServerAsync(GhostConfig());

        started.Should().BeFalse();
        var status = manager.GetStatus().Should().ContainSingle(s => s.Name == "ghost-lsp").Subject;
        status.IsRunning.Should().BeFalse();
        status.LastError.Should().NotBeNullOrEmpty("启动失败原因必须对 /lsp status 可见（软失败落池）");
    }

    [Fact]
    public async Task RestartServerAsync_ServerNotRunning_ReturnsFalseWithoutStarting()
    {
        await using var manager = CreateManager();

        var restarted = await manager.RestartServerAsync("ghost-lsp");

        restarted.Should().BeFalse("restart 是 stop + start，未运行的服务器不属于重启范畴（应走 /lsp enable）");
        manager.GetStatus().Should().NotContain(s => s.Name == "ghost-lsp" && s.IsRunning);
    }

    [Fact]
    public async Task StartServerAsync_MissingBinary_RestartAlsoFailsWithoutResurrectingInstance()
    {
        await using var manager = CreateManager();
        await manager.StartServerAsync(GhostConfig());

        var restarted = await manager.RestartServerAsync("ghost-lsp");

        restarted.Should().BeFalse("启动失败的实例已出池，重启无从恢复");
        manager.GetStatus().Should().Contain(s => s.Name == "ghost-lsp" && s.LastError != null);
    }

    [Fact]
    public async Task SendWithTimeoutAsync_HungSend_TimesOutAndMarksUnhealthy()
    {
        // stdin 管道假死（服务器活着但不读 stdin）：发送任务永不完成 → 必须在短超时内
        // 抛 TimeoutException 并回调 onTimeout 标记 unhealthy，交由崩溃自愈循环重启。
        var hung = new TaskCompletionSource();
        Exception? marked = null;
        var sw = Stopwatch.StartNew();

        var act = () => LspServerManager.SendWithTimeoutAsync(
            hung.Task, TimeSpan.FromMilliseconds(50), ex => marked = ex, null);

        await act.Should().ThrowAsync<TimeoutException>();
        marked.Should().NotBeNull("超时必须标记 unhealthy，交由崩溃自愈循环接管");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "挂起的发送不得拖住调用方");
    }

    [Fact]
    public async Task SendWithTimeoutAsync_CompletedSend_PropagatesUnderlyingException()
    {
        // 正常完成路径必须观察/传播底层异常（管道关闭等），不得被超时包装吞掉。
        var failing = Task.FromException(new InvalidOperationException("pipe closed"));

        var act = () => LspServerManager.SendWithTimeoutAsync(
            failing, TimeSpan.FromSeconds(5), _ => { }, null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("pipe closed");
    }
}
