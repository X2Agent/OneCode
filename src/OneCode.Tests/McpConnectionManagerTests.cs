using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Mcp;
using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="McpStartupPreconnector"/> — 启动预连接的单次执行、
/// 首条消息有界等待与回调异常隔离（Plan B 防回归）。
/// </summary>
public sealed class McpStartupPreconnectorTests
{
    private static McpStartupPreconnector CreateSut(IMcpConnectionManager manager) =>
        new(manager, NullLogger<McpStartupPreconnector>.Instance);

    // 并发首调（bootstrap fire-and-forget + cron EnsureConnected 撞车）只触发一次全量连接

    [Fact]
    public async Task EnsureConnectedAsync_ConcurrentFirstCalls_ConnectsExactlyOnce()
    {
        var manager = Substitute.For<IMcpConnectionManager>();
        manager.ConnectAllAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var sut = CreateSut(manager);
        var ct = TestContext.Current.CancellationToken;

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => sut.EnsureConnectedAsync(ct), ct))
            .ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15), ct);

        await manager.Received(1).ConnectAllAsync(Arg.Any<CancellationToken>());
    }

    // 预连接尚未启动时（无 MCP 配置、未走到 bootstrap），首条消息等待必须立即返回

    [Fact]
    public async Task WaitForFirstMessageAsync_PreconnectNotStarted_ReturnsImmediately()
    {
        var manager = Substitute.For<IMcpConnectionManager>();
        var sut = CreateSut(manager);
        var ct = TestContext.Current.CancellationToken;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitForFirstMessageAsync(ct);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "no preconnect was started — waiting must not block the first message");
        await manager.DidNotReceive().ConnectAllAsync(Arg.Any<CancellationToken>());
    }

    // 连接被慢速服务器拖住时，首条消息等待必须有界放行（不无限阻塞对话）

    [Fact]
    public async Task WaitForFirstMessageAsync_ConnectAllStillRunning_BoundedByExternalCancellationToken()
    {
        var manager = Substitute.For<IMcpConnectionManager>();
        manager.ConnectAllAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.Delay(Timeout.Infinite, callInfo.ArgAt<CancellationToken>(0)));
        var sut = CreateSut(manager);

        using var waitBudget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitForFirstMessageAsync(waitBudget.Token);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "first-message wait must be bounded even while ConnectAll is still in flight");
    }

    // 预连接完成回调（技能提供者重建）抛异常不得导致任务故障或进程崩溃

    [Fact]
    public async Task StartBackground_OnCompletedThrows_DoesNotFaultPreconnectTask()
    {
        var manager = Substitute.For<IMcpConnectionManager>();
        manager.ConnectAllAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var sut = CreateSut(manager);
        var ct = TestContext.Current.CancellationToken;

        sut.StartBackground(onCompleted: () => throw new InvalidOperationException("boom"), ct: ct);
        var act = () => sut.EnsureConnectedAsync(ct);

        await act.Should().NotThrowAsync();
        // 预连接任务必须实际跑完（ConnectAll 恰好一次），回调异常被吞不等于任务未完成
        await manager.Received(1).ConnectAllAsync(ct);
    }
}

/// <summary>
/// Unit tests for <see cref="McpConnectionManager"/> connection lifecycle semantics —
/// 纯状态/门闩/软失败路径，通过 HTTP 端口占用模拟"连接失败"，不拉起任何真实 MCP 进程。
/// </summary>
public sealed class McpConnectionManagerTests : IAsyncDisposable
{
    private readonly McpConnectionManager _manager;

    public McpConnectionManagerTests()
    {
        _manager = new McpConnectionManager(
            new McpMultiScopeConfigLoader(NullLogger<McpMultiScopeConfigLoader>.Instance),
            new McpElicitationHandler(NullLogger<McpElicitationHandler>.Instance));
    }

    public async ValueTask DisposeAsync() => await _manager.DisposeAsync();

    private static McpServerDefinition HttpDefinition(int port) =>
        new(McpTransportType.Http, Url: $"http://127.0.0.1:{port}/mcp");

    // GetServerNames：软失败（连接异常不抛出）也保留连接池条目

    [Fact]
    public async Task ConnectOneAsync_HttpConnectionRefused_SoftFailsAndKeepsEntry()
    {
        var refusedPort = GetFreeTcpPort();
        var def = HttpDefinition(refusedPort);

        var act = () => _manager.ConnectOneAsync("refused", def, TestContext.Current.CancellationToken);
        await act.Should().NotThrowAsync();

        _manager.GetServerNames().Should().Contain("refused");
        _manager.GetStatus().Should().ContainSingle(s => s.Name == "refused" && !s.IsConnected);
        _manager.GetClient("refused").Should().BeNull();
        _manager.GetAllTools().Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectOneAsync_UnreachableHttpServer_StatusStaysDisconnected()
    {
        var def = HttpDefinition(GetFreeTcpPort());

        var act = () => _manager.ConnectOneAsync("unreachable", def, TestContext.Current.CancellationToken);

        // 软失败：不抛出，连接池保留条目但状态为断连（GetAllTools 不暴露其工具）。
        await act.Should().NotThrowAsync();
        _manager.GetStatus().Should().ContainSingle(s => s.Name == "unreachable" && !s.IsConnected);
        _manager.GetAllTools().Should().BeEmpty();
    }

    // 非法定义在连接前被 IsValid 守卫拦截 —— 连接池中不产生任何条目

    [Fact]
    public async Task ConnectOneAsync_InvalidDefinitionWithoutUrl_NeverEntersConnectionPool()
    {
        var invalid = new McpServerDefinition(McpTransportType.Http); // 缺 Url → IsValid == false

        var act = () => _manager.ConnectOneAsync("invalid", invalid, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        _manager.GetServerNames().Should().NotContain("invalid");
        _manager.GetStatus().Should().BeEmpty();
    }

    // 并发连接同一服务器：连接门闩保证最终只有一个连接条目

    [Fact]
    public async Task ConnectOneAsync_ConcurrentConnectsToSameServer_ProducesSingleEntry()
    {
        var def = HttpDefinition(GetFreeTcpPort());
        var ct = TestContext.Current.CancellationToken;

        var first = _manager.ConnectOneAsync("gated", def, ct);
        var second = _manager.ConnectOneAsync("gated", def, ct);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15), ct);

        _manager.GetServerNames().Count(n => n == "gated").Should().Be(1);
        _manager.GetStatus().Should().ContainSingle(s => s.Name == "gated" && !s.IsConnected);
    }

    // GetConnectionSummary：软失败计入 Failed —— 状态栏三态与 /mcp list 的数据源。
    // 防回归：此前失败服务器对用户完全不可见（0 连接即隐藏）。

    [Fact]
    public async Task GetConnectionSummary_SoftFailedServer_CountedAsFailedWithZeroConnected()
    {
        var def = HttpDefinition(GetFreeTcpPort());
        await _manager.ConnectOneAsync("refused", def, TestContext.Current.CancellationToken);

        var summary = _manager.GetConnectionSummary();

        summary.Failed.Should().Be(1);
        summary.Connected.Should().Be(0);
        summary.Connecting.Should().Be(0);
        summary.ToolCount.Should().Be(0);
        summary.HasActivity.Should().BeTrue();
    }

    // 失败原因被记录（LastError）—— /mcp list 与 /mcp connect 据此展示

    [Fact]
    public async Task GetStatus_SoftFailedServer_ExposesLastError()
    {
        var def = HttpDefinition(GetFreeTcpPort());
        await _manager.ConnectOneAsync("refused", def, TestContext.Current.CancellationToken);

        _manager.GetStatus().Should().ContainSingle(s => s.Name == "refused")
            .Which.LastError.Should().NotBeNullOrEmpty();
    }

    // 重连语义：先摘除旧连接、再查配置 —— 配置已消失的服务器重连后条目被清除

    [Fact]
    public async Task ReconnectServerAsync_ConfigNoLongerPresent_RemovesStaleConnection()
    {
        var def = HttpDefinition(GetFreeTcpPort());
        var ct = TestContext.Current.CancellationToken;
        await _manager.ConnectOneAsync("stale", def, ct);
        _manager.GetServerNames().Should().Contain("stale");

        // 配置加载器在本机找不到 .mcp.json → "not configured" → 跳过重连，
        // 但摘除旧连接的动作已生效：断连条目被清理。
        await _manager.ReconnectServerAsync("stale", ct);

        _manager.GetServerNames().Should().NotContain("stale");
    }

    // Disconnect / 显式断开标记（自动重连不碰手动断开的服务器——由 AutoReconnect 循环消费）

    [Fact]
    public async Task DisconnectAsync_UnknownServer_IsNoOpWithoutThrowing()
    {
        var act = () => _manager.DisconnectAsync("never-connected");

        await act.Should().NotThrowAsync();
        _manager.GetServerNames().Should().NotContain("never-connected");
    }

    // 内置服务器（playwright）按需连接是"正向约定"——不随启动连接

    [Fact]
    public async Task ConnectAllAsync_OnlyBuiltInServersConfigured_ConnectsNothing()
    {
        // playwright 是内置服务（BuiltInMcpServers.IsBuiltIn），启动路径应跳过。
        // 本机无用户/项目级 .mcp.json（测试前置检查），配置即仅内置清单本身。
        await _manager.ConnectAllAsync(TestContext.Current.CancellationToken);

        _manager.GetServerNames().Should().NotContain("playwright");
        _manager.GetConnectedClients().Should().BeEmpty();
        // 内置按需服务器不产生任何连接活动——状态栏/欢迎页不渲染 MCP 指示。
        _manager.GetConnectionSummary().HasActivity.Should().BeFalse();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>
/// 自动重连防回归：软失败落池的条目必须能被 AutoReconnect 循环真实重连——
/// 此前循环调用的 <c>ConnectOneAsync(name, def, ct)</c> 不清理陈旧条目，
/// <c>ConnectOneAsyncCore</c> 的 ContainsKey 早退把每次重连短路成 no-op（死逻辑）。
/// 另验证 def 重载的幂等性：对已连接服务器重复连接不得拆掉活跃连接。
/// </summary>
public sealed class McpAutoReconnectTests : IAsyncDisposable
{
    private static readonly TimeSpan OriginalInterval = McpConnectionManager.AutoReconnectInterval;

    private readonly FlakyEchoServerProvider _provider = new();
    private readonly McpConnectionManager _manager;

    public McpAutoReconnectTests(ITestOutputHelper output)
    {
        // 加速循环：默认 60s 间隔对测试不可接受。interval 是跨测试共享的静态字段，
        // 但候选为空时循环在 LoadAllAsync 之前空转返回，对其他测试构造的 manager 无副作用。
        McpConnectionManager.AutoReconnectInterval = TimeSpan.FromMilliseconds(50);
        _manager = new McpConnectionManager(
            new McpMultiScopeConfigLoader(NullLogger<McpMultiScopeConfigLoader>.Instance),
            new McpElicitationHandler(NullLogger<McpElicitationHandler>.Instance),
            inProcessRegistry: new InProcessMcpServerRegistry([_provider]),
            logger: new DiagnosticsLogger(output));
    }

    /// <summary>诊断用日志：透出探活标记与循环错误到 xUnit 测试输出（ITestOutputHelper）。</summary>
    private sealed class DiagnosticsLogger(ITestOutputHelper output) : ILogger<McpConnectionManager>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => output.WriteLine($"[MCP:{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" :: {exception.GetType().Name}: {exception.Message}")}");
    }

    public async ValueTask DisposeAsync()
    {
        McpConnectionManager.AutoReconnectInterval = OriginalInterval;
        await _manager.DisposeAsync();
    }

    private static McpServerDefinition InProcessDefinition() =>
        // InProcess 的 IsValid 要求 Url/Command 槽位非空（见 McpServerDefinition.IsValid）。
        new(McpTransportType.InProcess, Command: "test://flaky");

    [Fact]
    public async Task AutoReconnect_SoftFailedServer_IsActuallyRetriedAndRecovers()
    {
        var ct = TestContext.Current.CancellationToken;
        _provider.AllowConnections = false;

        // 故障期连接：provider 拒绝 → 软失败落池（IsConnected=false）。
        await _manager.ConnectOneAsync("flaky", InProcessDefinition(), ct);
        _manager.GetStatus().Should().ContainSingle(s => s.Name == "flaky" && !s.IsConnected);

        // 故障恢复后，AutoReconnect 循环必须真实重试并恢复连接（而非被 ContainsKey 早退短路）。
        _provider.AllowConnections = true;

        var recovered = await WaitUntilAsync(
            () => _manager.GetStatus().Any(s => s.Name == "flaky" && s.IsConnected),
            TimeSpan.FromSeconds(10), ct);

        recovered.Should().BeTrue("auto-reconnect must retry soft-failed servers once the failure cause is gone");
        _manager.GetAllTools().Select(t => t.Name).Should().Contain("mcp__flaky__ping");
    }

    [Fact]
    public async Task ConnectOneAsync_DefinitionOverload_AlreadyConnected_KeepsLiveConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        _provider.AllowConnections = true;

        await _manager.ConnectOneAsync("flaky", InProcessDefinition(), ct);
        _manager.GetStatus().Should().ContainSingle(s => s.Name == "flaky" && s.IsConnected);

        // 幂等：对已连接服务器重复连接（def 重载）不得拆掉活跃连接。
        await _manager.ConnectOneAsync("flaky", InProcessDefinition(), ct);

        _manager.GetStatus().Should().ContainSingle(s => s.Name == "flaky" && s.IsConnected && s.ToolCount > 0);
    }

    [Fact]
    public async Task AutoReconnect_RuntimeCrashedServer_IsProbedDisconnectedAndRecovered()
    {
        var ct = TestContext.Current.CancellationToken;
        _provider.AllowConnections = true;

        await _manager.ConnectOneAsync("flaky", InProcessDefinition(), ct);
        _manager.GetStatus().Should().ContainSingle(s => s.Name == "flaky" && s.IsConnected);

        // 模拟运行中崩溃：server 会话终止，但连接池条目 IsConnected 仍为 true（SDK 无断连事件）。
        _provider.KillAll();

        // 探活防回归：ping 探活必须检出崩溃（RuntimeDisconnectDetections 递增），
        // 随后自动重连恢复连接。断连窗口（探活标记 → 同轮/下轮重连）短于测试轮询间隔，
        // 无法直接轮询 !IsConnected；LastError 在重连成功后按 McpServerStatus 语义清空，
        // 故以"探活计数 ≥1 且最终已连接"为联合证据。
        var recovered = await WaitUntilAsync(
            () => _manager.RuntimeDisconnectDetections >= 1
                && _manager.GetStatus().Any(s => s.Name == "flaky" && s.IsConnected),
            TimeSpan.FromSeconds(15), ct);

        recovered.Should().BeTrue("ping probe must detect the crash and auto-reconnect must restore the connection");
        _manager.RuntimeDisconnectDetections.Should().BeGreaterThanOrEqualTo(1);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>可通过开关控制放行的同进程 echo server（模拟"故障 → 恢复"场景）。</summary>
    private sealed class FlakyEchoServerProvider : IInProcessMcpServerProvider
    {
        public volatile bool AllowConnections;

        private readonly object _lock = new();
        private readonly List<CancellationTokenSource> _sessions = [];

        public bool TryCreateServer(string serverName, ITransport serverTransport)
        {
            if (!AllowConnections)
                return false;

            // 每个连接独立会话 CTS——KillAll 只终止已创建的会话，重连时新会话照常启动。
            var sessionCts = new CancellationTokenSource();
            lock (_lock)
                _sessions.Add(sessionCts);

            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = serverName, Version = "1.0.0" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                ToolCollection =
                [
                    McpServerTool.Create(typeof(EchoTools).GetMethod(nameof(EchoTools.Ping))!, target: null),
                ],
            };

            var server = ModelContextProtocol.Server.McpServer.Create(serverTransport, options);
            _ = server.RunAsync(sessionCts.Token);
            return true;
        }

        /// <summary>终止全部运行中的 server 会话（模拟运行中进程崩溃——连接条目仍在池中且 IsConnected=true，但 server 不再响应）。</summary>
        public void KillAll()
        {
            lock (_lock)
            {
                foreach (var session in _sessions)
                    session.Cancel();
                _sessions.Clear();
            }
        }
    }

    private static class EchoTools
    {
        [System.ComponentModel.Description("ping tool")]
        public static string Ping(string input) => $"ping:{input}";
    }
}
