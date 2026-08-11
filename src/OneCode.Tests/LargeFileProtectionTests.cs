using OneCode.Infrastructure;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// Large-file protection tests — verifies the 10 MB
/// hard limit is enforced on file-snapshotting (EditTransaction) and exposed as a
/// named constant on PathsHelper.
/// </summary>
public sealed class LargeFileProtectionTests
{
    private readonly string _tempDir;

    public LargeFileProtectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LargeFileProtectionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private void Cleanup()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // EditTransaction.Snapshot — enforces the size limit

    [Fact]
    public async Task EditTransaction_Snapshot_FileLargerThanLimit_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_tempDir, "huge.txt");
        try
        {
            // Write exactly MaxFileReadSize + 1 byte
            await WriteBytesAsync(path, PathsHelper.MaxFileReadSize + 1, ct);
            var transaction = new EditTransaction();

            Action act = () => transaction.Snapshot(path);

            var thrown = act.Should().Throw<InvalidOperationException>().Which;
            thrown.Message.Should().Contain("too large");
            thrown.Message.Should().Contain("snapshot");
            thrown.Message.Should().Contain("MB");
        }
        finally { Cleanup(); }
    }

    [Fact]
    public async Task EditTransaction_Snapshot_FileExactlyAtLimit_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_tempDir, "exactly_limit.txt");
        try
        {
            await WriteBytesAsync(path, PathsHelper.MaxFileReadSize, ct);

            using var transaction = new EditTransaction();
            var act = () => transaction.Snapshot(path);

            act.Should().NotThrow();
            transaction.SnapshotCount.Should().Be(1);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public async Task EditTransaction_Snapshot_FileJustUnderLimit_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_tempDir, "under_limit.txt");
        try
        {
            await WriteBytesAsync(path, PathsHelper.MaxFileReadSize - 1, ct);

            using var transaction = new EditTransaction();
            var act = () => transaction.Snapshot(path);

            act.Should().NotThrow();
        }
        finally { Cleanup(); }
    }

    [Fact]
    public async Task EditTransaction_Snapshot_FileOverLimit_DoesNotAddToSnapshotCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_tempDir, "too_big.txt");
        try
        {
            await WriteBytesAsync(path, PathsHelper.MaxFileReadSize + 1024, ct);

            using var transaction = new EditTransaction();
            try { transaction.Snapshot(path); } catch (InvalidOperationException) { /* expected */ }

            // Snapshot must not have been recorded — the file's bytes were never read
            transaction.SnapshotCount.Should().Be(0);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public async Task EditTransaction_Snapshot_SmallFileAfterRejectedLargeFile_StillWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var bigPath = Path.Combine(_tempDir, "big.txt");
        var smallPath = Path.Combine(_tempDir, "small.txt");
        try
        {
            await WriteBytesAsync(bigPath, PathsHelper.MaxFileReadSize + 1, ct);
            await File.WriteAllTextAsync(smallPath, "small", ct);

            using var transaction = new EditTransaction();
            try { transaction.Snapshot(bigPath); } catch (InvalidOperationException) { /* expected */ }

            var act = () => transaction.Snapshot(smallPath);
            act.Should().NotThrow();
            transaction.SnapshotCount.Should().Be(1);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public async Task EditTransaction_Snapshot_LargeFile_ExceptionMessageContainsSizeAndLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_tempDir, "msg_check.txt");
        try
        {
            // Use exactly 11 MB so integer-division message format produces "11MB"
            await WriteBytesAsync(path, 11 * 1024 * 1024, ct);
            using var transaction = new EditTransaction();

            Action act = () => transaction.Snapshot(path);
            act.Should().Throw<InvalidOperationException>()
               .Which.Message.Should()
                   .Contain("11MB")          // actual size in MB (integer division)
                   .And.Contain("10MB");     // limit in MB
        }
        finally { Cleanup(); }
    }

    private static async Task WriteBytesAsync(string path, long byteCount, CancellationToken ct)
    {
        // Write in chunks to avoid LOH pressure for ~10 MB files
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 8192, useAsync: true);
        var chunkSize = 64 * 1024;
        var buffer = new byte[chunkSize];
        var remaining = byteCount;
        while (remaining > 0)
        {
            var toWrite = (int)Math.Min(remaining, chunkSize);
            await stream.WriteAsync(buffer.AsMemory(0, toWrite), ct);
            remaining -= toWrite;
        }
    }
}
