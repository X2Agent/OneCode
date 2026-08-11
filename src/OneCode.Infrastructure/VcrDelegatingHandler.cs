using System.Security.Cryptography;

namespace OneCode.Infrastructure;

/// <summary>
/// An HTTP <see cref="DelegatingHandler"/> that records and replays API responses
/// for deterministic testing. When VCR is in "replay" mode, matching requests are
/// served from cached fixtures; in "record" mode, responses are captured to disk.
/// </summary>
public sealed class VcrDelegatingHandler : DelegatingHandler
{
    /// <summary>Fixture 文件名哈希截断长度（16 hex chars = 64-bit 碰撞空间）。</summary>
    private const int FixtureKeyHashLength = 16;

    /// <summary>Request body 哈希截断长度（12 hex chars = 48-bit，仅作为 key 的消歧义部分）。</summary>
    private const int BodyHashLength = 12;

    /// <summary>写入 fixture 时使用（带缩进，便于 diff/审计）。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly VcrMode _mode;
    private readonly string _fixtureDir;

    public VcrDelegatingHandler(VcrMode mode, string? fixtureDir = null)
    {
        _mode = mode;
        _fixtureDir = fixtureDir ?? VcrPaths.HttpFixturesDir;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (!_mode.IsActive())
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        var fixtureKey = await ComputeFixtureKeyAsync(request).ConfigureAwait(false);
        var fixturePath = Path.Combine(_fixtureDir, $"{fixtureKey}.json");

        // Replay mode: try to serve from cache
        if (!_mode.IsRecording() && File.Exists(fixturePath))
        {
            return await ReplayFromFixtureAsync(fixturePath, ct).ConfigureAwait(false);
        }

        // Record mode or no cache: make real request
        var realResponse = await base.SendAsync(request, ct).ConfigureAwait(false);

        if (_mode.IsRecording())
        {
            await RecordToFixtureAsync(fixturePath, realResponse).ConfigureAwait(false);
        }

        return realResponse;
    }

    private static async Task<string> ComputeFixtureKeyAsync(HttpRequestMessage request)
    {
        var sb = new StringBuilder();
        sb.Append(request.Method);
        sb.Append('|');
        sb.Append(request.RequestUri?.GetLeftPart(UriPartial.Query));

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(body))
            {
                var bodyHash = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(body)));
                sb.Append('|');
                sb.Append(bodyHash[..BodyHashLength]);
            }
        }

        var keyHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..FixtureKeyHashLength];
        return keyHash;
    }

    private static async Task<HttpResponseMessage> ReplayFromFixtureAsync(
        string fixturePath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(fixturePath, ct).ConfigureAwait(false);
        var fixture = JsonSerializer.Deserialize<VcrFixture>(json)
            ?? throw new InvalidOperationException("Corrupted VCR fixture.");

        var response = new HttpResponseMessage((System.Net.HttpStatusCode)fixture.StatusCode)
        {
            Content = new StringContent(fixture.Body, Encoding.UTF8, fixture.ContentType ?? "application/json"),
            ReasonPhrase = fixture.ReasonPhrase,
        };

        // Response headers go to response.Headers; content headers go to response.Content.Headers.
        // Separating them avoids TryAddWithoutValidation silently dropping headers
        // placed in the wrong collection.
        foreach (var (key, values) in fixture.ResponseHeaders)
            response.Headers.TryAddWithoutValidation(key, values);

        if (response.Content is not null && fixture.ContentHeaders is not null)
        {
            foreach (var (key, values) in fixture.ContentHeaders)
                response.Content.Headers.TryAddWithoutValidation(key, values);
        }

        return response;
    }

    private static async Task RecordToFixtureAsync(string fixturePath, HttpResponseMessage response)
    {
        var fixture = new VcrFixture
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            ContentType = response.Content?.Headers.ContentType?.MediaType,
            Body = response.Content is not null
                ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                : string.Empty,
            ResponseHeaders = [],
            ContentHeaders = [],
        };

        foreach (var (key, values) in response.Headers)
            fixture.ResponseHeaders[key] = [.. values];

        if (response.Content?.Headers is not null)
        {
            foreach (var (key, values) in response.Content.Headers)
                fixture.ContentHeaders[key] = [.. values];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        var json = JsonSerializer.Serialize(fixture, WriteOptions);
        await File.WriteAllTextAsync(fixturePath, json).ConfigureAwait(false);
    }

    /// <summary>
    /// HTTP fixture。ResponseHeaders 与 ContentHeaders 分开存放，回放时分别填入
    /// <see cref="HttpResponseMessage.Headers"/> 与 <see cref="HttpContent.Headers"/>，
    /// 避免 TryAddWithoutValidation 因分类错误静默丢弃头部。
    /// </summary>
    private sealed record VcrFixture
    {
        public int StatusCode { get; init; } = 200;
        public string? ReasonPhrase { get; init; }
        public string? ContentType { get; init; }
        public string Body { get; init; } = "";
        public Dictionary<string, string[]> ResponseHeaders { get; init; } = new();
        public Dictionary<string, string[]> ContentHeaders { get; init; } = new();
    }
}
