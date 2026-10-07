using OneCode.Infrastructure.Persistence;

namespace OneCode.Tests;

/// <summary>
/// <see cref="DurableFile"/> 契约：原子替换写入、校验失败不动主文件、.bak 兜底、
/// 崩溃残留（*.tmp-*）清扫。此前无独立契约测试，原子性仅靠各 store 集成测间接覆盖。
/// </summary>
public sealed class DurableFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-durable-file-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_root, name);

    public DurableFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WriteAtomic_ReplacesContentAndLeavesNoTempResidue()
    {
        var path = PathFor("state.json");
        File.WriteAllText(path, "v1");

        DurableFile.WriteAtomic(path, "v2"u8.ToArray());

        File.ReadAllText(path).Should().Be("v2");
        Directory.EnumerateFiles(_root, "*.tmp-*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void WriteAtomic_ValidateTempFails_LeavesMainFileUntouched()
    {
        // 反证：替换前校验失败必须中止，主文件保持原内容。
        var path = PathFor("state.json");
        File.WriteAllText(path, "v1");

        var act = () => DurableFile.WriteAtomic(
            path,
            "corrupt"u8.ToArray(),
            validateTemp: _ => throw new InvalidDataException("bad temp"));

        act.Should().Throw<InvalidDataException>();
        File.ReadAllText(path).Should().Be("v1");
        Directory.EnumerateFiles(_root, "*.tmp-*").Should().BeEmpty("临时文件在 finally 中清理");
    }

    [Fact]
    public void WriteAtomic_CanBackupSourceFalse_PreservesExistingBackup()
    {
        // 语义：主文件损坏时（canBackupSource=false）不得用损坏内容盖掉上一份有效备份。
        var path = PathFor("state.json");
        var backup = path + ".bak";
        File.WriteAllText(path, "v1");
        File.WriteAllText(backup, "last-good");

        DurableFile.WriteAtomic(path, "v2"u8.ToArray(), canBackupSource: _ => false);

        File.ReadAllText(backup).Should().Be("last-good", "备份保留既有有效内容");
        File.ReadAllText(path).Should().Be("v2");
    }

    [Fact]
    public void WriteAtomic_HealthySource_CopiesToBackup()
    {
        var path = PathFor("state.json");
        File.WriteAllText(path, "v1");

        DurableFile.WriteAtomic(path, "v2"u8.ToArray());

        File.ReadAllText(path + ".bak").Should().Be("v1");
    }

    [Fact]
    public void SweepTempResidue_DeletesOnlyStaleTempFiles()
    {
        var nested = Path.Combine(_root, "goal-runs", "run-1");
        Directory.CreateDirectory(nested);
        var stale = Path.Combine(nested, "state.json.tmp-abc");
        File.WriteAllText(stale, "residue");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(2));
        var fresh = Path.Combine(nested, "state.json.tmp-def");
        File.WriteAllText(fresh, "in-flight");
        var keeper = Path.Combine(nested, "state.json");
        File.WriteAllText(keeper, "real");

        var removed = DurableFile.SweepTempResidue(_root, TimeSpan.FromHours(1));

        removed.Should().Be(1);
        File.Exists(stale).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue("1 小时内的临时文件可能是并发进程正在写入的");
        File.Exists(keeper).Should().BeTrue("只清 *.tmp-*");
    }

    [Fact]
    public void SweepTempResidue_MissingRoot_ReturnsZero()
    {
        DurableFile.SweepTempResidue(Path.Combine(_root, "nope"), TimeSpan.FromHours(1)).Should().Be(0);
    }
}
