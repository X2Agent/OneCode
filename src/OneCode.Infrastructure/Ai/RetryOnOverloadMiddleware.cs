using Microsoft.Extensions.AI;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OneCode.Infrastructure.Ai;

public sealed class RetryOnOverloadChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<RetryOnOverloadChatClient>? _logger;
    private readonly int _maxRetries;

    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(120);
    private const double CircuitBreakerFailureRatio = 0.8;
    private const int CircuitBreakerMinimumThroughput = 3;
    private static readonly TimeSpan CircuitBreakerDuration = TimeSpan.FromSeconds(60);

    public RetryOnOverloadChatClient(
        IChatClient inner,
        ILogger<RetryOnOverloadChatClient>? logger = null,
        int maxRetries = 6)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger;
        _maxRetries = maxRetries;

        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = CircuitBreakerFailureRatio,
                MinimumThroughput = CircuitBreakerMinimumThroughput,
                BreakDuration = CircuitBreakerDuration,
                ShouldHandle = static args => ValueTask.FromResult(IsRateLimitError(args.Outcome.Exception)),
                OnOpened = args =>
                {
                    _logger?.LogWarning(
                        "Rate-limit circuit breaker OPENED for {Duration:g}. Cause: {Error}",
                        args.BreakDuration, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    _logger?.LogInformation("Rate-limit circuit breaker CLOSED, resuming requests");
                    return ValueTask.CompletedTask;
                },
            })
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = DefaultRetryDelay,
                MaxDelay = MaxRetryDelay,
                ShouldHandle = static args => ValueTask.FromResult(IsRateLimitError(args.Outcome.Exception)),
                OnRetry = args =>
                {
                    _logger?.LogWarning(
                        "LLM rate limit / overload — attempt {Attempt}/{Max}, retrying in {Delay:g}. Error: {Error}",
                        args.AttemptNumber + 1, maxRetries,
                        args.RetryDelay,
                        args.Outcome.Exception?.Message ?? "unknown");
                    return ValueTask.CompletedTask;
                },
                DelayGenerator = args =>
                {
                    // 计算指数退避延迟（作为下限保证）
                    var attempt = args.AttemptNumber;
                    var exponentialSeconds = Math.Min(
                        4.0 * Math.Pow(2, attempt),
                        120.0);

                    if (args.Outcome.Exception is ClientResultException cre)
                    {
                        var retryAfter = GetRetryAfterDelay(cre);
                        if (retryAfter.HasValue)
                        {
                            // 取 Retry-After 与指数退避的最大值，确保不因服务端过小的建议值跳过退避
                            var delay = TimeSpan.FromSeconds(Math.Max(retryAfter.Value.TotalSeconds, exponentialSeconds));
                            return ValueTask.FromResult<TimeSpan?>(delay);
                        }
                    }
                    return ValueTask.FromResult<TimeSpan?>(null);
                },
            })
            .Build();
    }

    public void Dispose() => _inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await _pipeline.ExecuteAsync(
            async ct => await _inner.GetResponseAsync(messages, options, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        var writerTask = StreamWithRetryAsync(messages, options, channel.Writer, cancellationToken);

        var readerCompleted = false;
        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
                yield return update;
            readerCompleted = true;
        }
        finally
        {
            // Ensure writerTask is always observed to prevent unobserved task exceptions
            // when ReadAllAsync throws (e.g. on cancellation).
            if (readerCompleted)
                await writerTask.ConfigureAwait(false);
            else
            {
                try { await writerTask.ConfigureAwait(false); }
                catch { /* suppress to preserve the original exception */ }
            }
        }
    }

    private async Task StreamWithRetryAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        ChannelWriter<ChatResponseUpdate> writer,
        CancellationToken cancellationToken)
    {
        var replayState = new StreamingReplayState();

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var replayCursor = attempt == 0 || !replayState.HasHistory
                    ? null
                    : replayState.CreateCursor();
                TimeSpan? retryDelay = null;

                var source = _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
                var enumerator = source.GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        var update = replayCursor is null
                            ? enumerator.Current
                            : FilterReplay(enumerator.Current, replayCursor);

                        if (update is null)
                            continue;

                        replayState.Record(update);
                        await writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (attempt < _maxRetries && IsRateLimitError(ex))
                {
                    retryDelay = GetRetryDelay(ex, attempt);
                    _logger?.LogWarning(
                        "LLM rate limit / overload during streaming — attempt {Attempt}/{Max}, retrying in {Delay:g}. Replaying {TextChars} chars and {ToolCalls} tool calls.",
                        attempt + 1,
                        _maxRetries,
                        retryDelay,
                        replayState.EmittedTextLength,
                        replayState.EmittedToolCallCount);

                    if (replayCursor?.DidDiverge == true)
                    {
                        _logger?.LogWarning(
                            "Streaming retry replay diverged after matching {MatchedChars} chars and {MatchedToolCalls} tool calls; duplicate suppression switched to best effort.",
                            replayCursor.MatchedTextChars,
                            replayCursor.MatchedToolCalls);
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                if (!retryDelay.HasValue)
                    return;

                await Task.Delay(retryDelay.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.GetService(serviceType, serviceKey);

    private static bool IsRateLimitError(Exception? ex) =>
        ex switch
        {
            HttpRequestException hre => hre.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable
                || (int?)hre.StatusCode == 529,
            ClientResultException cre => cre.Status is 429 or 503 or 529,
            _ => false,
        };

    private TimeSpan? GetRetryAfterDelay(ClientResultException ex)
    {
        try
        {
            var response = ex.GetRawResponse();
            if (response is null) return null;
            foreach (var header in response.Headers)
            {
                if (header.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(header.Value, out var seconds))
                {
                    return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120));
                }
            }
        }
        catch (Exception ex2)
        {
            _logger?.LogDebug(ex2, "Failed to parse Retry-After header from rate-limit response");
        }
        return null;
    }

    private TimeSpan GetRetryDelay(Exception ex, int attempt)
    {
        var exponentialSeconds = Math.Min(
            4.0 * Math.Pow(2, attempt),
            120.0);

        if (ex is ClientResultException cre)
        {
            var retryAfter = GetRetryAfterDelay(cre);
            if (retryAfter.HasValue)
            {
                return TimeSpan.FromSeconds(Math.Max(retryAfter.Value.TotalSeconds, exponentialSeconds));
            }
        }

        return TimeSpan.FromSeconds(exponentialSeconds);
    }

    private static ChatResponseUpdate? FilterReplay(ChatResponseUpdate update, StreamingReplayCursor replayCursor)
    {
        List<AIContent>? filteredContents = null;

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent textContent:
                    {
                        var remainingText = replayCursor.ConsumeText(textContent.Text);
                        if (!string.IsNullOrEmpty(remainingText))
                        {
                            filteredContents ??= new List<AIContent>(update.Contents.Count);
                            filteredContents.Add(new TextContent(remainingText));
                        }
                        break;
                    }

                case FunctionCallContent functionCall when replayCursor.ShouldSkip(functionCall):
                    break;

                default:
                    if (!replayCursor.HasPendingReplay)
                    {
                        filteredContents ??= new List<AIContent>(update.Contents.Count);
                        filteredContents.Add(content);
                    }
                    break;
            }
        }

        if (filteredContents is null && !ShouldEmitMetadataOnly(update, replayCursor))
        {
            return null;
        }

        return CloneUpdate(update, filteredContents ?? new List<AIContent>());
    }

    private static bool ShouldEmitMetadataOnly(ChatResponseUpdate update, StreamingReplayCursor replayCursor)
        => !replayCursor.HasPendingReplay && update.FinishReason.HasValue;

    private static ChatResponseUpdate CloneUpdate(ChatResponseUpdate update, IList<AIContent> contents)
        => new(update.Role, contents)
        {
            AuthorName = update.AuthorName,
            RawRepresentation = update.RawRepresentation,
            AdditionalProperties = update.AdditionalProperties,
            ResponseId = update.ResponseId,
            MessageId = update.MessageId,
            ConversationId = update.ConversationId,
            CreatedAt = update.CreatedAt,
            FinishReason = update.FinishReason,
            ModelId = update.ModelId,
        };

    private static string BuildToolCallKey(FunctionCallContent functionCall)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{functionCall.Name}:{JsonSerializer.Serialize(functionCall.Arguments)}");

    private sealed class StreamingReplayState
    {
        private readonly List<ReplaySegment> _segments = [];

        public bool HasHistory => _segments.Count > 0;

        public int EmittedTextLength { get; private set; }

        public int EmittedToolCallCount { get; private set; }

        public void Record(ChatResponseUpdate update)
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } textContent:
                        AppendText(textContent.Text);
                        break;

                    case FunctionCallContent functionCall:
                        _segments.Add(ReplaySegment.Tool(BuildToolCallKey(functionCall)));
                        EmittedToolCallCount++;
                        break;
                }
            }
        }

        public StreamingReplayCursor CreateCursor() => new(_segments);

        private void AppendText(string text)
        {
            EmittedTextLength += text.Length;

            if (_segments.Count > 0 && _segments[^1].Kind == ReplaySegmentKind.Text)
            {
                _segments[^1] = _segments[^1] with { Value = _segments[^1].Value + text };
                return;
            }

            _segments.Add(ReplaySegment.Text(text));
        }
    }

    private sealed class StreamingReplayCursor(IReadOnlyList<ReplaySegment> segments)
    {
        private readonly IReadOnlyList<ReplaySegment> _segments = segments;
        private int _segmentIndex;
        private int _textOffset;

        public bool HasPendingReplay => _segmentIndex < _segments.Count;

        public bool DidDiverge { get; private set; }

        public int MatchedTextChars { get; private set; }

        public int MatchedToolCalls { get; private set; }

        public string ConsumeText(string text)
        {
            if (string.IsNullOrEmpty(text) || !HasPendingReplay)
            {
                return text;
            }

            var remaining = text;
            while (remaining.Length > 0 && HasPendingReplay)
            {
                var segment = _segments[_segmentIndex];
                if (segment.Kind != ReplaySegmentKind.Text)
                {
                    return MarkDivergedAndReturn(remaining);
                }

                var expected = segment.Value.AsSpan(_textOffset);
                var matched = GetCommonPrefixLength(remaining.AsSpan(), expected);
                MatchedTextChars += matched;

                if (matched == 0)
                {
                    return MarkDivergedAndReturn(remaining);
                }

                AdvanceTextSegment(matched);

                if (matched == remaining.Length)
                {
                    return string.Empty;
                }

                if (matched < expected.Length)
                {
                    return MarkDivergedAndReturn(remaining.Substring(matched));
                }

                remaining = remaining.Substring(matched);
            }

            return remaining;
        }

        public bool ShouldSkip(FunctionCallContent functionCall)
        {
            if (!HasPendingReplay)
            {
                return false;
            }

            var segment = _segments[_segmentIndex];
            if (segment.Kind != ReplaySegmentKind.ToolCall)
            {
                MarkDiverged();
                return false;
            }

            if (!string.Equals(segment.Value, BuildToolCallKey(functionCall), StringComparison.Ordinal))
            {
                MarkDiverged();
                return false;
            }

            MatchedToolCalls++;
            _segmentIndex++;
            _textOffset = 0;
            return true;
        }

        private void AdvanceTextSegment(int matched)
        {
            _textOffset += matched;
            while (HasPendingReplay
                && _segments[_segmentIndex].Kind == ReplaySegmentKind.Text
                && _textOffset >= _segments[_segmentIndex].Value.Length)
            {
                _segmentIndex++;
                _textOffset = 0;
            }
        }

        private static int GetCommonPrefixLength(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
        {
            var length = Math.Min(left.Length, right.Length);
            var matched = 0;
            while (matched < length && left[matched] == right[matched])
            {
                matched++;
            }

            return matched;
        }

        private string MarkDivergedAndReturn(string remaining)
        {
            MarkDiverged();
            return remaining;
        }

        private void MarkDiverged()
        {
            DidDiverge = true;
            _segmentIndex = _segments.Count;
            _textOffset = 0;
        }
    }

    private enum ReplaySegmentKind
    {
        Text,
        ToolCall,
    }

    private sealed record ReplaySegment(ReplaySegmentKind Kind, string Value)
    {
        public static ReplaySegment Text(string value) => new(ReplaySegmentKind.Text, value);

        public static ReplaySegment Tool(string value) => new(ReplaySegmentKind.ToolCall, value);
    }
}
