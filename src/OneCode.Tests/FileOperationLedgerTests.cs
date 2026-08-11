using OneCode.Infrastructure.Workflows;

namespace OneCode.Tests;

public sealed class FileOperationLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "operation-ledger", Guid.NewGuid().ToString("N"));

    public FileOperationLedgerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task BeginAndCommit_RoundTripsWithEvidence()
    {
        var ledger = new FileOperationLedger(_root);
        var op = "build/br-1/attempt/1/agent-edit-transaction";

        await ledger.BeginTransactionAsync(op, "file-transaction", 7, TestContext.Current.CancellationToken);
        var file = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(file, "before", TestContext.Current.CancellationToken);
        await ledger.AddFileIntentAsync(op, 7, file, "before"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file, "after", TestContext.Current.CancellationToken);
        await ledger.CommitTransactionAsync(op, 7, "evidence-1", TestContext.Current.CancellationToken);

        var loaded = await ledger.LoadAsync(op, TestContext.Current.CancellationToken);
        loaded.Should().NotBeNull();
        loaded!.IsCommitted.Should().BeTrue();
        loaded.Evidence.Should().Be("evidence-1");
        loaded.FileIntents.Should().ContainSingle().Which.Path.Should().Be(Path.GetFullPath(file));
        loaded.FileIntents[0].BeforeContent.Should().BeEquivalentTo("before"u8.ToArray());
        loaded.FileIntents[0].AfterHash.Should().NotBeNull();
    }

    [Fact]
    public async Task ReconcileAndRollback_UncommittedTransaction_RestoresBeforeContent()
    {
        var ledger = new FileOperationLedger(_root);
        var op = "goal/gr-1/step/1/fence/5";
        var file = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(file, "original", TestContext.Current.CancellationToken);
        await ledger.BeginTransactionAsync(op, "file-transaction", 5, TestContext.Current.CancellationToken);
        await ledger.AddFileIntentAsync(op, 5, file, "original"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file, "mutated", TestContext.Current.CancellationToken);

        var result = await ledger.ReconcileAndRollbackAsync(op, TestContext.Current.CancellationToken);

        result.HadResidual.Should().BeTrue();
        result.RolledBackFiles.Should().Contain(Path.GetFullPath(file));
        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should().Be("original");
    }

    [Fact]
    public async Task ReconcileAndRollback_NewFile_DeletesResidual()
    {
        var ledger = new FileOperationLedger(_root);
        var op = "goal/gr-2/step/1/fence/5";
        var file = Path.Combine(_root, "new.txt");
        await ledger.BeginTransactionAsync(op, "file-transaction", 5, TestContext.Current.CancellationToken);
        await ledger.AddFileIntentAsync(op, 5, file, null, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file, "residual", TestContext.Current.CancellationToken);

        var result = await ledger.ReconcileAndRollbackAsync(op, TestContext.Current.CancellationToken);

        File.Exists(file).Should().BeFalse();
        result.RolledBackFiles.Should().Contain(Path.GetFullPath(file));
    }

    [Fact]
    public async Task ReconcileAndRollback_CommittedTransaction_DoesNotTouchFiles()
    {
        var ledger = new FileOperationLedger(_root);
        var op = "team/tr-1/fence/9";
        var file = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(file, "original", TestContext.Current.CancellationToken);
        await ledger.BeginTransactionAsync(op, "file-transaction", 9, TestContext.Current.CancellationToken);
        await ledger.AddFileIntentAsync(op, 9, file, "original"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file, "committed", TestContext.Current.CancellationToken);
        await ledger.CommitTransactionAsync(op, 9, null, TestContext.Current.CancellationToken);

        var result = await ledger.ReconcileAndRollbackAsync(op, TestContext.Current.CancellationToken);

        result.AlreadyCommitted.Should().BeTrue();
        result.RolledBackFiles.Should().BeEmpty();
        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Should().Be("committed");
    }

    [Fact]
    public async Task ReconcileRun_OnlyRollsBackMatchingUncommittedPrefix()
    {
        var ledger = new FileOperationLedger(_root);
        var opA = "build/br-1/attempt/1/agent-edit-transaction";
        var opB = "build/br-1/attempt/2/agent-edit-transaction";
        var opOther = "goal/gr-9/step/1/fence/3";
        var fileA = Path.Combine(_root, "a.txt");
        var fileB = Path.Combine(_root, "b.txt");
        var fileOther = Path.Combine(_root, "c.txt");
        await File.WriteAllTextAsync(fileA, "a0", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fileB, "b0", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fileOther, "c0", TestContext.Current.CancellationToken);
        foreach (var (op, fence, file) in new[]
                 {
                     (opA, 1L, fileA), (opB, 2L, fileB), (opOther, 3L, fileOther),
                 })
        {
            await ledger.BeginTransactionAsync(op, "file-transaction", fence, TestContext.Current.CancellationToken);
            await ledger.AddFileIntentAsync(op, fence, file, File.ReadAllBytes(file), TestContext.Current.CancellationToken);
        }
        await File.WriteAllTextAsync(fileA, "a-mutated", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fileB, "b-mutated", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fileOther, "c-mutated", TestContext.Current.CancellationToken);

        var results = await ledger.ReconcileRunAsync("build/br-1", TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
        (await File.ReadAllTextAsync(fileA, TestContext.Current.CancellationToken)).Should().Be("a0");
        (await File.ReadAllTextAsync(fileB, TestContext.Current.CancellationToken)).Should().Be("b0");
        (await File.ReadAllTextAsync(fileOther, TestContext.Current.CancellationToken)).Should().Be("c-mutated");
    }

    [Fact]
    public async Task StaleFencingToken_RejectedOnMutation()
    {
        var ledger = new FileOperationLedger(_root);
        var op = "build/br-2/attempt/1/agent-edit-transaction";
        await ledger.BeginTransactionAsync(op, "file-transaction", 7, TestContext.Current.CancellationToken);

        var act = () => ledger.AddFileIntentAsync(op, 8, Path.Combine(_root, "a.txt"), null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fencing token*");
    }

    [Fact]
    public async Task CorruptEnvelope_FailsClosedToNull()
    {
        var ledger = new FileOperationLedger(_root);
        var op = "build/br-3/attempt/1/agent-edit-transaction";
        await ledger.BeginTransactionAsync(op, "file-transaction", 7, TestContext.Current.CancellationToken);
        var path = Directory.EnumerateFiles(_root, "*.op.json").Single();
        await File.WriteAllTextAsync(path, "{ corrupt", TestContext.Current.CancellationToken);

        var loaded = await ledger.LoadAsync(op, TestContext.Current.CancellationToken);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task IntentIsIdempotent_AcrossDuplicateAdds()
    {
        var ledger = new FileOperationLedger(_root);
        var op = "build/br-4/attempt/1/agent-edit-transaction";
        var file = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(file, "v0", TestContext.Current.CancellationToken);
        await ledger.BeginTransactionAsync(op, "file-transaction", 7, TestContext.Current.CancellationToken);
        await ledger.AddFileIntentAsync(op, 7, file, "v0"u8.ToArray(), TestContext.Current.CancellationToken);
        await ledger.AddFileIntentAsync(op, 7, file, "v0"u8.ToArray(), TestContext.Current.CancellationToken);

        var loaded = await ledger.LoadAsync(op, TestContext.Current.CancellationToken);
        loaded!.FileIntents.Should().ContainSingle();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
