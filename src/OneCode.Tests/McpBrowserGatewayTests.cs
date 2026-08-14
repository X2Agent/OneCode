using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Tools;
using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

public sealed class McpBrowserGatewayTests
{
    [Fact]
    public async Task RenderAsync_NoConnectedServer_AttemptsOnDemandConnectAndReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var mcp = Substitute.For<IMcpConnectionManager>();
        mcp.GetClient(McpBrowserGateway.DefaultServerName).Returns((McpClient?)null);
        mcp.GetConnectedClients().Returns(Array.Empty<(string Name, McpClient Client)>());
        mcp.ConnectOneAsync(McpBrowserGateway.DefaultServerName, Arg.Any<CancellationToken>())
            .Returns(System.Threading.Tasks.Task.FromResult(false));

        var sut = new McpBrowserGateway(mcp, NullLogger<McpBrowserGateway>.Instance);

        var result = await sut.RenderAsync("https://example.com/", timeoutMs: 1000, ct);

        result.Should().BeNull();
        // 按需连接：首次使用才触发；失败静默跳过（WebFetch 保留 HTTP 结果）。
        await mcp.Received(1).ConnectOneAsync(McpBrowserGateway.DefaultServerName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenderAsync_OnDemandConnectThrows_ReturnsNullWithoutPropagating()
    {
        var ct = TestContext.Current.CancellationToken;
        var mcp = Substitute.For<IMcpConnectionManager>();
        mcp.GetClient(McpBrowserGateway.DefaultServerName).Returns((McpClient?)null);
        mcp.GetConnectedClients().Returns(Array.Empty<(string Name, McpClient Client)>());
        mcp.ConnectOneAsync(McpBrowserGateway.DefaultServerName, Arg.Any<CancellationToken>())
            .Returns<System.Threading.Tasks.Task<bool>>(_ => throw new InvalidOperationException("boom"));

        var sut = new McpBrowserGateway(mcp, NullLogger<McpBrowserGateway>.Instance);

        var result = await sut.RenderAsync("https://example.com/", timeoutMs: 1000, ct);

        result.Should().BeNull();
        await mcp.Received(1).ConnectOneAsync(McpBrowserGateway.DefaultServerName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenderAsync_EmptyUrl_ReturnsNullWithoutLookingUpClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var mcp = Substitute.For<IMcpConnectionManager>();
        var sut = new McpBrowserGateway(mcp, NullLogger<McpBrowserGateway>.Instance);

        var result = await sut.RenderAsync("  ", ct: ct);

        result.Should().BeNull();
        mcp.DidNotReceive().GetClient(Arg.Any<string>());
    }

    [Fact]
    public async Task RenderAsync_ConcurrentCalls_AreSerializedOnSharedSessionGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var mcp = Substitute.For<IMcpConnectionManager>();
        var gate = new SemaphoreSlim(1, 1);

        var concurrent = 0;
        var maxConcurrent = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        var sut = new McpBrowserGateway(
            mcp,
            NullLogger<McpBrowserGateway>.Instance,
            gate,
            async (url, _, token) =>
            {
                var n = Interlocked.Increment(ref callCount);
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);

                if (n == 1)
                {
                    started.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token).ConfigureAwait(false);
                }
                else
                {
                    enteredSecond.TrySetResult();
                }

                Interlocked.Decrement(ref concurrent);
                return $"rendered:{url}";
            });

        var first = sut.RenderAsync("https://a.example/", timeoutMs: 5_000, ct);
        await started.Task.WaitAsync(ct).ConfigureAwait(false);

        // Second call must block on the session gate until the first releases.
        var second = sut.RenderAsync("https://b.example/", timeoutMs: 5_000, ct);
        var racedIn = await Task.WhenAny(enteredSecond.Task, Task.Delay(80, ct)).ConfigureAwait(false);
        racedIn.Should().NotBe(enteredSecond.Task, "second render must wait for the shared session gate");

        releaseFirst.TrySetResult();
        var results = await Task.WhenAll(first, second).ConfigureAwait(false);

        results.Should().BeEquivalentTo(["rendered:https://a.example/", "rendered:https://b.example/"]);
        maxConcurrent.Should().Be(1);
        callCount.Should().Be(2);
        // Override path must not probe MCP connectivity.
        mcp.DidNotReceive().GetClient(Arg.Any<string>());
    }

    private static void InterlockedMax(ref int location, int candidate)
    {
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref location);
            if (candidate <= snapshot)
                return;
        }
        while (Interlocked.CompareExchange(ref location, candidate, snapshot) != snapshot);
    }
}
