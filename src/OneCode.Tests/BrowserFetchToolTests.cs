using OneCode.App.Tools;
using OneCode.Core.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace OneCode.Tests;

/// <summary>
/// BrowserFetchTool：以能力命名的一等浏览器工具。验证 SSRF 前置校验、
/// 按需连接、结构化失败语义（503）与共享浏览器会话串行化。
/// </summary>
public sealed class BrowserFetchToolTests
{
    private static IMcpClient CreateClient(bool navigateOk = true, string snapshot = "ARIA snapshot")
    {
        var client = Substitute.For<IMcpClient>();
        client.CallToolAsync(BrowserFetchTool.NavigateToolName, Arg.Any<Dictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new McpToolResult("navigated", IsError: !navigateOk));
        client.CallToolAsync(BrowserFetchTool.SnapshotToolName, Arg.Any<Dictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new McpToolResult(snapshot, IsError: false));
        return client;
    }

    private static IMcpConnectionManager CreateManager(IMcpClient? client, bool connectResult = true)
    {
        var mcp = Substitute.For<IMcpConnectionManager>();
        mcp.GetClient(BrowserFetchTool.ServerName).Returns(client);
        mcp.GetConnectedClients().Returns(
            client is null ? [] : [(BrowserFetchTool.ServerName, client)]);
        mcp.ConnectOneAsync(BrowserFetchTool.ServerName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(connectResult));
        return mcp;
    }

    [Fact]
    public async Task FetchAsync_EmptyUrl_ReturnsErrorWithoutMcpAccess()
    {
        var mcp = Substitute.For<IMcpConnectionManager>();
        var sut = new BrowserFetchTool(mcp, NullLogger<BrowserFetchTool>.Instance);

        var result = await sut.FetchAsync("  ", TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        mcp.DidNotReceive().GetClient(Arg.Any<string>());
    }

    [Fact]
    public async Task FetchAsync_PrivateHost_BlockedBySsrfBeforeAnyConnection()
    {
        var mcp = Substitute.For<IMcpConnectionManager>();
        var sut = new BrowserFetchTool(mcp, NullLogger<BrowserFetchTool>.Instance);

        var result = await sut.FetchAsync("http://127.0.0.1:8080/admin", TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Invalid URL");
        mcp.DidNotReceive().GetClient(Arg.Any<string>());
        await mcp.DidNotReceive().ConnectOneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_ConnectFails_ReturnsServiceUnavailable()
    {
        var mcp = CreateManager(client: null, connectResult: false);
        var sut = new BrowserFetchTool(mcp, NullLogger<BrowserFetchTool>.Instance);

        var result = await sut.FetchAsync("https://203.0.113.10/", TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not available");
        await mcp.Received(1).ConnectOneAsync(BrowserFetchTool.ServerName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_ConnectThrows_ReturnsServiceUnavailableWithoutPropagating()
    {
        var mcp = Substitute.For<IMcpConnectionManager>();
        mcp.GetClient(BrowserFetchTool.ServerName).Returns((IMcpClient?)null);
        mcp.GetConnectedClients().Returns([]);
        mcp.ConnectOneAsync(BrowserFetchTool.ServerName, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("npx not found"));
        var sut = new BrowserFetchTool(mcp, NullLogger<BrowserFetchTool>.Instance);

        var result = await sut.FetchAsync("https://203.0.113.10/", TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not available");
    }

    [Fact]
    public async Task FetchAsync_NavigateFails_ReturnsRenderError()
    {
        var mcp = CreateManager(CreateClient(navigateOk: false));
        var sut = new BrowserFetchTool(mcp, NullLogger<BrowserFetchTool>.Instance);

        var result = await sut.FetchAsync("https://203.0.113.10/", TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Failed to render");
    }

    [Fact]
    public async Task FetchAsync_ConnectedServer_RendersSnapshotWithoutReconnect()
    {
        var mcp = CreateManager(CreateClient(snapshot: "button 'Sign in'"));
        var sut = new BrowserFetchTool(mcp, NullLogger<BrowserFetchTool>.Instance);

        var result = await sut.FetchAsync("https://203.0.113.10/", TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("button 'Sign in'");
        // 已连接：不触发重复连接（ConnectOneAsync 会先丢弃旧连接再重连）。
        await mcp.DidNotReceive().ConnectOneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_ConcurrentCalls_WaitOnSharedSessionGate()
    {
        // 测试构造器注入 gate：预先持有，FetchAsync 必须阻塞等待，验证共享会话串行化。
        var gate = new SemaphoreSlim(1, 1);
        var mcp = CreateManager(CreateClient());
        var sut = new BrowserFetchTool(mcp, NullLogger<BrowserFetchTool>.Instance, gate);
        var ct = TestContext.Current.CancellationToken;

        await gate.WaitAsync(ct);
        var task = sut.FetchAsync("https://203.0.113.10/", ct);

        var completed = await Task.WhenAny(task, Task.Delay(200, ct));
        completed.Should().NotBe(task, "FetchAsync must wait for the shared browser session gate");

        gate.Release();
        var result = await task;
        result.IsError.Should().BeFalse();
    }
}