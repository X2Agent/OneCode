using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

public sealed class EditTransactionTests
{
    [Fact]
    public async Task DisposeWithoutCommit_RollsBackModifiedFile()
    {
        var tempDir = CreateTempDir();
        try
        {
            var path = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);

            using (var transaction = new EditTransaction())
            {
                transaction.Snapshot(path);
                await File.WriteAllTextAsync(path, "changed", TestContext.Current.CancellationToken);
            }

            File.ReadAllText(path).Should().Be("original");
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Commit_PreservesModifiedFile()
    {
        var tempDir = CreateTempDir();
        try
        {
            var path = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);

            using (var transaction = new EditTransaction())
            {
                transaction.Snapshot(path);
                await File.WriteAllTextAsync(path, "changed", TestContext.Current.CancellationToken);
                transaction.Commit();
            }

            File.ReadAllText(path).Should().Be("changed");
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task GetModifiedFilesSince_IncludesFileTouchedBeforeAndAfterBaseline()
    {
        var tempDir = CreateTempDir();
        try
        {
            var path = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
            using var transaction = new EditTransaction();

            transaction.Snapshot(path);
            await File.WriteAllTextAsync(path, "first", TestContext.Current.CancellationToken);
            var version = transaction.CaptureChangeVersion();

            transaction.Snapshot(path);
            await File.WriteAllTextAsync(path, "second", TestContext.Current.CancellationToken);

            transaction.GetModifiedFilesSince(version).Should().ContainSingle().Which.Should().Be(path);
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Commit_WhenFileChangesAfterValidationBaseline_ThrowsConflict()
    {
        var tempDir = CreateTempDir();
        try
        {
            var path = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
            using var transaction = new EditTransaction();
            transaction.Snapshot(path);
            await File.WriteAllTextAsync(path, "validated", TestContext.Current.CancellationToken);
            transaction.CaptureValidationBaseline();

            await File.WriteAllTextAsync(path, "external", TestContext.Current.CancellationToken);

            var act = () => transaction.Commit();
            act.Should().Throw<InvalidOperationException>().WithMessage("*commit conflict*");
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task PreserveForManualReconciliation_DoesNotRestoreStaleSnapshot()
    {
        var tempDir = CreateTempDir();
        try
        {
            var path = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);

            using (var transaction = new EditTransaction())
            {
                transaction.Snapshot(path);
                await File.WriteAllTextAsync(path, "build-change", TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(path, "external-change", TestContext.Current.CancellationToken);
                transaction.PreserveForManualReconciliation();
            }

            File.ReadAllText(path).Should().Be("external-change");
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"EditTransactionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
