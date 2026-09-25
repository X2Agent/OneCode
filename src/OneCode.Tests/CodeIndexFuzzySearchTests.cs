using OneCode.Infrastructure;

namespace OneCode.Tests;

// CodeIndexService — fuzzy / typo-tolerant Levenshtein search

public sealed class CodeIndexFuzzySearchTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "CodeIdxFuzzy_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly CodeIndexService _svc = new();

    public CodeIndexFuzzySearchTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task FuzzySearch_FindsTypo_GetUsr_ReturnsGetUser()
    {
        await IndexAsync("User.cs", "public class GetUser { }");

        // "GetUsr" has Levenshtein distance 1 from "GetUser"  — within threshold
        var results = _svc.Search("GetUsr");

        results.Should().Contain(r => r.Symbol.Name == "GetUser");
    }

    [Fact]
    public async Task FuzzySearch_ShortQuery_DoesNotTriggerFuzzy()
    {
        await IndexAsync("Short.cs", "public class AB { }");

        // queries < 3 chars should not trigger fuzzy (too noisy)
        var results = _svc.Search("XY");

        results.Should().NotContain(r => r.Symbol.Name == "AB");
    }

    [Fact]
    public async Task FuzzyScore_IsLower_ThanExactAndPrefixScore()
    {
        await IndexAsync("Mixed.cs", """
            public class GetUser { }
            public class GetUsers { }
            public class SomethingElse { }
            """);

        // Exact + prefix matches exist  — fuzzy result would be for a typo
        var exactResults = _svc.Search("GetUser");
        var fuzzyResults = _svc.Search("GetUsr");  // typo

        // fuzzy matches should have lower score than exact
        var exactScore = exactResults.First(r => r.Symbol.Name == "GetUser").RelevanceScore;
        var fuzzyResult = fuzzyResults.FirstOrDefault(r => r.Symbol.Name == "GetUser");

        // Must assert not-null — otherwise the test silently passes if fuzzy
        // search fails to find the symbol, defeating the purpose of the test.
        fuzzyResult.Should().NotBeNull("fuzzy search should find 'GetUser' for typo 'GetUsr'");
        fuzzyResult!.RelevanceScore.Should().BeLessThan(exactScore,
            "fuzzy score × 0.4 must be lower than exact score (1.0)");
    }

    [Fact]
    public async Task FuzzySearch_ExactMatchExists_NoFuzzyDuplicates()
    {
        await IndexAsync("Exact.cs", "public class GetUser { }");

        var results = _svc.Search("GetUser");

        results.Where(r => r.Symbol.Name == "GetUser").Should().HaveCount(1);
    }

    private async Task IndexAsync(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_tmpDir, fileName), content);
        await _svc.BuildIndexAsync(_tmpDir);
    }
}
