namespace OneCode.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneCode.App.Services.Mcp;
using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;

/// <summary>
/// 连接层白名单过滤的端到端测试：经 InProcessMcpServerRegistry 起同进程 MCP server
/// （不拉起子进程），验证 McpConnectionManager.LoadAgentToolsAsync 只把白名单命中的
/// 方法暴露进 GetAllTools / GetStatus，且 ReloadToolsAsync 热生效不断开连接。
/// </summary>
public sealed class McpToolWhitelistConnectionTests : IAsyncDisposable
{
    private readonly InProcessMcpServerRegistry _registry = new([new TestEchoServerProvider()]);
    private readonly McpConnectionManager _manager;

    public McpToolWhitelistConnectionTests()
    {
        _manager = new McpConnectionManager(
            new McpMultiScopeConfigLoader(NullLogger<McpMultiScopeConfigLoader>.Instance),
            new McpElicitationHandler(NullLogger<McpElicitationHandler>.Instance),
            inProcessRegistry: _registry);
    }

    public async ValueTask DisposeAsync() => await _manager.DisposeAsync();

    private static McpServerDefinition InProcessDefinition(IReadOnlyList<string>? tools) =>
        // InProcess 的 IsValid 要求 Url/Command 槽位非空（见 McpServerDefinition.IsValid）。
        new(McpTransportType.InProcess, Command: "test://echo", EnabledTools: tools);

    [Fact]
    public async Task ConnectOneAsync_WhitelistNull_ExposesAllServerTools()
    {
        await _manager.ConnectOneAsync("echo", InProcessDefinition(null), TestContext.Current.CancellationToken);

        var names = _manager.GetAllTools().Select(t => t.Name).ToList();
        names.Should().Contain("mcp__echo__alpha");
        names.Should().Contain("mcp__echo__beta");
        _manager.GetStatus().Should().ContainSingle(s => s.Name == "echo" && s.IsConnected && s.ToolCount == 3);
    }

    [Fact]
    public async Task ConnectOneAsync_WhitelistSet_OnlyMatchingToolsExposed()
    {
        await _manager.ConnectOneAsync("echo", InProcessDefinition(["alpha", "g*"]), TestContext.Current.CancellationToken);

        var names = _manager.GetAllTools().Select(t => t.Name).ToList();
        names.Should().Contain("mcp__echo__alpha");
        names.Should().Contain("mcp__echo__gamma");
        names.Should().NotContain("mcp__echo__beta");
        _manager.GetStatus().Should().ContainSingle(s => s.Name == "echo" && s.ToolCount == 2);
    }

    [Fact]
    public async Task ConnectOneAsync_EmptyWhitelist_ConnectedButNoTools()
    {
        await _manager.ConnectOneAsync("echo", InProcessDefinition([]), TestContext.Current.CancellationToken);

        _manager.GetStatus().Should().ContainSingle(s => s.Name == "echo" && s.IsConnected && s.ToolCount == 0);
        _manager.GetAllTools().Should().BeEmpty();
    }

    [Fact]
    public async Task ReloadToolsAsync_ConnectedServer_KeepsFilterAndConnection()
    {
        await _manager.ConnectOneAsync("echo", InProcessDefinition(["alpha"]), TestContext.Current.CancellationToken);
        _manager.GetAllTools().Select(t => t.Name).Should().Contain("mcp__echo__alpha");

        var reloaded = await _manager.ReloadToolsAsync("echo", TestContext.Current.CancellationToken);

        reloaded.Should().BeTrue();
        _manager.GetStatus().Should().ContainSingle(s => s.Name == "echo" && s.IsConnected);
        // Reload 沿用连接定义（配置文件未提供新名单）——过滤结果保持不变。
        _manager.GetAllTools().Select(t => t.Name).Should().Contain("mcp__echo__alpha");
    }

    [Fact]
    public async Task ReloadToolsAsync_UnknownOrDisconnectedServer_ReturnsFalse()
    {
        (await _manager.ReloadToolsAsync("never-connected", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    /// <summary>同进程测试 MCP server：alpha / beta / gamma 三个方法。</summary>
    private sealed class TestEchoServerProvider : IInProcessMcpServerProvider
    {
        public bool TryCreateServer(string serverName, ITransport serverTransport)
        {
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = serverName, Version = "1.0.0" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                ToolCollection =
                [
                    McpServerTool.Create(typeof(EchoTools).GetMethod(nameof(EchoTools.Alpha))!, target: null),
                    McpServerTool.Create(typeof(EchoTools).GetMethod(nameof(EchoTools.Beta))!, target: null),
                    McpServerTool.Create(typeof(EchoTools).GetMethod(nameof(EchoTools.Gamma))!, target: null),
                ],
            };

            var server = ModelContextProtocol.Server.McpServer.Create(serverTransport, options);
            _ = server.RunAsync(TestContext.Current.CancellationToken);
            return true;
        }
    }

    private static class EchoTools
    {
        [System.ComponentModel.Description("alpha tool")]
        public static string Alpha(string input) => $"alpha:{input}";

        [System.ComponentModel.Description("beta tool")]
        public static string Beta(string input) => $"beta:{input}";

        [System.ComponentModel.Description("gamma tool")]
        public static string Gamma(string input) => $"gamma:{input}";
    }
}

/// <summary>
/// <see cref="McpToolFilter"/> 纯逻辑测试：白名单三态语义（null=全放行 / 空=全拒绝 /
/// 非空=模式命中）、通配符匹配与大小写敏感性。
/// </summary>
public sealed class McpToolFilterTests
{
    private static McpServerDefinition Def(params string[] tools) =>
        new(McpTransportType.Stdio, Command: "x", EnabledTools: tools.Length == 0 ? [] : tools);

    // 三态语义

    [Fact]
    public void IsEnabled_NullWhitelist_EnablesEverything()
    {
        var def = new McpServerDefinition(McpTransportType.Stdio, Command: "x", EnabledTools: null);

        McpToolFilter.IsEnabled(def, "anything").Should().BeTrue();
        McpToolFilter.IsEnabled(def, "GET_file_contents").Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_EmptyWhitelist_DisablesEverything()
    {
        var def = Def();

        McpToolFilter.IsEnabled(def, "any").Should().BeFalse();
        McpToolFilter.IsEnabled(def, "").Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_BlankToolName_ReturnsFalse()
    {
        McpToolFilter.IsEnabled(Def("x"), " ").Should().BeFalse();
    }

    // 精确匹配

    [Fact]
    public void IsEnabled_ExactName_CaseInsensitive()
    {
        var def = Def("create_issue");

        McpToolFilter.IsEnabled(def, "create_issue").Should().BeTrue();
        McpToolFilter.IsEnabled(def, "Create_Issue").Should().BeTrue();
        McpToolFilter.IsEnabled(def, "create_issues").Should().BeFalse();
        McpToolFilter.IsEnabled(def, "create").Should().BeFalse();
    }

    // 通配符

    [Theory]
    [InlineData("browser_*", "browser_navigate", true)]
    [InlineData("browser_*", "browser", false)]
    [InlineData("browser_*", "Browser_click", true)]
    [InlineData("*_screenshot", "page_screenshot", true)]
    [InlineData("*_screenshot", "screenshot", false)]
    [InlineData("*_screenshot", "page_screenshot_full", false)]
    [InlineData("*", "anything_at_all", true)]
    [InlineData("get_*", "list_files", false)]
    [InlineData("a*b*c", "aXbYc", true)]
    [InlineData("a*b*c", "abYc", true)]
    [InlineData("a*b*c", "aYcbXc", true)]
    [InlineData("a*b*c", "acb", false)]
    public void IsEnabled_WildcardPatterns_MatchAsDocumented(string pattern, string tool, bool expected)
    {
        McpToolFilter.IsEnabled(Def(pattern), tool).Should().Be(expected);
    }

    [Fact]
    public void IsEnabled_MixedPatterns_AnyHitEnables()
    {
        var def = Def("get_*", "create_issue");

        McpToolFilter.IsEnabled(def, "get_file").Should().BeTrue();
        McpToolFilter.IsEnabled(def, "create_issue").Should().BeTrue();
        McpToolFilter.IsEnabled(def, "delete_file").Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_ListOverload_MatchesDefinitionOverload()
    {
        IReadOnlyList<string>? whitelist = ["get_*"];

        McpToolFilter.IsEnabled(whitelist, "get_x").Should().BeTrue();
        McpToolFilter.IsEnabled((IReadOnlyList<string>?)null, "get_x").Should().BeTrue();
        McpToolFilter.IsEnabled([], "get_x").Should().BeFalse();
    }

    // Matches 边界

    [Fact]
    public void Matches_BlankPattern_ReturnsFalse()
    {
        McpToolFilter.Matches("", "tool").Should().BeFalse();
        McpToolFilter.Matches("  ", "tool").Should().BeFalse();
        McpToolFilter.Matches("tool", "").Should().BeFalse();
    }
}
