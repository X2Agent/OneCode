using Renci.SshNet;

namespace OneCode.Infrastructure.Remote;

public sealed record SshConnectionConfig(
    string Host,
    int Port = 22,
    string? Username = null,
    string? Password = null,
    string? PrivateKeyPath = null,
    string? PrivateKeyPassphrase = null,
    string? WorkingDirectory = null)
{
    public string EffectiveWorkingDirectory => WorkingDirectory ?? "/home/" + (Username ?? "root");

    /// <summary>
    /// Try to parse an SSH workspace string (user@host:port/path or host:/path).
    /// Returns null if the string does not look like an SSH target.
    /// </summary>
    public static SshConnectionConfig? TryParse(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            return null;

        // Must contain "@" or ":<path>" to be SSH. Pure local paths are not SSH.
        var atIndex = workspace.IndexOf('@');
        var colonIndex = workspace.IndexOf(':');

        // No '@' and no ':' means it's not SSH
        if (atIndex < 0 && colonIndex < 0)
            return null;

        // Has '@' but no ':' after it — it's a user@host format
        // Windows paths like C:\... would confuse this, but those are detected as local
        if (colonIndex >= 0 && colonIndex < 3 && workspace.Length > 1 && workspace[1] == ':')
            return null; // Windows absolute path like C:\foo

        string? username = null;
        string hostPart;

        if (atIndex >= 0 && (colonIndex < 0 || atIndex < colonIndex))
        {
            username = workspace[..atIndex];
            hostPart = workspace[(atIndex + 1)..];
        }
        else
        {
            hostPart = workspace;
        }

        // Split host:port/path
        string host;
        int port = 22;
        string? workingDir = null;

        var slashIndex = hostPart.IndexOf('/');
        var hostColonIndex = hostPart.IndexOf(':');

        if (slashIndex >= 0)
        {
            workingDir = hostPart[slashIndex..];
            hostPart = hostPart[..slashIndex];
        }

        if (hostColonIndex >= 0)
        {
            host = hostPart[..hostColonIndex];
            if (int.TryParse(hostPart[(hostColonIndex + 1)..], out var p))
                port = p;
        }
        else
        {
            host = hostPart;
        }

        if (string.IsNullOrWhiteSpace(host))
            return null;

        return new SshConnectionConfig(
            Host: host,
            Port: port,
            Username: username,
            WorkingDirectory: workingDir);
    }
}

public sealed record SshCommandResult(
    string Command,
    string StandardOutput,
    string StandardError,
    int ExitCode,
    TimeSpan Duration)
{
    public bool Success => ExitCode == 0;
}

public sealed class SshRemoteService : IAsyncDisposable
{
    private SshClient? _client;
    private SftpClient? _sftpClient;
    private SshConnectionConfig? _config;
    private readonly ILogger<SshRemoteService> _logger;
    private readonly object _lock = new();

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _client?.IsConnected ?? false;
            }
        }
    }

    public SshConnectionConfig? Config => _config;

    public SshRemoteService(ILogger<SshRemoteService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> ConnectAsync(SshConnectionConfig config, CancellationToken ct = default)
    {
        _config = config;

        // Dispose any existing connections before creating new ones —
        // overwriting without disposing leaks SshClient/SftpClient resources.
        Disconnect();

        try
        {
            List<AuthenticationMethod> authMethods = [];

            if (!string.IsNullOrEmpty(config.Password))
            {
                authMethods.Add(new PasswordAuthenticationMethod(
                    config.Username ?? Environment.UserName,
                    config.Password));
            }

            if (!string.IsNullOrEmpty(config.PrivateKeyPath) && File.Exists(config.PrivateKeyPath))
            {
                var keyFile = string.IsNullOrEmpty(config.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(config.PrivateKeyPath)
                    : new PrivateKeyFile(config.PrivateKeyPath, config.PrivateKeyPassphrase);
                authMethods.Add(new PrivateKeyAuthenticationMethod(
                    config.Username ?? Environment.UserName,
                    keyFile));
            }

            if (authMethods.Count == 0)
            {
                var defaultKeyPath = Path.Combine(
                    PathsHelper.UserHome,
                    ".ssh", "id_rsa");
                if (File.Exists(defaultKeyPath))
                {
                    var keyFile = new PrivateKeyFile(defaultKeyPath);
                    authMethods.Add(new PrivateKeyAuthenticationMethod(
                        config.Username ?? Environment.UserName,
                        keyFile));
                }
            }

            var connectionInfo = new ConnectionInfo(
                config.Host,
                config.Port,
                config.Username ?? Environment.UserName,
                authMethods.ToArray());

            _client = new SshClient(connectionInfo);
            _sftpClient = new SftpClient(connectionInfo);

            await Task.Run(() =>
            {
                _client.Connect();
                _sftpClient.Connect();
            }, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "SSH connected to {Host}:{Port} as {User}",
                config.Host, config.Port, config.Username ?? Environment.UserName);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH connection failed to {Host}:{Port}", config.Host, config.Port);
            return false;
        }
    }

    public async Task<SshCommandResult> ExecuteCommandAsync(
        string command,
        string? workingDirectory = null,
        int timeoutMs = 30_000,
        CancellationToken ct = default)
    {
        if (!IsConnected || _client == null)
            throw new InvalidOperationException("SSH not connected");

        var cwd = workingDirectory ?? _config?.EffectiveWorkingDirectory ?? "/";

        if (!IsSafePath(cwd))
            throw new ArgumentException($"Unsafe working directory path: {cwd}");

        var safeCommand = $"cd '{cwd.Replace("'", "'\\''")}' 2>/dev/null; {command}";

        // Use Stopwatch on the caller side rather than relying on cmd.CommandTimeout
        // (which only aborts synchronous reads), so the duration we report reflects
        // actual wall-clock execution time and includes remote latency.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(() =>
            {
                var cmd = _client.CreateCommand(safeCommand);
                cmd.CommandTimeout = TimeSpan.FromMilliseconds(timeoutMs);
                var output = cmd.Execute();
                return new
                {
                    Output = output ?? "",
                    Error = cmd.Error ?? "",
                    ExitCode = cmd.ExitStatus ?? -1,
                };
            }, ct).ConfigureAwait(false);

            stopwatch.Stop();
            _logger.LogDebug("SSH command: {Cmd} → exit={Exit} in {Ms}ms",
                command, result.ExitCode, stopwatch.ElapsedMilliseconds);

            return new SshCommandResult(
                command,
                result.Output,
                result.Error,
                result.ExitCode,
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "SSH command failed: {Cmd}", command);
            return new SshCommandResult(command, "", ex.Message, -1, stopwatch.Elapsed);
        }
    }

    public async Task<string?> ReadFileAsync(string remotePath, CancellationToken ct = default)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected)
            return null;

        try
        {
            return await Task.Run(() =>
            {
                using var stream = _sftpClient.OpenRead(remotePath);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH read file failed: {Path}", remotePath);
            return null;
        }
    }

    public async Task<bool> WriteFileAsync(string remotePath, string content, CancellationToken ct = default)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected)
            return false;

        try
        {
            await Task.Run(() =>
            {
                using var stream = _sftpClient.Create(remotePath);
                using var writer = new StreamWriter(stream);
                writer.Write(content);
            }, ct).ConfigureAwait(false);

            _logger.LogDebug("SSH write file: {Path} ({Len} bytes)", remotePath, content.Length);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH write file failed: {Path}", remotePath);
            return false;
        }
    }

    public async Task<bool> FileExistsAsync(string remotePath, CancellationToken ct = default)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected)
            return false;

        return await Task.Run(() => _sftpClient.Exists(remotePath), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected)
            return [];

        try
        {
            return await Task.Run(() =>
            {
                return _sftpClient.ListDirectory(remotePath)
                    .Select(f => f.Name)
                    .Where(n => n != "." && n != "..")
                    .ToList()
                    .AsReadOnly();
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH list directory failed: {Path}", remotePath);
            return [];
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try
            {
                if (_sftpClient?.IsConnected == true)
                    _sftpClient.Disconnect();
                if (_client?.IsConnected == true)
                    _client.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SSH disconnect error");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        lock (_lock)
        {
            _sftpClient?.Dispose();
            _sftpClient = null;
            _client?.Dispose();
            _client = null;
        }
        return ValueTask.CompletedTask;
    }

    private static bool IsSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var invalidChars = new[] { ';', '&', '|', '`', '$', '(', ')', '{', '}', '<', '>', '\n', '\r' };
        return invalidChars.All(c => !path.Contains(c));
    }
}
