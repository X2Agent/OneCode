using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

/// <summary>
/// 真实官方 MCP Registry 链路集成测试（验证数据源、搜索、详情）。
///
/// <para>默认跳过（需外网访问 https://registry.modelcontextprotocol.io）。
/// 启用方式：设置环境变量 <c>ONECODE_MCP_REGISTRY_TEST=1</c> 后运行。</para>
///
/// <para>验证点：</para>
/// <list type="number">
///   <item><c>Search_ReturnsServers</c>：/mcp search 底层的服务端搜索（search + version=latest）能命中真实 registry。</item>
///   <item><c>GetLatest_ReturnsPackages</c>：/mcp install 底层的 GetLatestAsync 能取到本地 stdio 安装包。</item>
/// </list>
/// </summary>
public sealed class OfficialMcpRegistryIntegrationTests
{
    private const string EnableEnvVar = "ONECODE_MCP_REGISTRY_TEST";
    private const string RegistryBaseUrl = "https://registry.modelcontextprotocol.io";

    /// <summary>为 registry 客户端提供带 BaseAddress 的真实 HttpClient（相对 URL 依赖 BaseAddress）。</summary>
    private sealed class RegistryHttpClientFactory : IHttpClientFactory
    {
        // 复用单实例：搜索/详情各一次请求，复用连接池避免重建 TCP/TLS。
        // 超时 60s > 客户端内置 30s 请求预算（官方 search 端点实测 18~26s）。
        private readonly HttpClient _client = new()
        {
            BaseAddress = new Uri(RegistryBaseUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };

        public HttpClient CreateClient(string name) => _client;
    }

    [Fact]
    public async Task Search_ReturnsServers()
    {
        SkipUnlessEnabled();

        var client = new OfficialMcpRegistryClient(new RegistryHttpClientFactory());
        var results = await client.SearchAsync("github", limit: 10, TestContext.Current.CancellationToken);

        results.Should().NotBeEmpty("官方 registry 服务端 search 较慢（实测 18~26s）但应返回匹配结果");
        results.Should().OnlyContain(s => !string.IsNullOrEmpty(s.Name));
    }

    [Fact]
    public async Task GetLatest_ReturnsPackages()
    {
        SkipUnlessEnabled();

        var client = new OfficialMcpRegistryClient(new RegistryHttpClientFactory());
        var server = await client.GetLatestAsync("agency.kesey/pretrip", TestContext.Current.CancellationToken);

        server.Should().NotBeNull("官方 registry 详情应返回 server 元数据");
        server!.Packages.Should().NotBeNullOrEmpty("本地 stdio server 应声明 packages");
        server.Packages!.Should().Contain(p =>
            p.RegistryType.Equals("npm", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(p.Identifier)
            && p.Transport != null && p.Transport.Type == "stdio");
    }

    private static void SkipUnlessEnabled()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnableEnvVar)))
            Assert.Skip($"Set {EnableEnvVar}=1 to run the real official-registry integration test.");
    }
}
