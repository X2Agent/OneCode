using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

/// <summary>
/// 官方 MCP Registry 客户端的单元测试：用可路由的 stub HTTP handler 模拟 registry。
/// 关键防回归：① 搜索必须且只能走服务端 search（单次请求）——历史实现曾全量分页拉取
/// （registry 数万版本条目、300+ 页导致 /mcp search 卡死数分钟且 UI 无反馈），也曾因官方
/// search 端点慢（实测 18~26s 返回）误判挂起而降级为部分覆盖的本地扫描；现行为：一次
/// 服务端 search 请求，"慢"由宽请求预算覆盖，空结果/失败绝不触发第二次请求；
/// ② isLatest 去重、deleted 跳过、deprecated 打标；③ 单查端点按规范 URL 编码 serverName；
/// ④ 超时冒泡（不能吞成空结果）。不访问真实网络，故不需要 gate 环境变量。
/// </summary>
public sealed class OfficialMcpRegistryClientTests
{
    /// <summary>按 URL 谓词路由响应的 stub handler；无匹配路由视为 registry 挂起（永不返回，直到取消）。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(Func<string, bool> Match, string Json)> _routes = [];

        public List<string> RequestedUrls { get; } = [];

        public void Route(Func<string, bool> match, string json) => _routes.Add((match, json));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.PathAndQuery;
            RequestedUrls.Add(url);
            var route = _routes.FirstOrDefault(r => r.Match(url));
            if (route.Json is null)
            {
                // 真·挂起：一直等待到调用方/客户端预算取消，用于验证超时由客户端预算触发
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                throw new UnreachableException();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(route.Json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;

        public StubHttpClientFactory(StubHandler handler) => _handler = handler;

        // 30s 超时故意大于测试注入的客户端请求预算，用于证明超时由客户端预算而非 HttpClient 决定
        public HttpClient CreateClient(string name) => new(_handler)
        {
            BaseAddress = new Uri("https://registry.example.test"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>注入 1s 请求预算：挂起场景快速超时，同时保持测试套件速度。</summary>
    private static (OfficialMcpRegistryClient Client, StubHandler Handler) CreateClient(
        Action<StubHandler>? setup = null)
    {
        var handler = new StubHandler();
        setup?.Invoke(handler);
        return (new OfficialMcpRegistryClient(
            new StubHttpClientFactory(handler), requestBudget: TimeSpan.FromSeconds(1)), handler);
    }

    private static string PageJson(string? nextCursor, params OfficialRegistryServerEntry[] entries)
        => JsonSerializer.Serialize(new OfficialRegistryListResponse
        {
            Servers = [.. entries],
            Metadata = new OfficialRegistryListMetadata { NextCursor = nextCursor, Count = entries.Length },
        });

    private static OfficialRegistryServerEntry Entry(
        string name,
        string? title,
        string? description,
        bool isLatest,
        string status = "active",
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
                ["io.modelcontextprotocol.registry/official"] = new Dictionary<string, object>
                {
                    ["isLatest"] = isLatest,
                    ["status"] = status,
                },
            }),
        };

    [Fact]
    public async Task SearchAsync_UsesServerSideSearch_SingleRequest()
    {
        var (client, handler) = CreateClient(h => h.Route(
            u => u.Contains("search="),
            PageJson(null,
                Entry("com.github/api", "GitHub API", "manage repos", isLatest: true),
                Entry("com.example/other", "Other", "connects to github issues", isLatest: true))));

        var results = await client.SearchAsync("github", limit: 20, TestContext.Current.CancellationToken);

        // 防回归：恰好一次服务端 search 请求——历史实现曾退回全量分页扫描（300+ 页卡死），
        // 也曾因官方 search 端点慢（实测 18~26s）误判挂起而降级为部分覆盖扫描
        handler.RequestedUrls.Should().ContainSingle("搜索必须且只能走服务端 search 端点");
        handler.RequestedUrls[0].Should().Be("/v0.1/servers?search=github&version=latest&limit=20");

        // 客户端仅排序：name 命中（打分 3）排最前，description 命中（打分 1）不丢弃、排靠后
        results.Should().HaveCount(2);
        results[0].Name.Should().Be("com.github/api");
        results[1].Name.Should().Be("com.example/other");
    }

    [Fact]
    public async Task SearchAsync_EmptyServerResult_StopsImmediately()
    {
        // 空结果即结束——绝不能因"没搜到"退回分页扫描兜底（历史降级实现的回归路径）
        var (client, handler) = CreateClient(h => h.Route(u => u.Contains("search="), PageJson(null)));

        var results = await client.SearchAsync("no-such-thing", limit: 20, TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
        handler.RequestedUrls.Should().ContainSingle("空结果即结束，不得发起第二次请求");
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty_WithoutNetwork()
    {
        var (client, handler) = CreateClient();

        var results = await client.SearchAsync("   ", limit: 20, TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
        handler.RequestedUrls.Should().BeEmpty("空查询不应发起网络请求");
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesByLatestVersion()
    {
        // 同一 name 两个版本条目：isLatest=true 的版本（v2）应胜出
        var (client, _) = CreateClient(h => h.Route(
            u => u.Contains("search="),
            PageJson(null,
                Entry("com.github/api", "GitHub API", "v1", isLatest: false),
                Entry("com.github/api", "GitHub API", "v2", isLatest: true))));

        var results = await client.SearchAsync("github", limit: 20, TestContext.Current.CancellationToken);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("com.github/api");
        results[0].Description.Should().Be("v2");
    }

    [Fact]
    public async Task SearchAsync_SkipsDeleted_MarksDeprecated_ActiveFirst()
    {
        var (client, handler) = CreateClient(h => h.Route(
            u => u.Contains("search="),
            PageJson(null,
                Entry("ac.test/sql-dead", null, null, isLatest: true, status: "deleted"),
                Entry("ac.test/sql-old", null, null, isLatest: true, status: "deprecated"),
                Entry("ac.test/sql-active", null, null, isLatest: true, status: "active"))));

        var results = await client.SearchAsync("sql", limit: 20, TestContext.Current.CancellationToken);

        results.Select(s => s.Name).Should().Equal("ac.test/sql-active", "ac.test/sql-old");
        results.Single(s => s.Name == "ac.test/sql-old").IsDeprecated.Should().BeTrue();
        results.Should().NotContain(s => s.Name == "ac.test/sql-dead", "deleted 条目不应出现在结果中");
        handler.RequestedUrls.Should().ContainSingle("无降级扫描：一次服务端 search 即结束");
    }

    [Fact]
    public async Task GetLatestAsync_UrlEncodesServerName()
    {
        var (client, handler) = CreateClient(h => h.Route(_ => true,
            """{"server":{"name":"io.github.user/weather","version":"1.0.2"}}"""));

        var server = await client.GetLatestAsync("io.github.user/weather", TestContext.Current.CancellationToken);

        server!.Name.Should().Be("io.github.user/weather");
        // 规范要求 serverName 整体 URL 编码（示例 com.example%2Fmy-server）；实测原始斜杠 404
        handler.RequestedUrls.Should().ContainSingle()
            .Which.Should().Be("/v0.1/servers/io.github.user%2Fweather/versions/latest");
    }

    [Fact]
    public async Task GetLatestAsync_WhenRegistryHangs_TimesOutWithinBudget()
    {
        var (client, _) = CreateClient(); // 无路由 → registry 挂起

        var sw = Stopwatch.StartNew();
        var act = async () => await client.GetLatestAsync("io.github.user/x", TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "registry 挂起时超时必须冒泡，不能吞成 null（否则用户只会看到 not found）");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "注入的 1s 预算生效，而非等 HttpClient 的 30s 超时");
    }
}
