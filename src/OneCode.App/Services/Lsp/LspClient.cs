using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Lsp;

/// <summary>
/// LSP (Language Server Protocol) client implementation.
/// Uses JSON-RPC over stdio to communicate with LSP servers.
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly string _serverName;
    private readonly ILogger<LspClient> _logger;
    private readonly Action<Exception>? _onCrash;
    private Process? _process;
    private bool _isInitialized;
    private bool _isStopping;
    private bool _startFailed;
    private Exception? _startError;
    private readonly List<PendingNotificationHandler> _pendingNotificationHandlers = new();
    private readonly List<PendingRequestHandler> _pendingRequestHandlers = new();
    private readonly Dictionary<string, Func<JsonElement, Task<JsonElement>>> _requestHandlers = new();
    private readonly Dictionary<string, Action<JsonElement>> _notificationHandlers = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private Task? _readLoopTask;
    private readonly ConcurrentDictionary<string, Task> _outstandingServerRequests = new();

    // Cloned JsonElements outlive their parent JsonDocument, so these are safe to
    // reuse as default parameter values without disposing.
    private static readonly JsonElement EmptyObject = CreateEmptyObject();
    private static readonly JsonElement EmptyNull = CreateEmptyNull();

    private static JsonElement CreateEmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static JsonElement CreateEmptyNull()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Server capabilities received after initialization.
    /// </summary>
    public JsonElement? Capabilities { get; private set; }

    /// <summary>
    /// Whether the LSP server has been successfully initialized.
    /// </summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Whether the client failed to start.
    /// </summary>
    public bool StartFailed => _startFailed;

    /// <summary>
    /// The error that occurred during start, if any.
    /// </summary>
    public Exception? StartError => _startError;

    public LspClient(string serverName, ILogger<LspClient> logger, Action<Exception>? onCrash = null)
    {
        _serverName = serverName;
        _logger = logger;
        _onCrash = onCrash;
    }

    /// <summary>
    /// Start the LSP server process.
    /// </summary>
    public async Task StartAsync(string command, string[] args, Dictionary<string, string>? env = null, string? cwd = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            if (env != null)
            {
                foreach (var (key, value) in env)
                    psi.Environment[key] = value;
            }

            if (!string.IsNullOrEmpty(cwd))
                psi.WorkingDirectory = cwd;

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start LSP server: {command}");

            // Capture stderr for diagnostics
            _process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogDebug("[LSP SERVER {ServerName}] {Output}", _serverName, e.Data);
            };
            _process.BeginErrorReadLine();

            _process.Exited += OnProcessExited;
            _process.EnableRaisingEvents = true;

            _readLoopTask = Task.Run(() => ReadMessageLoopAsync(), CancellationToken.None);

            _logger.LogDebug("LSP client started for {ServerName}", _serverName);
        }
        catch (Exception ex)
        {
            _startFailed = true;
            _startError = ex;
            _logger.LogError(ex, "LSP server {ServerName} failed to start: {Message}", _serverName, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Initialize the LSP server with the given parameters.
    /// </summary>
    public async Task<JsonElement> InitializeAsync(JsonElement initializeParams)
    {
        if (_process == null || _process.StandardInput.BaseStream == null)
            throw new InvalidOperationException("LSP client not started");

        if (_startFailed)
            throw new InvalidOperationException($"LSP server {_serverName} failed to start", _startError);

        try
        {
            var result = await SendRequestAsync("initialize", initializeParams).ConfigureAwait(false);

            if (result.TryGetProperty("capabilities", out var caps))
                Capabilities = caps;

            await SendNotificationAsync("initialized", EmptyObject).ConfigureAwait(false);

            _isInitialized = true;

            foreach (var handler in _pendingNotificationHandlers)
                RegisterNotificationHandler(handler.Method, handler.Handler);
            _pendingNotificationHandlers.Clear();

            foreach (var handler in _pendingRequestHandlers)
                RegisterRequestHandler(handler.Method, handler.Handler);
            _pendingRequestHandlers.Clear();

            _logger.LogDebug("LSP server {ServerName} initialized", _serverName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LSP server {ServerName} initialize failed: {Message}", _serverName, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Send a request to the LSP server and wait for response.
    /// When <paramref name="ct"/> is cancelled, a <c>$/cancelRequest</c> notification is sent
    /// (best-effort) and the awaiter receives <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<JsonElement> SendRequestAsync(string method, JsonElement parameters, CancellationToken ct = default)
    {
        if (_process == null)
            throw new InvalidOperationException("LSP client not started");

        if (_startFailed)
            throw new InvalidOperationException($"LSP server {_serverName} failed to start", _startError);

        // The "initialize" request is the LSP lifecycle method that performs initialization itself —
        // it MUST be sent before _isInitialized becomes true. Exempting it avoids a chicken-and-egg
        // deadlock where InitializeAsync() calls SendRequestAsync("initialize", ...) but the guard
        // here rejects the call because initialization hasn't happened yet.
        if (!_isInitialized && method != "initialize")
            throw new InvalidOperationException("LSP server not initialized");

        var requestId = Guid.NewGuid().ToString("N");
        var request = new
        {
            jsonrpc = "2.0",
            id = requestId,
            method,
            @params = parameters
        };

        var tcs = new TaskCompletionSource<JsonElement>();
        lock (_pendingRequests)
            _pendingRequests[requestId] = tcs;

        // Register caller cancellation: send $/cancelRequest (best-effort) and cancel the TCS
        // so the awaiter receives OperationCanceledException instead of waiting for a response
        // that may never arrive.
        CancellationTokenRegistration reg = default;
        if (ct.CanBeCanceled)
        {
            reg = ct.Register(() =>
            {
                try
                {
                    if (_process != null)
                    {
                        var cancelParams = JsonSerializer.SerializeToElement(new { id = requestId });
                        _ = SendNotificationAsync("$/cancelRequest", cancelParams);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to send $/cancelRequest for {Method}", method);
                }
                lock (_pendingRequests)
                    _pendingRequests.Remove(requestId);
                tcs.TrySetCanceled(ct);
            });
        }

        try
        {
            await SendJsonRpcMessageAsync(request).ConfigureAwait(false);

            // Initialize can take much longer than other requests (loading MSBuild SDK,
            // Roslyn workspace, etc.). Use a longer timeout for initialize.
            var timeoutSec = method == "initialize"
                ? Constants.Lsp.InitializeTimeoutSec
                : Constants.Lsp.RequestTimeoutSec;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — not caller cancellation
                lock (_pendingRequests)
                    _pendingRequests.Remove(requestId);
                throw new TimeoutException($"LSP request '{method}' timed out after {timeoutSec}s");
            }
        }
        finally
        {
            reg.Dispose();
        }
    }

    /// <summary>
    /// Send a notification to the LSP server (fire-and-forget).
    /// </summary>
    public async Task SendNotificationAsync(string method, JsonElement parameters)
    {
        if (_process == null)
            throw new InvalidOperationException("LSP client not started");

        var notification = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        };

        try
        {
            await SendJsonRpcMessageAsync(notification).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notification {Method} failed but continuing", method);
        }
    }

    /// <summary>
    /// Register a handler for incoming notifications.
    /// </summary>
    public void OnNotification(string method, Action<JsonElement> handler)
    {
        if (_process == null)
        {
            _pendingNotificationHandlers.Add(new PendingNotificationHandler(method, handler));
            return;
        }

        RegisterNotificationHandler(method, handler);
    }

    /// <summary>
    /// Register a handler for incoming requests.
    /// </summary>
    public void OnRequest(string method, Func<JsonElement, Task<JsonElement>> handler)
    {
        if (_process == null)
        {
            // Pre-StartAsync: queue for later registration (symmetric with OnNotification).
            // The previous implementation incorrectly added a no-op notification handler
            // and silently discarded the request handler — losing it permanently.
            _pendingRequestHandlers.Add(new PendingRequestHandler(method, handler));
            return;
        }

        RegisterRequestHandler(method, handler);
    }

    private void RegisterNotificationHandler(string method, Action<JsonElement> handler)
    {
        lock (_notificationHandlers)
            _notificationHandlers[method] = handler;
    }

    private void RegisterRequestHandler(string method, Func<JsonElement, Task<JsonElement>> handler)
    {
        lock (_requestHandlers)
            _requestHandlers[method] = handler;
    }

    // Message reading loop

    /// <summary>
    /// Background loop: reads Content-Length-delimited JSON-RPC messages from server stdout.
    /// </summary>
    private async Task ReadMessageLoopAsync()
    {
        try
        {
            var stream = _process!.StandardOutput.BaseStream;
            var oneByte = new byte[1];
            List<byte> headerBuffer = [];

            while (_process != null && !_process.HasExited)
            {
                var read = await stream.ReadAsync(oneByte, 0, 1).ConfigureAwait(false);
                if (read == 0) break; // EOF
                var b = oneByte[0];

                // Phase 1: accumulate header bytes until \r\n\r\n terminator.
                headerBuffer.Add(b);
                if (headerBuffer.Count >= 4 &&
                    headerBuffer[^4] == '\r' && headerBuffer[^3] == '\n' &&
                    headerBuffer[^2] == '\r' && headerBuffer[^1] == '\n')
                {
                    var headerText = System.Text.Encoding.ASCII.GetString(headerBuffer.ToArray());
                    // LSP 协议头格式固定为 "Content-Length: <digits>\r\n"，用字符串查找替代正则。
                    // 正则在此场景属于过度设计，IndexOf + int.TryParse 更轻量且无 ReDoS 隐患。
                    var contentLength = TryParseContentLength(headerText);
                    headerBuffer.Clear();
                    if (contentLength.HasValue)
                    {
                        // Phase 2: read exactly contentLength bytes of JSON payload.
                        var content = new byte[contentLength.Value];
                        var offset = 0;
                        while (offset < contentLength.Value)
                        {
                            var n = await stream.ReadAsync(content, offset, contentLength.Value - offset).ConfigureAwait(false);
                            if (n == 0) break; // EOF mid-content
                            offset += n;
                        }

                        if (offset < contentLength.Value) break; // truncated stream

                        var jsonText = System.Text.Encoding.UTF8.GetString(content);
                        await DispatchMessageAsync(jsonText).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LSP read loop error for {ServerName}", _serverName);
        }
    }

    /// <summary>
    /// 从 LSP 协议头文本中解析 Content-Length 值。
    ///
    /// <para>LSP 协议头格式固定为 <c>Content-Length: &lt;digits&gt;\r\n</c>（可能还有 Content-Type 等其他头）。
    /// 用字符串查找替代正则，更轻量且无 ReDoS 隐患。</para>
    ///
    /// <para>查找策略：
    /// <list type="number">
    ///   <item>用 <see cref="string.IndexOf(string, StringComparison)"/> 定位 "Content-Length:" 标记</item>
    ///   <item>从标记后开始跳过空白字符</item>
    ///   <item>读取连续的数字字符</item>
    ///   <item>用 <see cref="int.TryParse(string, NumberStyles, IFormatProvider, out int)"/> 解析为整数</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <returns>解析成功返回 Content-Length 值；找不到或解析失败返回 null。</returns>
    private static int? TryParseContentLength(string headerText)
    {
        const string Marker = "Content-Length:";
        var markerIndex = headerText.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        var i = markerIndex + Marker.Length;

        // 跳过标记后的空白字符（空格、制表符）
        while (i < headerText.Length && (headerText[i] == ' ' || headerText[i] == '\t'))
            i++;

        // 读取连续的数字字符
        var start = i;
        while (i < headerText.Length && headerText[i] >= '0' && headerText[i] <= '9')
            i++;

        if (i == start)
            return null;  // 标记后没有数字

        var numberSpan = headerText.AsSpan(start, i - start);
        if (int.TryParse(numberSpan, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength))
            return contentLength;

        return null;
    }

    private async Task DispatchMessageAsync(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (root.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString() ?? "";
                var hasId = root.TryGetProperty("id", out _);

                if (hasId)
                {
                    // Preserve the raw JSON form of id so we can echo it back unchanged
                    // (string vs number). GetRawText() is correct here — JsonDocument.Parse
                    // reconstitutes the original JSON token.
                    var requestIdRaw = root.TryGetProperty("id", out var ridEl) ? ridEl.GetRawText() : "null";
                    // Clone params so the handler can use them after doc is disposed.
                    var @params = root.TryGetProperty("params", out var pEl) ? pEl.Clone() : EmptyObject;

                    Func<JsonElement, Task<JsonElement>>? handler = null;
                    lock (_requestHandlers)
                    {
                        if (_requestHandlers.TryGetValue(method, out var h))
                            handler = h;
                    }

                    if (handler != null)
                    {
                        var taskKey = Guid.NewGuid().ToString("N");
                        _outstandingServerRequests[taskKey] = Task.Run(async () =>
                        {
                            try
                            {
                                var result = await handler(@params).ConfigureAwait(false);
                                using var idDoc = JsonDocument.Parse(requestIdRaw);
                                var response = new
                                {
                                    jsonrpc = "2.0",
                                    id = idDoc.RootElement.Clone(),
                                    result
                                };
                                await SendJsonRpcMessageAsync(response).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to handle server request {Method}", method);
                                using var idDoc = JsonDocument.Parse(requestIdRaw);
                                var errorResponse = new
                                {
                                    jsonrpc = "2.0",
                                    id = idDoc.RootElement.Clone(),
                                    error = new { code = -32603, message = ex.Message }
                                };
                                await SendJsonRpcMessageAsync(errorResponse).ConfigureAwait(false);
                            }
                            finally
                            {
                                _outstandingServerRequests.TryRemove(taskKey, out _);
                            }
                        }, CancellationToken.None);
                    }
                    else
                    {
                        _logger.LogDebug("No handler for server request {Method}", method);
                        using var idDoc = JsonDocument.Parse(requestIdRaw);
                        var errorResponse = new
                        {
                            jsonrpc = "2.0",
                            id = idDoc.RootElement.Clone(),
                            error = new { code = -32601, message = $"Method not found: {method}" }
                        };
                        await SendJsonRpcMessageAsync(errorResponse).ConfigureAwait(false);
                    }
                }
                else
                {
                    // Notification from server (e.g., textDocument/publishDiagnostics)
                    lock (_notificationHandlers)
                    {
                        if (_notificationHandlers.TryGetValue(method, out var handler))
                        {
                            var @params = root.TryGetProperty("params", out var p) ? p : EmptyObject;
                            handler(@params);
                        }
                    }
                }
            }
            else if (root.TryGetProperty("id", out var idProp))
            {
                // Response to our request. Must use ToPendingRequestKey — GetRawText() on a
                // string id returns quoted JSON (e.g. "\"abc\"") which never matches the
                // unquoted key we stored when sending the request, causing every response
                // to be dropped and every request to time out.
                var requestId = ToPendingRequestKey(idProp);
                lock (_pendingRequests)
                {
                    if (_pendingRequests.TryGetValue(requestId, out var tcs))
                    {
                        _pendingRequests.Remove(requestId);
                        try
                        {
                            if (root.TryGetProperty("error", out var error))
                                tcs.SetException(new LspException($"LSP request failed: {error.GetRawText()}"));
                            else if (root.TryGetProperty("result", out var result))
                                tcs.SetResult(result.Clone());
                            else
                                tcs.SetResult(EmptyNull);
                        }
                        catch (Exception ex) { tcs.SetException(ex); }
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Received LSP response for unknown/expired request id {RequestId}",
                            requestId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch LSP message");
        }
    }

    private async Task SendJsonRpcMessageAsync(object message)
    {
        if (_process == null)
            throw new InvalidOperationException("LSP client not started");

        var json = JsonSerializer.Serialize(message);
        var contentBytes = System.Text.Encoding.UTF8.GetBytes(json);

        // LSP uses Content-Length header format
        var header = $"Content-Length: {contentBytes.Length}\r\n\r\n";
        var headerBytes = System.Text.Encoding.ASCII.GetBytes(header);

        await _process.StandardInput.BaseStream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.WriteAsync(contentBytes, 0, contentBytes.Length).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Normalize a JSON-RPC <c>id</c> value to the key used in <see cref="_pendingRequests"/>.
    /// String ids must use <see cref="JsonElement.GetString"/> — <see cref="JsonElement.GetRawText"/>
    /// includes surrounding quotes and would never match the unquoted key stored at send time.
    /// </summary>
    internal static string ToPendingRequestKey(JsonElement id) =>
        id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? "",
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.Null => "null",
            _ => id.GetRawText(),
        };

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (!_isStopping)
        {
            _isInitialized = false;
            var crashError = new Exception($"LSP server {_serverName} crashed with exit code {_process?.ExitCode}");
            _logger.LogError(crashError, "LSP server crashed");
            _onCrash?.Invoke(crashError);
        }
    }

    /// <summary>
    /// Stop the LSP server gracefully.
    /// </summary>
    public async Task StopAsync()
    {
        _isStopping = true;

        try
        {
            if (_process != null && !_process.HasExited)
            {
                await SendNotificationAsync("shutdown", EmptyObject).ConfigureAwait(false);
                await SendNotificationAsync("exit", EmptyObject).ConfigureAwait(false);

                using var exitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Constants.Lsp.ProcessExitWaitMs));
                try
                {
                    await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill();
                }
            }

            // Drain server→client request handlers so in-flight tasks are observed.
            if (!_outstandingServerRequests.IsEmpty)
            {
                try
                {
                    await Task.WhenAll(_outstandingServerRequests.Values)
                        .WaitAsync(TimeSpan.FromSeconds(Constants.Lsp.OutstandingRequestDrainSec))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Draining outstanding server requests timed out for {ServerName}", _serverName);
                }
            }

            // Observe the read loop so unobserved-task-exception warnings don't fire.
            if (_readLoopTask != null)
            {
                try
                {
                    await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(Constants.Lsp.OutstandingRequestDrainSec)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Read loop did not complete within timeout for {ServerName}", _serverName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LSP server {ServerName} stop failed: {Message}", _serverName, ex.Message);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _isInitialized = false;
            Capabilities = null;
            _isStopping = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private record PendingNotificationHandler(string Method, Action<JsonElement> Handler);
    private record PendingRequestHandler(string Method, Func<JsonElement, Task<JsonElement>> Handler);
}

/// <summary>
/// Exception thrown when an LSP request fails.
/// </summary>
public sealed class LspException : Exception
{
    public LspException(string message) : base(message) { }
    public LspException(string message, Exception inner) : base(message, inner) { }
}
