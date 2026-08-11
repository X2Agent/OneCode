using System.Net.WebSockets;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OneCode.Infrastructure.Mcp;

public sealed class WebSocketClientTransport : IClientTransport, ITransport, IAsyncDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _url;
    private readonly ILogger? _logger;
    private readonly Channel<JsonRpcMessage> _messageChannel = Channel.CreateUnbounded<JsonRpcMessage>();
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private bool _disposed;

    public string Name => $"ws:{_url}";
    public string? SessionId { get; private set; }
    public ChannelReader<JsonRpcMessage> MessageReader => _messageChannel.Reader;

    public WebSocketClientTransport(string url, ILogger? logger = null)
    {
        _url = url;
        _logger = logger;
    }

    public async Task<ITransport> ConnectAsync(CancellationToken ct = default)
    {
        _webSocket = new ClientWebSocket();

        _logger?.LogInformation("Connecting to MCP WebSocket: {Url}", _url);

        await _webSocket.ConnectAsync(new Uri(_url), ct).ConfigureAwait(false);

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);

        _logger?.LogInformation("WebSocket connected to {Url}", _url);

        return this;
    }

    public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken ct = default)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected");

        var json = JsonSerializer.Serialize(message, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger?.LogInformation("WebSocket closed by server");
                    _messageChannel.Writer.TryComplete();
                    break;
                }

                var messageJson = Encoding.UTF8.GetString(ms.ToArray());
                if (string.IsNullOrWhiteSpace(messageJson)) continue;

                try
                {
                    var message = JsonSerializer.Deserialize<JsonRpcMessage>(messageJson, _jsonOptions);
                    if (message != null)
                        await _messageChannel.Writer.WriteAsync(message, ct).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    _logger?.LogWarning(ex, "Failed to parse WebSocket message");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _messageChannel.Writer.TryComplete();
        }
        catch (WebSocketException ex)
        {
            _logger?.LogWarning(ex, "WebSocket read error");
            _messageChannel.Writer.TryComplete(ex);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error in WebSocket read loop");
            _messageChannel.Writer.TryComplete(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _messageChannel.Writer.TryComplete();
        _readCts?.Cancel();

        if (_readLoop != null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* teardown */ }
        }

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "WebSocketClientTransport teardown close failed"); }
        }

        _webSocket?.Dispose();
        _readCts?.Dispose();
    }
}
