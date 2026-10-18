using Microsoft.Extensions.AI;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using System.ClientModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// 过载/停滞重试装饰器。重试重发的只是模型 HTTP 请求，不会导致工具重复执行：
/// FunctionInvokingChatClient 在每轮**整条流结束后**才执行工具调用（M.E.AI 10.x 契约），
/// 而本层位于装配链最内层（wire 侧，见 ChatClientFactory）——重试发生时工具必然尚未执行。
/// </summary>
public sealed class RetryOnOverloadChatClient(
    IChatClient inner,
    ILogger<RetryOnOverloadChatClient>? logger = null,
    int maxRetries = 6,
    TimeSpan? streamIdleTimeout = null) : DelegatingChatClient(inner)
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(120);
    /// <summary>chunk 间空闲上限：收到过数据后连续无新 chunk 超过该时长即判定连接停滞。
    /// 需容忍推理模型两段内容之间的分钟级静默，故取值偏宽。</summary>
    private static readonly TimeSpan DefaultStreamIdleTimeout = TimeSpan.FromSeconds(180);
    private const double CircuitBreakerFailureRatio = 0.8;
    private const int CircuitBreakerMinimumThroughput = 3;
    private static readonly TimeSpan CircuitBreakerDuration = TimeSpan.FromSeconds(60);

    private readonly ILogger<RetryOnOverloadChatClient>? _logger = logger;
    private readonly int _maxRetries = maxRetries;
    private readonly TimeSpan _streamIdleTimeout = streamIdleTimeout ?? DefaultStreamIdleTimeout;

    // 弹性管道只覆盖非流式 GetResponseAsync；流式路径（交互主路径）走 StreamWithRetryAsync
    // 手写重试（退避策略共用 MaxWithBackoff），熔断器对流式不生效——流式半途熔断会
    // 丢弃已输出内容，暂不开启。
    private readonly ResiliencePipeline _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = CircuitBreakerFailureRatio,
                MinimumThroughput = CircuitBreakerMinimumThroughput,
                BreakDuration = CircuitBreakerDuration,
                ShouldHandle = static args => ValueTask.FromResult(IsRateLimitError(args.Outcome.Exception)),
                OnOpened = args =>
                {
                    logger?.LogWarning(
                        "Rate-limit circuit breaker OPENED for {Duration:g}. Cause: {Error}",
                        args.BreakDuration, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    logger?.LogInformation("Rate-limit circuit breaker CLOSED, resuming requests");
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
                ShouldHandle = static args => ValueTask.FromResult(IsTransientUpstreamError(args.Outcome.Exception)),
                OnRetry = args =>
                {
                    logger?.LogWarning(
                        "LLM rate limit / overload — attempt {Attempt}/{Max}, retrying in {Delay:g}. Error: {Error}",
                        args.AttemptNumber + 1, maxRetries,
                        args.RetryDelay,
                        args.Outcome.Exception?.Message ?? "unknown");
                    return ValueTask.CompletedTask;
                },
                DelayGenerator = args =>
                {
                    // 无 Retry-After → 返回 null，回落到 Polly 的指数退避 + jitter；
                    // 有 → max(Retry-After, 指数退避)，不因服务端过小的建议值跳过退避。
                    return ValueTask.FromResult<TimeSpan?>(
                        GetServerRetryAfter(args.Outcome.Exception, logger) is { } retryAfter
                            ? MaxWithBackoff(retryAfter, args.AttemptNumber)
                            : null);
                },
            })
            .Build();

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await _pipeline.ExecuteAsync(
            async ct => await base.GetResponseAsync(messages, options, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
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
                ChatFinishReason? finishReason = null;

                // chunk 间空闲看门狗：收到首个 chunk 之后才开始计时（首字节前不计时，
                // 避免误杀本地小模型冷加载 / 长 prompt 预处理）；连续无新 chunk 超过
                // _streamIdleTimeout 即判定连接停滞，取消本次尝试进重试。HttpClient.Timeout
                // = Infinite 下 TCP 半开/代理停滞既不结束也不抛错，空响应防线（仅流正常
                // 结束时触发）覆盖不到，没有这里就会永久挂起、TUI 永久转圈。
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var idleTimer = new Timer(
                    static state =>
                    {
                        try { ((CancellationTokenSource)state!).Cancel(); }
                        catch (ObjectDisposedException) { }
                    },
                    attemptCts, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                var source = base.GetStreamingResponseAsync(messages, options, attemptCts.Token);
                var enumerator = source.GetAsyncEnumerator(attemptCts.Token);
                var sawChunk = false;
                try
                {
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        // 首个 chunk 之后即启用 chunk 间空闲计时（首字节前不计时——
                        // 本地模型冷加载/长 prompt 预处理的分钟级静默是合法状态）。
                        sawChunk = true;
                        idleTimer.Change(_streamIdleTimeout, Timeout.InfiniteTimeSpan);

                        var current = enumerator.Current;
                        finishReason = current.FinishReason ?? finishReason;

                        var update = replayCursor is null
                            ? current
                            : FilterReplay(current, replayCursor);

                        if (update is null)
                            continue;

                        replayState.Record(update);
                        await writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && sawChunk)
                {
                    // 空闲看门狗触发的尝试级取消（外部取消由 when 排除，原样向上传播）。
                    if (attempt >= _maxRetries)
                    {
                        _logger?.LogWarning(
                            "LLM streaming stalled — no chunks for {Idle:g} — giving up after {Attempts} attempts. Replaying {TextChars} chars and {ToolCalls} tool calls.",
                            _streamIdleTimeout,
                            attempt + 1,
                            replayState.EmittedTextLength,
                            replayState.EmittedToolCallCount);
                        throw new StreamStalledException(
                            $"模型流式响应停滞：{_streamIdleTimeout.TotalSeconds:N0}s 内无任何数据（已重试 {attempt + 1} 次）。");
                    }

                    retryDelay = MaxWithBackoff(retryAfter: null, attempt);
                    _logger?.LogWarning(
                        "LLM streaming stalled — no chunks for {Idle:g} (attempt {Attempt}/{Max}), retrying in {Delay:g}. Replaying {TextChars} chars and {ToolCalls} tool calls.",
                        _streamIdleTimeout,
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
                catch (Exception ex) when (attempt < _maxRetries && IsTransientUpstreamError(ex))
                {
                    retryDelay = GetRetryDelay(ex, attempt);
                    _logger?.LogWarning(
                        "LLM transient upstream error during streaming (rate limit / overload / empty choices) — attempt {Attempt}/{Max}, retrying in {Delay:g}. Replaying {TextChars} chars and {ToolCalls} tool calls.",
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
                {
                    if (replayState.HasVisibleContent)
                        return;

                    // 流式路径的空响应防线：HTTP 200 + 零内容 SSE（无文本、无工具调用、无推理，
                    // 仅有 usage 统计甚至空流直接结束）。OpenRouter 免费/实验模型过载或内容
                    // 过滤时的典型表现，与非流式路径的 EmptyChoicesResponseException 同源——
                    // 那条防线只覆盖 JSON 响应，SSE 流正常结束不会抛异常，此前会一路静默到
                    // TUI（运行按 Completed 收尾、零输出零提示）。已有内容后正常结束不重试
                    // （内容已渲染，重试无法撤回）。
                    if (attempt >= _maxRetries)
                    {
                        _logger?.LogWarning(
                            "LLM streaming response completed with zero content after {Attempts} attempts (last finish reason: {FinishReason}) — giving up",
                            attempt + 1,
                            finishReason?.ToString() ?? "none");
                        throw new EmptyChoicesResponseException(
                            $"模型连续 {attempt + 1} 次返回空响应（HTTP 200，无文本、无工具调用）。可能是上游过载或该请求被内容过滤：可稍后重试、切换模型（/model）或压缩上下文（/compact）。");
                    }

                    retryDelay = EmptyStreamRetryDelay(attempt);
                    _logger?.LogWarning(
                        "LLM streaming response completed with zero content (finish reason: {FinishReason}) — attempt {Attempt}/{Max}, retrying in {Delay:g}",
                        finishReason?.ToString() ?? "none",
                        attempt + 1,
                        _maxRetries,
                        retryDelay);
                }

                await Task.Delay(retryDelay.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static bool IsRateLimitError(Exception? ex) =>
        ex switch
        {
            HttpRequestException hre => hre.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable
                || (int?)hre.StatusCode == 529,
            ClientResultException cre => cre.Status is 429 or 503 or 529,
            _ => false,
        };

    /// <summary>
    /// 可重试的瞬时上游错误 = 限流/过载 + "200 但空 choices" + "200 但错误体且判定为瞬时"
    /// （OpenRouter 免费模型过载时返回空 choices 或 {"error":{...}} 错误体，
    /// 由 <see cref="OpenAiResponseSanitizingHandler"/> 分别转为
    /// <see cref="EmptyChoicesResponseException"/> 与
    /// <see cref="UpstreamProviderErrorException"/>；后者仅瞬时错误重试，
    /// 401/400 等永久错误立即失败）。熔断器只统计限流错误，
    /// 避免免费模型的偶发空响应把熔断器打开。
    /// </summary>
    private static bool IsTransientUpstreamError(Exception? ex) =>
        ex is EmptyChoicesResponseException
        || (ex is UpstreamProviderErrorException { IsTransient: true })
        || IsRateLimitError(ex);

    /// <summary>取服务端 Retry-After：掩码错误体异常由 Handler 从响应头读出挂载，
    /// ClientResultException 走原始响应头。</summary>
    private static TimeSpan? GetServerRetryAfter(Exception? ex, ILogger<RetryOnOverloadChatClient>? logger)
    {
        switch (ex)
        {
            case UpstreamProviderErrorException { RetryAfter: { } retryAfter }:
                return retryAfter;

            case ClientResultException cre:
                try
                {
                    var response = cre.GetRawResponse();
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
                    logger?.LogDebug(ex2, "Failed to parse Retry-After header from rate-limit response");
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// 统一退避策略：指数退避（4s 基数、120s 上限）为下限，服务端 Retry-After 与其取
    /// 最大值。流式与非流式两条重试路径共用，防止两份手写退避漂移。
    /// </summary>
    private static TimeSpan MaxWithBackoff(TimeSpan? retryAfter, int attempt)
    {
        var exponentialSeconds = Math.Min(4.0 * Math.Pow(2, attempt), 120.0);
        return TimeSpan.FromSeconds(Math.Max(retryAfter?.TotalSeconds ?? 0, exponentialSeconds));
    }

    /// <summary>
    /// 空流重试退避（3s 起、10s 封顶）：空响应与限流不同，大概率是该请求的确定性失败
    /// （内容过滤 / 上游对特定上下文返回坏结果），重试只是给瞬态过载留机会——套用限流的
    /// 120s 上限会让用户在最坏情况下干等约 4 分钟才见到报错，这里把最坏总等待压到约 50s。
    /// </summary>
    private static TimeSpan EmptyStreamRetryDelay(int attempt)
        => TimeSpan.FromSeconds(Math.Min(3.0 * Math.Pow(2, attempt), 10.0));

    private TimeSpan GetRetryDelay(Exception ex, int attempt) =>
        MaxWithBackoff(GetServerRetryAfter(ex, _logger), attempt);

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
        private bool _hasVisibleContent;

        public bool HasHistory => _segments.Count > 0;

        /// <summary>
        /// 本轮请求（跨全部重试尝试累计）是否产出过任何可见内容（文本/工具调用/推理/多模态）。
        /// 仅含 usage 统计的空流不算——那是 OpenRouter 免费/实验模型"200 但空"的典型表现。
        /// </summary>
        public bool HasVisibleContent => _hasVisibleContent;

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
                        _hasVisibleContent = true;
                        break;

                    case FunctionCallContent functionCall:
                        _segments.Add(ReplaySegment.Tool(BuildToolCallKey(functionCall)));
                        EmittedToolCallCount++;
                        _hasVisibleContent = true;
                        break;

                    case TextContent: // 空文本占位块
                    case UsageContent: // token 统计
                        break;

                    default:
                        // TextReasoningContent / DataContent 等真实内容
                        _hasVisibleContent = true;
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
