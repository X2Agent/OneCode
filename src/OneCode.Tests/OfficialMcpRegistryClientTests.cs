using System.Net;
using System.Text;
using System.Text.Json;
using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

/// <summary>
/// 官方 MCP Registry 客户端的单元测试：用 stub HTTP handler 提供预置 JSON，
/// 验证客户端过滤（name/title/description 打分排序）与分页去重（isLatest 胜出）逻辑。
/// 不访问真实网络，故不需要 gate 环境变量。
/// </summary>
public sealed class OfficialMcpRegistryClientTests
{
    /// <summary>按 URL 是否含 cursor 返回不同页的 stub handler。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _firstPageJson;
        private readonly string _secondPageJson;

        public StubHandler(string firstPageJson, string secondPageJson)
        {
            _firstPageJson = firstPageJson;
            _secondPageJson = secondPageJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isSecondPage = request.RequestUri!.ToString().Contains("cursor=", StringComparison.Ordinal);
            var json = isSecondPage ? _secondPageJson : _firstPageJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(StubHandler handler)
        {
            _client = new HttpClient(handler) { BaseAddress = new Uri("https://registry.example.test") };
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private static OfficialMcpRegistryClient CreateClient(string firstPage, string secondPage)
        => new(new StubHttpClientFactory(new StubHandler(firstPage, secondPage)));

    private static string PageJson(string? nextCursor, params OfficialRegistryServerEntry[] entries)
        => JsonSerializer.Serialize(new OfficialRegistryListResponse
        {
            Servers = [.. entries],
            Metadata = new OfficialRegistryListMetadata { NextCursor = nextCursor },
        });

    private static OfficialRegistryServerEntry Entry(
        string name,
        string? title,
        string? description,
        bool isLatest,
        string registryType = "npm",
        string identifier = "some-pkg")
        => new()
        {
            Server = new OfficialRegistryServer
            {
                Name = name,
                Title = title,
                Description = description,
                Version = "1.0.0",
                Packages =
                [
                    new OfficialRegistryPackage
                    {
                        RegistryType = registryType,
                        Identifier = identifier,
                        Transport = new OfficialRegistryTransport { Type = "stdio" },
                    },
                ],
            },
            Meta = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["io.modelcontextprotocol.registry/official"] = new Dictionary<string, object> { ["isLatest"] = isLatest },
            }),
        };

    [Fact]
    public async Task SearchAsync_FiltersByNameTitleDescription_AndOrdersByRelevance()
    {
        // 单页：name 匹配（com.github/api）+ description 匹配（com.example/other）
        var firstPage = PageJson(null,
            Entry("com.github/api", "GitHub API", "manage repos", isLatest: true),
            Entry("com.example/other", "Other", "connects to github issues", isLatest: true));

        var client = CreateClient(firstPage, PageJson(null));
        var results = await client.SearchAsync("github", limit: 20, TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
        // name 匹配排最前，description 匹配靠后
        results[0].Name.Should().Be("com.github/api");
        results[1].Name.Should().Be("com.example/other");
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty()
    {
        var client = CreateClient(PageJson(null), PageJson(null));

        var results = await client.SearchAsync("   ", limit: 20, TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesByLatestVersion()
    {
        // 两页：第一页旧版本（isLatest=false），第二页 latest（isLatest=true）应覆盖
        var firstPage = PageJson("cursor-1",
            Entry("com.github/api", "GitHub API", "v1", isLatest: false));
        var secondPage = PageJson(null,
            Entry("com.github/api", "GitHub API", "v2", isLatest: true));

        var client = CreateClient(firstPage, secondPage);
        var results = await client.SearchAsync("github", limit: 20, TestContext.Current.CancellationToken);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("com.github/api");
        results[0].Description.Should().Be("v2");
    }
}
