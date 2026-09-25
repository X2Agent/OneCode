using OneCode.Infrastructure;

namespace OneCode.Tests;

// CodeIndexService — sorted key / O(log N) prefix search

public sealed class CodeIndexPrefixSearchTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "CodeIdxPrefix_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly CodeIndexService _svc = new();

    public CodeIndexPrefixSearchTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task PrefixSearch_ReturnsExactAndPrefixMatches()
    {
        await IndexAsync("Names.cs", """
            public class UserService { }
            public class UserRepository { }
            public class ProductService { }
            """);

        var results = _svc.Search("User");

        results.Should().Contain(r => r.Symbol.Name == "UserService");
        results.Should().Contain(r => r.Symbol.Name == "UserRepository");
        results.Should().NotContain(r => r.Symbol.Name == "ProductService");
    }

    [Fact]
    public async Task UpdateFiles_RebuildsSortedKeys()
    {
        await IndexAsync("A.cs", "public class OldSymbol { }");
        _svc.Search("OldSymbol").Should().ContainSingle();

        var newFile = Path.Combine(_tmpDir, "B.cs");
        File.WriteAllText(newFile, "public class NewSymbol { }");
        await _svc.UpdateFilesAsync(new[] { newFile });

        _svc.Search("NewSymbol").Should().ContainSingle();
        _svc.Search("OldSymbol").Should().ContainSingle("incremental update must not drop entries of untouched files");
    }

    private async Task IndexAsync(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_tmpDir, fileName), content);
        await _svc.BuildIndexAsync(_tmpDir);
    }
}
