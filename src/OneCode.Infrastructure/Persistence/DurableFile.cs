namespace OneCode.Infrastructure.Persistence;

/// <summary>
/// 单机 JSON 落盘的共享持久化原语：跨进程文件锁 + 临时文件原子替换 + .bak 兜底。
/// Build / Goal / Workflow 各 run store 共用，避免各写一套耐久性机制。
/// 聚合语义（事件序列校验、版本围栏、checksum 格式）由各 store 自持，不走这里。
/// </summary>
internal static class DurableFile
{
    /// <summary>
    /// 获取排他锁文件（<c>FileShare.None</c> + <c>DeleteOnClose</c>）。其他进程持有锁时
    /// 以固定节奏重试；超过尝试次数后抛 <see cref="IOException"/> 而不是无限等待。
    /// </summary>
    public static async Task<FileStream> AcquireLockAsync(
        string lockPath,
        int maxAttempts = 50,
        int retryDelayMs = 20,
        CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 同步原子替换写入：payload 写入临时文件并 flush-to-disk（<paramref name="validateTemp"/>
    /// 在替换前复核临时文件，失败则中止且不动主文件）→ 主文件复制为 .bak
    /// （<paramref name="canBackupSource"/> 返回 false 时保留既有 .bak，避免把损坏的
    /// 主文件盖掉上一份有效备份）→ 临时文件原子替换主文件 → <paramref name="validateWritten"/>
    /// 复核落盘结果。临时文件在 finally 中清理。
    /// </summary>
    public static void WriteAtomic(
        string path,
        byte[] payload,
        Action<string>? validateTemp = null,
        Func<string, bool>? canBackupSource = null,
        Action<string>? validateWritten = null)
    {
        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            WritePayload(tempPath, payload);
            validateTemp?.Invoke(tempPath);

            if (File.Exists(path) && (canBackupSource?.Invoke(path) ?? true))
                File.Copy(path, path + ".bak", overwrite: true);

            File.Move(tempPath, path, overwrite: true);
            validateWritten?.Invoke(path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// 异步变体：锁与原子替换语义同 <see cref="WriteAtomic"/>，但**不提供**校验钩子——
    /// 异步调用方的落盘后复核是 store 专属逻辑（需读回并 await 的校验场景），
    /// 收进本原语只会多一个异步回调参数而不减少重复。
    /// </summary>
    public static async Task WriteAtomicAsync(
        string path,
        byte[] payload,
        CancellationToken ct = default,
        Func<string, bool>? canBackupSource = null)
    {
        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await WritePayloadAsync(tempPath, payload, ct).ConfigureAwait(false);

            if (File.Exists(path) && (canBackupSource?.Invoke(path) ?? true))
                File.Copy(path, path + ".bak", overwrite: true);

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void WritePayload(string tempPath, byte[] payload)
    {
        using var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WritePayloadAsync(string tempPath, byte[] payload, CancellationToken ct)
    {
        await using (var stream = new FileStream(
                         tempPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         FileOptions.WriteThrough))
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }
}
