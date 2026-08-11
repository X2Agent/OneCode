using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneCode.Core.Coordinator;

namespace OneCode.Infrastructure.Teams;

public sealed class JsonTeamRunStore : ITeamRunStore
{
    private readonly string _baseDirectory;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public JsonTeamRunStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public async Task<TeamRun?> LoadAsync(TeamRunId runId, CancellationToken ct = default)
    {
        var path = GetPath(runId);
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await SkipChecksumHeaderAsync(stream, ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<TeamRun>(stream, Options, ct).ConfigureAwait(false);
    }

    public async Task<TeamRun?> LoadActiveAsync(string workingDirectory, CancellationToken ct = default)
    {
        if (!Directory.Exists(_baseDirectory))
            return null;

        TeamRun? newest = null;
        foreach (var file in Directory.EnumerateFiles(_baseDirectory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await SkipChecksumHeaderAsync(stream, ct).ConfigureAwait(false);
            var run = await JsonSerializer.DeserializeAsync<TeamRun>(stream, Options, ct).ConfigureAwait(false);
            if (run is null || IsTerminal(run.Status)
                || !Path.GetFullPath(run.WorkingDirectory).Equals(
                    Path.GetFullPath(workingDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (newest is null || run.UpdatedAt > newest.UpdatedAt)
                newest = run;
        }

        return newest;
    }

    public async Task<IReadOnlyList<TeamRun>> ListActiveAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_baseDirectory))
            return [];

        var active = new List<TeamRun>();
        foreach (var file in Directory.EnumerateFiles(_baseDirectory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await SkipChecksumHeaderAsync(stream, ct).ConfigureAwait(false);
            var run = await JsonSerializer.DeserializeAsync<TeamRun>(stream, Options, ct).ConfigureAwait(false);
            if (run is not null && !IsTerminal(run.Status))
                active.Add(run);
        }

        return active
            .OrderByDescending(run => run.UpdatedAt)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Skips the `# checksum:xxx\n` header line written by <see cref="WriteAtomicAsync"/>.
    /// Legacy files without the header are handled gracefully by checking the first byte.
    /// Uses raw byte reads to avoid StreamReader's internal buffering which would
    /// consume JSON payload bytes beyond the header line.
    /// </summary>
    private static Task SkipChecksumHeaderAsync(Stream stream, CancellationToken ct)
    {
        var firstByte = stream.ReadByte();
        if (firstByte == -1)
            return Task.CompletedTask;

        if (firstByte != (byte)'#')
        {
            // Legacy file without checksum header — rewind so JSON deserializer sees full content.
            stream.Position = 0;
            return Task.CompletedTask;
        }

        // Read remaining bytes of the checksum header line until '\n' (exclusive).
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            ct.ThrowIfCancellationRequested();
            if (b == (byte)'\n')
                break;
        }

        return Task.CompletedTask;
    }

    public async Task<bool> TrySaveAsync(
        TeamRun run,
        long expectedVersion,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_baseDirectory);
        var path = GetPath(run.Id);
        var lockPath = path + ".lock";
        await using var lease = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        var current = await LoadAsync(run.Id, ct).ConfigureAwait(false);
        if ((current?.Version ?? 0) != expectedVersion)
            return false;

        // 已 Claim 的 TeamRun 拒绝无令牌写入；Claim 前的准备阶段（审批/创建）不受影响。
        if (current?.WorkflowFencingToken is { } claimed
            && run.WorkflowFencingToken != claimed)
        {
            throw new InvalidOperationException(
                $"TeamRun '{run.Id}' is workflow-claimed with fencing token {claimed}; unfenced saves are rejected.");
        }

        await WriteAtomicAsync(path, run, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<TeamRun> ClaimWorkflowAsync(
        TeamRunId runId,
        long fencingToken,
        long expectedVersion,
        CancellationToken ct = default)
    {
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken), "Fencing token must be positive.");

        Directory.CreateDirectory(_baseDirectory);
        var path = GetPath(runId);
        var lockPath = path + ".lock";
        await using var lease = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        var current = await LoadAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"TeamRun '{runId}' was not found.");
        if (current.Version != expectedVersion)
        {
            throw new InvalidOperationException(
                $"TeamRun '{runId}' version conflict: expected {expectedVersion}, found {current.Version}.");
        }
        if (current.WorkflowFencingToken is { } existing && fencingToken <= existing)
        {
            throw new InvalidOperationException(
                $"TeamRun '{runId}' fencing token must increase monotonically (current {existing}, attempted {fencingToken}).");
        }

        var claimed = current with
        {
            WorkflowFencingToken = fencingToken,
            Version = checked(current.Version + 1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await WriteAtomicAsync(path, claimed, ct).ConfigureAwait(false);
        return claimed;
    }

    public async Task SaveFencedAsync(
        TeamRun run,
        long expectedVersion,
        long expectedFencingToken,
        CancellationToken ct = default)
    {
        if (expectedFencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedFencingToken), "Fencing token must be positive.");

        Directory.CreateDirectory(_baseDirectory);
        var path = GetPath(run.Id);
        var lockPath = path + ".lock";
        await using var lease = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        var current = await LoadAsync(run.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"TeamRun '{run.Id}' was not found.");
        if (current.WorkflowFencingToken != expectedFencingToken)
        {
            throw new InvalidOperationException(
                $"TeamRun '{run.Id}' fencing token mismatch: run is held by {current.WorkflowFencingToken?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}, attempted {expectedFencingToken}.");
        }
        if (current.Version != expectedVersion)
        {
            throw new InvalidOperationException(
                $"TeamRun '{run.Id}' version conflict: expected {expectedVersion}, found {current.Version}.");
        }

        await WriteAtomicAsync(path, run, ct).ConfigureAwait(false);
    }

    private async Task WriteAtomicAsync(string path, TeamRun run, CancellationToken ct)
    {
        var tempPath = path + ".tmp";
        try
        {
            // Serialize payload and compute SHA256 checksum for integrity verification.
            byte[] payload;
            using (var memoryStream = new MemoryStream())
            {
                await JsonSerializer.SerializeAsync(memoryStream, run, Options, ct).ConfigureAwait(false);
                payload = memoryStream.ToArray();
            }
            var checksum = ComputeChecksum(payload);

            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.WriteThrough))
            {
                // Write checksum header line followed by the JSON payload.
                var header = Encoding.UTF8.GetBytes($"# checksum:{checksum}\n");
                await stream.WriteAsync(header, ct).ConfigureAwait(false);
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Backup the previous valid file before replacing it.
            if (File.Exists(path))
            {
                var backupPath = path + ".bak";
                File.Copy(path, backupPath, overwrite: true);
            }

            File.Move(tempPath, path, overwrite: true);

            // Write-after-read verification: reload and validate checksum to detect
            // partial writes, disk corruption, or filesystem anomalies.
            await VerifyWrittenFileAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string ComputeChecksum(byte[] payload)
    {
        var hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task VerifyWrittenFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new IOException($"TeamRun file '{path}' was not persisted after write.");

        // Read the entire file into memory to avoid StreamReader buffering issues
        // when splitting the checksum header from the JSON payload.
        byte[] fileBytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fileBytes = new byte[fs.Length];
            var offset = 0;
            while (offset < fileBytes.Length)
            {
                var read = await fs.ReadAsync(fileBytes.AsMemory(offset), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                offset += read;
            }
        }

        var newlineIndex = Array.IndexOf(fileBytes, (byte)'\n');
        if (newlineIndex == -1)
            throw new IOException($"TeamRun file '{path}' has no payload after checksum header.");

        var header = Encoding.UTF8.GetString(fileBytes, 0, newlineIndex);
        if (!header.StartsWith("# checksum:", StringComparison.Ordinal))
            throw new IOException($"TeamRun file '{path}' is missing the checksum header.");

        var expectedChecksum = header["# checksum:".Length..].Trim();
        var payloadBytes = fileBytes[(newlineIndex + 1)..];
        var actualChecksum = ComputeChecksum(payloadBytes);
        if (!string.Equals(expectedChecksum, actualChecksum, StringComparison.Ordinal))
            throw new IOException($"TeamRun file '{path}' checksum mismatch: expected {expectedChecksum}, actual {actualChecksum}.");
    }

    private string GetPath(TeamRunId runId)
    {
        if (string.IsNullOrWhiteSpace(runId.Value)
            || !Regex.IsMatch(runId.Value, "^[0-9a-f]{32}$", RegexOptions.IgnoreCase))
        {
            throw new ArgumentException("TeamRunId must contain 32 hexadecimal characters.", nameof(runId));
        }

        return Path.Combine(_baseDirectory, $"{runId.Value}.json");
    }

    private static bool IsTerminal(TeamRunStatus status)
        => status is TeamRunStatus.Succeeded
            or TeamRunStatus.Failed
            or TeamRunStatus.Cancelled
            or TeamRunStatus.RolledBack;
}
