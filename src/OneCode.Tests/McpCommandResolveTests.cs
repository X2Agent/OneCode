using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.App.Commands;
using OneCode.Core.Commands;
using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

/// <summary>
/// /mcp install 目标解析测试：
/// ① 纯函数内核 InstallTargetResolver——序号语法（非序号输入交还名字通道）、
///    短名按 server name 子串消歧（title/description 一律不参与安装决策）；
/// ② ResolveInstallTargetAsync 端到端——序号引用最近一次编号缓存、空缓存/越界终态报错、
///    短名回退只发一次 search 请求、歧义列表写回缓存使编号可直接回选。
/// registry 用可路由 stub handler 模拟，不访问真实网络；无路由请求视为挂起（1s 预算超时），
/// 便于用 RequestedUrls 断言"没碰网络"。
/// </summary>
public sealed class McpCommandResolveTests
{
    // ---- InstallTargetResolver（纯函数内核） ----

    [Fact]
    public void TryParseIndex_ValidOneBasedNumber_ReturnsIndex()
    {
        var ok = InstallTargetResolver.TryParseIndex("2", cacheCount: 3, out var index, out var error);

        ok.Should().BeTrue();
        index.Should().Be(2);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("0", "not a valid result number")]
    [InlineData("4", "out of range")]
    public void TryParseIndex_ZeroOrOutOfRange_TerminalError(string raw, string expectedFragment)
    {
        var ok = InstallTargetResolver.TryParseIndex(raw, cacheCount: 3, out var index, out var error);

        // 序号语法成立 → 终态错误，绝不能误落名字通道
        ok.Should().BeTrue();
        index.Should().Be(0);
        error.Should().NotBeNull().And.Contain(expectedFragment);
    }

    [Fact]
    public void TryParseIndex_EmptyCache_TerminalErrorWithSearchHint()
    {
        var ok = InstallTargetResolver.TryParseIndex("1", cacheCount: 0, out _, out var error);

        ok.Should().BeTrue();
        error.Should().NotBeNull().And.Contain("/mcp search");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("io.github.user/weather")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("１")]
    public void TryParseIndex_NonIndexSyntax_ReturnsFalseForNameChannel(string raw)
    {
        // 非序号输入必须交还给限定名/短名通道，且不产生终态错误
        var ok = InstallTargetResolver.TryParseIndex(raw, cacheCount: 3, out var index, out var error);

        ok.Should().BeFalse();
        index.Should().Be(0);
        error.Should().BeNull();
    }

    [Fact]
    public void TryPickShortNameMatch_NameContainsShortName_CaseInsensitive()
    {
        var results = new[] { Server("com.example/Weather-Server"), Server("io.github.other/notes") };

        var ok = InstallTargetResolver.TryPickShortNameMatch("weather-server", results, out var unique, out var candidates);

        ok.Should().BeTrue();
        unique!.Name.Should().Be("com.example/Weather-Server");
        candidates.Should().ContainSingle();
    }

    [Fact]
    public void TryPickShortNameMatch_MultipleHits_ReturnsCandidatesNotUnique()
    {
        var results = new[] { Server("com.example/weather"), Server("io.github.user/weather") };

        var ok = InstallTargetResolver.TryPickShortNameMatch("weather", results, out var unique, out var candidates);

        ok.Should().BeFalse();
        unique.Should().BeNull();
        candidates.Should().HaveCount(2);
    }

    [Fact]
    public void TryPickShortNameMatch_TitleOrDescriptionOnlyHit_IsIgnored()
    {
        // 防回归：title/description 提到 "weather" 但 server name 不含 → 不是安装目标。
        // 按"介绍里提到"就装包是危险动作。
        var results = new[]
        {
            Server("com.example/notes", title: "Weather Notes", description: "best weather forecasts for your notebook"),
        };

        var ok = InstallTargetResolver.TryPickShortNameMatch("weather", results, out var unique, out var candidates);

        ok.Should().BeFalse();
        unique.Should().BeNull();
        candidates.Should().BeEmpty();
    }

    [Fact]
    public void TryPickShortNameMatch_NoNameHit_EmptyCandidates()
    {
        var results = new[] { Server("com.example/notes") };

        var ok = InstallTargetResolver.TryPickShortNameMatch("weather", results, out _, out var candidates);

        ok.Should().BeFalse();
        candidates.Should().BeEmpty();
    }

    // ---- ResolveInstallTargetAsync（端到端，stub registry） ----

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(Func<string, bool> Match, string? Json)> _routes = [];

        public List<string> RequestedUrls { get; } = [];

        public void Route(Func<string, bool> match, string json) => _routes.Add((match, json));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.PathAndQuery;
            RequestedUrls.Add(url);
            var route = _routes.FirstOrDefault(r => r.Match(url));
            if (route.Json is null)
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(route.Json ?? "", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;

        public StubHttpClientFactory(StubHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler)
        {
            BaseAddress = new Uri("https://registry.example.test"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static OfficialRegistryServer Server(
        string name,
        string? title = null,
        string? description = null,
        List<OfficialRegistryPackage>? packages = null) => new()
    {
        Name = name,
        Title = title,
        Description = description,
        Version = "1.0.0",
        Packages = packages,
    };

    private static OfficialRegistryServerEntry Entry(OfficialRegistryServer server) => new()
    {
        Server = server,
        Meta = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["io.modelcontextprotocol.registry/official"] = new Dictionary<string, object>
            {
                ["isLatest"] = true,
                ["status"] = "active",
            },
        }),
    };

    private static string LatestJson(OfficialRegistryServer server)
        => JsonSerializer.Serialize(new OfficialRegistryLatestResponse { Server = server });

    private static string PageJson(params OfficialRegistryServerEntry[] entries)
        => JsonSerializer.Serialize(new OfficialRegistryListResponse { Servers = [.. entries] });

    private static (OfficialMcpRegistryClient Client, StubHandler Handler) CreateRegistryClient()
    {
        var handler = new StubHandler();
        return (new OfficialMcpRegistryClient(
            new StubHttpClientFactory(handler), requestBudget: TimeSpan.FromSeconds(1)), handler);
    }

    private static McpCommand CreateCommand(OfficialMcpRegistryClient client)
        => new(
            Substitute.For<IMcpConnectionManager>(),
            client,
            new McpMultiScopeConfigLoader(Substitute.For<ILogger<McpMultiScopeConfigLoader>>()),
            Substitute.For<ILogger<McpCommand>>());

    [Fact]
    public async Task Install_RemoteOnlyServer_SuggestsAddInstead()
    {
        var (client, handler) = CreateRegistryClient();
        handler.Route(u => u.Contains("/versions/latest"), LatestJson(Server("com.example/hosted")));
        var sut = CreateCommand(client);

        var result = await sut.ExecuteAsync(["install", "com.example/hosted"]);

        result.Should().BeOfType<CommandResult.TextResult>()
            .Which.Value.Should().Contain("no installable local package")
            .And.Contain("/mcp add");
        handler.RequestedUrls.Should().ContainSingle().Which.Should().Contain("/versions/latest");
    }

    [Fact]
    public async Task Resolve_NumberedTarget_ResolvesToCachedQualifiedName()
    {
        var (client, handler) = CreateRegistryClient();
        // 只挂 search 路由：若解析后又去查 registry（latest），会因挂起超时使测试失败
        handler.Route(u => u.Contains("search="), PageJson(Entry(Server("io.github.user/weather-cached"))));
        var sut = CreateCommand(client);

        await sut.ExecuteAsync(["search", "weather"]);
        var resolution = await sut.ResolveInstallTargetAsync("1", TestContext.Current.CancellationToken);

        resolution.QualifiedName.Should().Be("io.github.user/weather-cached");
        resolution.Error.Should().BeNull();
        handler.RequestedUrls.Should().ContainSingle("序号解析只做名字还原，不消费缓存元数据");
    }

    [Fact]
    public async Task Resolve_NumberWithoutCache_TerminalError()
    {
        var (client, handler) = CreateRegistryClient();
        var sut = CreateCommand(client);

        var resolution = await sut.ResolveInstallTargetAsync("1", TestContext.Current.CancellationToken);

        resolution.QualifiedName.Should().BeNull();
        resolution.Error.Should().NotBeNull().And.Contain("/mcp search");
        handler.RequestedUrls.Should().BeEmpty("纯本地解析，不碰网络");
    }

    [Fact]
    public async Task Resolve_NumberOutOfRange_TerminalError()
    {
        var (client, handler) = CreateRegistryClient();
        handler.Route(u => u.Contains("search="), PageJson(Entry(Server("com.example/first-hit"))));
        var sut = CreateCommand(client);

        // 先建立非空缓存（1 条），再引用越界编号
        await sut.ExecuteAsync(["search", "first"]);
        var resolution = await sut.ResolveInstallTargetAsync("7", TestContext.Current.CancellationToken);

        resolution.Error.Should().NotBeNull().And.Contain("out of range");
        handler.RequestedUrls.Should().ContainSingle("越界报错纯本地，不再请求网络");
    }

    [Fact]
    public async Task Resolve_QualifiedName_PassesThroughWithoutRegistryCall()
    {
        var (client, handler) = CreateRegistryClient();
        var sut = CreateCommand(client);

        var resolution = await sut.ResolveInstallTargetAsync("com.example/weather", TestContext.Current.CancellationToken);

        resolution.QualifiedName.Should().Be("com.example/weather");
        handler.RequestedUrls.Should().BeEmpty("限定名原样放行，元数据由安装步骤拉取");
    }

    [Fact]
    public async Task Resolve_ShortName_UniqueNameHit_AutoAdoptsWithSingleSearch()
    {
        var (client, handler) = CreateRegistryClient();
        handler.Route(u => u.Contains("search="), PageJson(
            Entry(Server("com.example/notes", description: "weather-adjacent but name does not match")),
            Entry(Server("io.github.user/weather", description: "the one and only"))));
        var sut = CreateCommand(client);

        var resolution = await sut.ResolveInstallTargetAsync("weather", TestContext.Current.CancellationToken);

        // 防 404 双查回归：短名直接走 search（一次请求），绝不先碰单查端点
        var requested = handler.RequestedUrls.Should().ContainSingle().Subject;
        requested.Should().Contain("search=");
        resolution.QualifiedName.Should().Be("io.github.user/weather");
    }

    [Fact]
    public async Task Resolve_ShortName_Ambiguous_WritesCandidatesIntoCache()
    {
        var (client, handler) = CreateRegistryClient();
        handler.Route(u => u.Contains("search="), PageJson(
            Entry(Server("com.example/weather")),
            Entry(Server("io.github.user/weather"))));
        var sut = CreateCommand(client);

        var resolution = await sut.ResolveInstallTargetAsync("weather", TestContext.Current.CancellationToken);

        resolution.Error.Should().NotBeNull().And.Contain("ambiguous");
        // 歧义候选必须写回编号缓存，使提示中的编号可直接回选
        var followUp = await sut.ResolveInstallTargetAsync("2", TestContext.Current.CancellationToken);
        followUp.QualifiedName.Should().Be("io.github.user/weather");
        handler.RequestedUrls.Should().ContainSingle("候选来自同一次 search，回选编号不再请求网络");
    }

    [Fact]
    public async Task Resolve_ShortName_NoMatch_TerminalNotFound()
    {
        var (client, handler) = CreateRegistryClient();
        handler.Route(u => u.Contains("search="), PageJson(Entry(Server("com.example/notes"))));
        var sut = CreateCommand(client);

        var resolution = await sut.ResolveInstallTargetAsync("weather", TestContext.Current.CancellationToken);

        resolution.QualifiedName.Should().BeNull();
        resolution.Error.Should().NotBeNull().And.Contain("not found");
    }

    [Fact]
    public async Task SearchThenResolve_CacheTracksLastList_EmptyResultClearsCache()
    {
        var (client, handler) = CreateRegistryClient();
        // 空结果路由先于通用路由：First-match 语义
        handler.Route(u => u.Contains("search=empty-no-hit"), PageJson());
        handler.Route(u => u.Contains("search="), PageJson(Entry(Server("com.example/first-hit"))));
        var sut = CreateCommand(client);

        await sut.ExecuteAsync(["search", "first"]);
        await sut.ExecuteAsync(["search", "empty-no-hit"]);
        var resolution = await sut.ResolveInstallTargetAsync("1", TestContext.Current.CancellationToken);

        // 空结果清空缓存：编号不得引用过期列表
        resolution.QualifiedName.Should().BeNull();
        resolution.Error.Should().NotBeNull().And.Contain("/mcp search");
    }
}

