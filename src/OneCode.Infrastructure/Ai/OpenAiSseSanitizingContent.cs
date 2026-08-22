using System.Buffers;
using System.Net;

namespace OneCode.Infrastructure.Ai;

/// <summary>
/// Wraps an SSE response so each <c>data:</c> line is sanitized before the OpenAI SDK
/// deserializes it. Overrides <see cref="HttpContent.CreateContentReadStreamAsync()"/>
/// to avoid buffering the entire stream into a <see cref="MemoryStream"/>.
/// </summary>
internal sealed class OpenAiSseSanitizingContent : HttpContent
{
    // 4KB — typical chat SSE events are far smaller; leftover incomplete lines stay in a MemoryStream.
    private const int ReadBufferSize = 4096;

    private readonly HttpContent _inner;

    /// <summary>
    /// Copies content headers from the original SSE response so Content-Type stays <c>text/event-stream</c>.
    /// </summary>
    public OpenAiSseSanitizingContent(HttpContent inner)
    {
        _inner = inner;
        foreach (var header in inner.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        CreateSanitizedStreamAsync(CancellationToken.None);

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        CreateSanitizedStreamAsync(cancellationToken);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var sanitized = await CreateSanitizedStreamAsync(cancellationToken).ConfigureAwait(false);
        await sanitized.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    private async Task<Stream> CreateSanitizedStreamAsync(CancellationToken cancellationToken)
    {
        var innerStream = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new OpenAiSseSanitizingStream(innerStream);
    }

    /// <summary>
    /// Line-oriented transform over UTF-8 SSE bytes. Splitting on <c>0x0A</c> is safe
    /// for UTF-8 because newline cannot appear inside a multibyte code unit.
    /// </summary>
    private sealed class OpenAiSseSanitizingStream : Stream
    {
        private readonly Stream _inner;
        private readonly MemoryStream _incomplete = new();
        private byte[] _pending = [];
        private int _pendingPos;
        private bool _completed;

        public OpenAiSseSanitizingStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            var destination = buffer.AsSpan(offset, count);
            var copied = CopyPending(destination);
            if (copied > 0 || _completed)
                return copied;

            var rented = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
            try
            {
                while (true)
                {
                    var read = _inner.Read(rented, 0, ReadBufferSize);
                    if (ProcessReadBytes(rented.AsSpan(0, read)))
                        return CopyPending(destination);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copied = CopyPending(buffer.Span);
            if (copied > 0 || _completed)
                return copied;

            var rented = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
            try
            {
                while (true)
                {
                    var read = await _inner.ReadAsync(rented.AsMemory(0, ReadBufferSize), cancellationToken)
                        .ConfigureAwait(false);
                    if (ProcessReadBytes(rented.AsSpan(0, read)))
                        return CopyPending(buffer.Span);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _incomplete.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <returns><see langword="true"/> when the consumer should stop reading from the inner stream.</returns>
        private bool ProcessReadBytes(ReadOnlySpan<byte> read)
        {
            if (read.Length == 0)
            {
                _completed = true;
                TryFlushCompleteLines(flushTail: true);
                return true;
            }

            _incomplete.Write(read);
            return TryFlushCompleteLines(flushTail: false);
        }

        private int CopyPending(Span<byte> destination)
        {
            if (_pendingPos >= _pending.Length || destination.Length == 0)
                return 0;

            var n = Math.Min(destination.Length, _pending.Length - _pendingPos);
            _pending.AsSpan(_pendingPos, n).CopyTo(destination);
            _pendingPos += n;
            return n;
        }

        private bool TryFlushCompleteLines(bool flushTail)
        {
            var span = _incomplete.GetBuffer().AsSpan(0, (int)_incomplete.Length);
            var consumed = 0;
            using var output = new MemoryStream();

            while (true)
            {
                var remaining = span[consumed..];
                var nl = remaining.IndexOf((byte)'\n');
                if (nl < 0)
                    break;

                EmitSanitizedLine(output, remaining[..nl], addNewline: true);
                consumed += nl + 1;
            }

            if (flushTail && consumed < span.Length)
            {
                EmitSanitizedLine(output, span[consumed..], addNewline: false);
                consumed = span.Length;
            }

            if (consumed > 0)
            {
                var leftover = span[consumed..];
                _incomplete.SetLength(0);
                _incomplete.Write(leftover);
            }

            if (output.Length == 0)
                return false;

            _pending = output.ToArray();
            _pendingPos = 0;
            return true;
        }

        private static void EmitSanitizedLine(MemoryStream output, ReadOnlySpan<byte> lineBytes, bool addNewline)
        {
            if (lineBytes is [.., (byte)'\r'])
                lineBytes = lineBytes[..^1];

            var line = Encoding.UTF8.GetString(lineBytes);
            var sanitized = OpenAiResponseSanitizer.SanitizeSseLine(line);
            var encoded = Encoding.UTF8.GetBytes(sanitized);
            output.Write(encoded);
            if (addNewline)
                output.WriteByte((byte)'\n');
        }
    }
}
