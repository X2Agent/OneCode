using System.Net;
using OneCode.Infrastructure;

namespace OneCode.Tests;

public sealed class VcrDelegatingHandlerTests : IDisposable
{
    private readonly string _fixtureDir;

    public VcrDelegatingHandlerTests()
    {
        _fixtureDir = Path.Combine(Path.GetTempPath(), $"onecode-vcr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixtureDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_fixtureDir))
                Directory.Delete(_fixtureDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; temp files are harmless.
        }
    }

    [Fact]
    public async Task SendAsync_VcrInactive_PassesThroughToInnerHandler()
    {
        var vcr = CreateVcrMode(isActive: false, isRecording: false);
        var inner = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("real"),
        });
        var sut = new VcrDelegatingHandler(vcr, _fixtureDir)
        {
            InnerHandler = inner,
        };
        using var client = new HttpClient(sut);

        var response = await client.GetAsync("https://example.com/api", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("real");
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_RecordingMode_SavesFixtureAndReturnsRealResponse()
    {
        var vcr = CreateVcrMode(isActive: true, isRecording: true);
        var inner = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("created"),
        });
        var sut = new VcrDelegatingHandler(vcr, _fixtureDir)
        {
            InnerHandler = inner,
        };
        using var client = new HttpClient(sut);

        var response = await client.PostAsync(
            "https://example.com/items",
            new StringContent("{\"name\":\"x\"}"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("created");
        inner.CallCount.Should().Be(1);
        Directory.EnumerateFiles(_fixtureDir, "*.json").Should().ContainSingle();
    }

    [Fact]
    public async Task SendAsync_ReplayModeWithFixture_ReturnsCachedResponseWithoutCallingInner()
    {
        var vcr = CreateVcrMode(isActive: true, isRecording: true);
        var inner = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cached"),
            Headers = { { "X-Custom", "value" } },
        });
        var sut = new VcrDelegatingHandler(vcr, _fixtureDir)
        {
            InnerHandler = inner,
        };
        using var client = new HttpClient(sut);
        using var first = await client.GetAsync("https://example.com/api", TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var replayVcr = CreateVcrMode(isActive: true, isRecording: false);
        var replayInner = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("should-not-use"),
        });
        var replaySut = new VcrDelegatingHandler(replayVcr, _fixtureDir)
        {
            InnerHandler = replayInner,
        };
        using var replayClient = new HttpClient(replaySut);
        using var second = await replayClient.GetAsync("https://example.com/api", TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("cached");
        second.Headers.GetValues("X-Custom").Should().ContainSingle().Which.Should().Be("value");
        replayInner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_DifferentBodiesProduceDifferentFixtures()
    {
        var vcr = CreateVcrMode(isActive: true, isRecording: true);
        var inner = new StubHttpHandler(async req =>
        {
            var body = req.Content is null
                ? string.Empty
                : await req.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"echo-{body}"),
            };
        });
        var sut = new VcrDelegatingHandler(vcr, _fixtureDir)
        {
            InnerHandler = inner,
        };
        using var client = new HttpClient(sut);

        await client.PostAsync("https://example.com/echo", new StringContent("a"), TestContext.Current.CancellationToken);
        await client.PostAsync("https://example.com/echo", new StringContent("b"), TestContext.Current.CancellationToken);

        Directory.EnumerateFiles(_fixtureDir, "*.json").Should().HaveCount(2);
    }

    [Fact]
    public async Task SendAsync_DifferentQueryStringsProduceDifferentFixtures()
    {
        // Regression test: fixture key must include the query string.
        var vcr = CreateVcrMode(isActive: true, isRecording: true);
        var inner = new StubHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok"),
        });
        var sut = new VcrDelegatingHandler(vcr, _fixtureDir)
        {
            InnerHandler = inner,
        };
        using var client = new HttpClient(sut);

        await client.GetAsync("https://example.com/search?q=hello", TestContext.Current.CancellationToken);
        await client.GetAsync("https://example.com/search?q=world", TestContext.Current.CancellationToken);

        Directory.EnumerateFiles(_fixtureDir, "*.json").Should().HaveCount(2);
    }

    private static VcrMode CreateVcrMode(bool isActive, bool isRecording) =>
        isActive switch
        {
            false => VcrMode.Inactive,
            true when isRecording => VcrMode.Record,
            _ => VcrMode.Replay,
        };

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _factory;
        private readonly HttpResponseMessage? _fixedResponse;

        public StubHttpHandler(HttpResponseMessage response)
        {
            _fixedResponse = response;
        }

        public StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> factory)
        {
            _factory = factory;
        }

        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_factory is not null)
                return await _factory(request).ConfigureAwait(false);
            return _fixedResponse!;
        }
    }
}
