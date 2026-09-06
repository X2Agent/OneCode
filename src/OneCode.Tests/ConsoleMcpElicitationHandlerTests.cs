using System.Diagnostics;
using OneCode.App.Services.Mcp;

namespace OneCode.Tests;

/// <summary>
/// <see cref="ConsoleMcpElicitationHandler.PromptAsync"/> 的取消语义锚点：取消 = 空响应（null）。
/// 此前 <c>Task.Run(Console.ReadLine, ct)</c> 在已取消 token 下产生 Canceled 任务，
/// 调用方（McpElicitationHandler）会把取消当成 elicitation 失败；修复后由轮询循环
/// 自身保证取消语义。
/// </summary>
public sealed class ConsoleMcpElicitationHandlerTests
{
    [Fact]
    public async Task PromptAsync_AlreadyCancelled_ReturnsNullInsteadOfThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await ConsoleMcpElicitationHandler.PromptAsync("ignored: ", cts.Token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PromptAsync_CancelWhileWaiting_ReturnsNullPromptly()
    {
        using var cts = new CancellationTokenSource();
        var promptTask = ConsoleMcpElicitationHandler.PromptAsync("waiting: ", cts.Token);
        cts.Cancel();

        // 有界等待：交互控制台下取消经 KeyAvailable 轮询及时生效；stdin 重定向环境
        // 回退阻塞 ReadLine（已知局限），超时即失败以明确暴露环境限制而非挂死套件。
        var stopwatch = Stopwatch.StartNew();
        var result = await promptTask.WaitAsync(TimeSpan.FromSeconds(5));

        stopwatch.Stop();
        result.Should().BeNull();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4),
            "cancellation must take effect promptly via the polling loop");
    }
}
