using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OneCode.Infrastructure.Mcp;

/// <summary>
/// InProcess MCP 服务器扩展点：宿主/测试通过 <see cref="IInProcessMcpServerProvider"/>
/// 把同进程 MCP 服务器（<c>ModelContextProtocol.Server.McpServer</c>）暴露给
/// McpConnectionManager 的 InProcess 分支，客户端经 <see cref="InProcessMcpTransportPair"/>
/// 的内存 Channel 直连——不经过 stdio 子进程或任何网络栈。
/// </summary>
public interface IInProcessMcpServerProvider
{
    /// <summary>
    /// 尝试为指定服务器名在同进程内启动 MCP 服务器会话。返回 false 表示该名称
    /// 不受此 provider 支持（多个 provider 按注册顺序尝试，首个命中生效）。
    /// </summary>
    /// <remarks>
    /// 实现方负责用 <paramref name="serverTransport"/> 调
    /// <c>ModelContextProtocol.Server.McpServer.CreateAsync(transport, options)</c>
    /// 启动会话（返回即视为就绪，无需等待客户端 initialize）。每个连接调用应创建
    /// 新的 server 会话，避免跨连接串扰消息。
    /// </remarks>
    bool TryCreateServer(string serverName, ITransport serverTransport);
}

/// <summary>
/// 同进程 MCP 服务器注册表（provider 集合）。按服务器名顺序尝试各 provider，
/// 未命中时连接侧保持软失败（跳过 + 警告日志），与无效配置行为一致。
/// </summary>
public sealed class InProcessMcpServerRegistry
{
    private readonly IInProcessMcpServerProvider[] _providers;

    public InProcessMcpServerRegistry(IEnumerable<IInProcessMcpServerProvider> providers)
        => _providers = [.. providers];

    public InProcessMcpServerRegistry()
        : this([]) { }

    /// <summary>
    /// 为指定服务器创建内存 transport 对，并逐个尝试 provider 启动同进程 server。
    /// 无 provider 接管时返回 null（连接侧按未注册软失败）。
    /// </summary>
    public InProcessMcpTransportPair? TryCreatePair(string serverName)
    {
        var pair = new InProcessMcpTransportPair(serverName);
        foreach (var provider in _providers)
        {
            if (provider.TryCreateServer(serverName, pair.Server))
                return pair;
        }

        return null;
    }
}

/// <summary>
/// 一对经内存 Channel 互联的 MCP transport：client 侧实现
/// <see cref="IClientTransport"/>（供 <see cref="McpClient"/> 连接），server 侧实现
/// <see cref="ITransport"/>（供同进程 <c>McpServer.CreateAsync</c> 挂载）。
/// 消息对象直接跨 Channel 传递，零序列化开销；任一侧 Dispose 即关闭整条链路。
/// </summary>
public sealed class InProcessMcpTransportPair : IAsyncDisposable
{
    private readonly Channel<JsonRpcMessage> _clientToServer =
        Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<JsonRpcMessage> _serverToClient =
        Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true });
    private bool _disposed;

    internal InProcessMcpTransportPair(string name)
    {
        Client = new InProcessClientTransport(name, this);
        Server = new InProcessServerTransport(name, this);
    }

    /// <summary>client 侧 transport（传给 <see cref="McpClient.ConnectAsync"/>）。</summary>
    public InProcessClientTransport Client { get; }

    /// <summary>server 侧 transport（传给同进程 <c>McpServer.CreateAsync</c>）。</summary>
    public InProcessServerTransport Server { get; }

    internal async ValueTask SendToServerAsync(JsonRpcMessage message, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _clientToServer.Writer.WriteAsync(message, ct).ConfigureAwait(false);
    }

    internal async ValueTask SendToClientAsync(JsonRpcMessage message, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _serverToClient.Writer.WriteAsync(message, ct).ConfigureAwait(false);
    }

    internal ChannelReader<JsonRpcMessage> ClientReader => _serverToClient.Reader;

    internal ChannelReader<JsonRpcMessage> ServerReader => _clientToServer.Reader;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _clientToServer.Writer.TryComplete();
        _serverToClient.Writer.TryComplete();
    }
}

/// <summary>client 侧内存 transport：<see cref="IClientTransport"/> + <see cref="ITransport"/>，与 <see cref="WebSocketClientTransport"/> 同构。</summary>
public sealed class InProcessClientTransport(string name, InProcessMcpTransportPair pair)
    : IClientTransport, ITransport
{
    public string Name => $"inprocess:{name}";

    public string? SessionId => null;

    public ChannelReader<JsonRpcMessage> MessageReader => pair.ClientReader;

    public Task<ITransport> ConnectAsync(CancellationToken ct = default)
        => Task.FromResult<ITransport>(this);

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken ct = default)
        => pair.SendToServerAsync(message, ct).AsTask();

    public ValueTask DisposeAsync() => pair.DisposeAsync();
}

/// <summary>server 侧内存 transport：把同进程 <c>McpServer</c> 的出站响应转给 client 侧入站 Channel。</summary>
public sealed class InProcessServerTransport(string name, InProcessMcpTransportPair pair)
    : ITransport
{
    public string Name => $"inprocess:{name}";

    public string? SessionId => null;

    public ChannelReader<JsonRpcMessage> MessageReader => pair.ServerReader;

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken ct = default)
        => pair.SendToClientAsync(message, ct).AsTask();

    public ValueTask DisposeAsync() => pair.DisposeAsync();
}
