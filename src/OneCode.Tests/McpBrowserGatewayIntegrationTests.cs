using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Mcp;
using OneCode.App.Tools;
using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

/// <summary>
/// 真实 MCP 链路集成测试：<see cref="McpConnectionManager"/> 加载内置 playwright 服务
/// 并按需连接（stdio：npx @playwright/mcp），验证 <see cref="McpBrowserGateway"/> 的
/// browser_navigate + browser_snapshot 调用能返回真实页面文本。
///
/// <para>默认跳过（需要本机 playwright MCP 可用，且会启动真实浏览器）。
/// 启用方式：设置环境变量 <c>ONECODE_PLAYWRIGHT_MCP_TEST=1</c> 后运行。</para>
///
/// <para>验证点：内置服务合并、按需连接（ConnectOneAsync）、工具名
/// （browser_navigate / browser_snapshot）、参数传递、返回内容解析——即生产调用路径
/// McpBrowserGateway.RenderAsync 的完整链路。</para>
/// </summary>
public sealed class McpBrowserGatewayIntegrationTests
{
    private const string EnableEnvVar = "ONECODE_PLAYWRIGHT_MCP_TEST";

    [Fact]
    public async Task RenderAsync_WithRealPlaywrightMcp_ReturnsPageText()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnableEnvVar)))
        {
            Assert.Skip($"Set {EnableEnvVar}=1 to run the real-MCP integration test.");
        }

        var ct = TestContext.Current.CancellationToken;

        // 1. 真实 McpConnectionManager：内置 playwright 服务，按需连接。
        var manager = new McpConnectionManager(
            new McpMultiScopeConfigLoader(NullLogger<McpMultiScopeConfigLoader>.Instance),
            new McpElicitationHandler(NullLogger<McpElicitationHandler>.Instance));

        await using (manager)
        {
            // 2. 构造被测对象，走与生产一致的按需连接 + 渲染路径。
            var sut = new McpBrowserGateway(manager, NullLogger<McpBrowserGateway>.Instance);

            // 3. 渲染真实页面（60s：首次启动浏览器可能较慢）。
            var result = await sut.RenderAsync("https://example.com/", timeoutMs: 60_000, ct)
                .ConfigureAwait(false);

            result.Should().NotBeNullOrWhiteSpace("browser_navigate + browser_snapshot 应返回页面 ARIA 快照文本");
            result.Should().Contain("Example Domain");
        }
    }
}
