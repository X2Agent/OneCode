using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// IChatClient 装饰器：在 IChatClient 语义层（而非 HTTP 层）录制/回放 LLM 调用。
///
/// 与 <see cref="VcrDelegatingHandler"/>（HTTP 层，用于 WebSearch/WebFetch）互补。
/// 本装饰器工作在语义层，能正确处理 LLM 的流式响应
/// (<see cref="IAsyncEnumerable{ChatResponseUpdate}"/>)，不会像 HTTP 层
/// <c>ReadAsStringAsync</c> 那样消费流导致下游读不到内容。
///
/// 装饰器链位置：最外层（ProviderAware 之上）。回放命中时直接返回缓存，
/// 不走 retry/watchdog/real HTTP。
///
/// fixture 存储在 <c>~/.onecode/vcr/chat/{hash}.json</c>（流式用 <c>.stream.json</c> 后缀）。
/// </summary>
public sealed class VcrChatClientDecorator : IChatClient
{
    /// <summary>Fixture 文件名哈希截断长度（16 hex chars = 64-bit 碰撞空间，足够本地缓存）。</summary>
    private const int FixtureKeyHashLength = 16;

    /// <summary>
    /// AIJsonUtilities.DefaultOptions 配置了 AIContent 多态转换器，
    /// 能正确序列化 ChatMessage.Contents (IList&lt;AIContent&gt;) 中的
    /// TextContent / FunctionCallContent / FunctionResultContent 等子类。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = AIJsonUtilities.DefaultOptions;

    /// <summary>写入 fixture 时使用（带缩进，便于 diff/审计）；读取时使用 <see cref="JsonOptions"/>。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new(JsonOptions) { WriteIndented = true };

    private readonly IChatClient _inner;
    private readonly VcrMode _mode;
    private readonly string _fixtureDir;

    public VcrChatClientDecorator(IChatClient inner, VcrMode mode, string? fixtureDir = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _mode = mode;
        _fixtureDir = fixtureDir ?? VcrPaths.ChatFixturesDir;
    }

    public void Dispose() => _inner.Dispose();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.GetService(serviceType, serviceKey);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_mode.IsActive())
            return await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var fixturePath = await ComputeFixturePathAsync(messages, options, isStreaming: false).ConfigureAwait(false);

        // Replay mode: serve from cache
        if (!_mode.IsRecording() && File.Exists(fixturePath))
        {
            return await LoadNonStreamingFixtureAsync(fixturePath, cancellationToken).ConfigureAwait(false);
        }

        // Record mode or cache miss: make real request
        var realResponse = await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        if (_mode.IsRecording())
        {
            await SaveNonStreamingFixtureAsync(fixturePath, realResponse, cancellationToken).ConfigureAwait(false);
        }

        return realResponse;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_mode.IsActive())
        {
            await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
            yield break;
        }

        var fixturePath = await ComputeFixturePathAsync(messages, options, isStreaming: true).ConfigureAwait(false);

        // Replay mode: serve from cache
        if (!_mode.IsRecording() && File.Exists(fixturePath))
        {
            var cached = await LoadStreamingFixtureAsync(fixturePath, cancellationToken).ConfigureAwait(false);
            foreach (var update in cached)
                yield return update;
            yield break;
        }

        // Record mode or cache miss: stream from inner, collect updates
        var collected = new List<ChatResponseUpdate>();
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            collected.Add(update);
            yield return update;
        }

        if (_mode.IsRecording())
        {
            await SaveStreamingFixtureAsync(fixturePath, collected, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 计算 fixture 文件路径。
    ///
    /// Key 范围（故意为之，仅基于以下两项）：
    /// <list type="bullet">
    /// <item><see cref="ChatOptions.ModelId"/> — 不同模型 fixture 隔离</item>
    /// <item>messages 的 JSON 序列化 — 包含完整对话历史、工具调用结果（FunctionResultContent）等</item>
    /// </list>
    /// 不参与 key 的项及原因：
    /// <list type="bullet">
    /// <item><c>Temperature/TopP/MaxOutputTokens</c> — 同一对话在不同采样参数下的响应不应共享 fixture，但实际开发场景中这些参数稳定；如需精确隔离可后续扩展</item>
    /// <item><c>Tools</c> — 工具定义通过 messages 中已存在的 FunctionCallContent/FunctionResultContent 间接影响响应；工具 schema 本身的差异不会被捕获</item>
    /// </list>
    ///
    /// 流式与非流式用不同文件后缀，避免同一请求两种调用方式互相覆盖。
    /// </summary>
    private async Task<string> ComputeFixturePathAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        bool isStreaming)
    {
        var sb = new StringBuilder();
        sb.Append(options?.ModelId ?? string.Empty);
        sb.Append('|');
        sb.Append(JsonSerializer.Serialize(messages, JsonOptions));

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var suffix = isStreaming ? ".stream" : "";
        return Path.Combine(_fixtureDir, $"{hash[..FixtureKeyHashLength]}{suffix}.json");
    }

    private static async Task<ChatResponse> LoadNonStreamingFixtureAsync(string fixturePath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(fixturePath, ct).ConfigureAwait(false);
        var fixture = JsonSerializer.Deserialize<NonStreamingFixture>(json, JsonOptions)
            ?? throw new InvalidOperationException("Corrupted VCR chat fixture.");

        var response = new ChatResponse();
        foreach (var msg in fixture.Messages)
            response.Messages.Add(msg);
        response.ResponseId = fixture.ResponseId;
        response.ModelId = fixture.ModelId;
        response.FinishReason = fixture.FinishReason;
        if (fixture.Usage is not null)
            response.Usage = fixture.Usage;
        return response;
    }

    private static async Task SaveNonStreamingFixtureAsync(
        string fixturePath, ChatResponse response, CancellationToken ct)
    {
        var fixture = new NonStreamingFixture
        {
            ResponseId = response.ResponseId,
            ModelId = response.ModelId,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            Messages = new List<ChatMessage>(response.Messages),
        };
        await WriteFixtureAsync(fixturePath, fixture, ct).ConfigureAwait(false);
    }

    private static async Task<List<ChatResponseUpdate>> LoadStreamingFixtureAsync(string fixturePath, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(fixturePath, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<ChatResponseUpdate>>(json, JsonOptions) ?? [];
    }

    private static async Task SaveStreamingFixtureAsync(
        string fixturePath, List<ChatResponseUpdate> updates, CancellationToken ct)
    {
        await WriteFixtureAsync(fixturePath, updates, ct).ConfigureAwait(false);
    }

    private static async Task WriteFixtureAsync<T>(string fixturePath, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        var json = JsonSerializer.Serialize(value, WriteOptions);
        await File.WriteAllTextAsync(fixturePath, json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 非流式 fixture wrapper。ChatResponse 继承自 List&lt;ChatMessage&gt;，
    /// 直接序列化会输出为纯 JSON 数组丢失元数据，因此用 wrapper 保存 ResponseId/ModelId/Usage 等。
    /// </summary>
    private sealed record NonStreamingFixture
    {
        public string? ResponseId { get; init; }
        public string? ModelId { get; init; }
        public ChatFinishReason? FinishReason { get; init; }
        public UsageDetails? Usage { get; init; }
        public List<ChatMessage> Messages { get; init; } = new();
    }
}
