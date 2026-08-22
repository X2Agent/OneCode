using OneCode.Infrastructure;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="CodeIndexService"/> — symbol extraction and search.
/// C# methods are indexed via _msDotNetMethodPattern.
/// </summary>
public sealed class CodeIndexServiceTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "CodeIndexTests_" + Guid.NewGuid().ToString("N")[..8]);

    public CodeIndexServiceTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    // C# type declarations

    [Fact]
    public async Task Search_CSharpClass_IsIndexed()
    {
        await IndexContentAsync("MyClass.cs", """
            public class MyClass
            {
            }
            """);

        var results = Search("MyClass");
        results.Should().ContainSingle(r => r.Symbol.Name == "MyClass" && r.Symbol.Kind == "class");
    }

    [Fact]
    public async Task Search_CSharpInterface_IsIndexed()
    {
        await IndexContentAsync("IService.cs", "public interface IService { }");

        Search("IService").Should().ContainSingle(r => r.Symbol.Kind == "interface");
    }

    [Fact]
    public async Task Search_CSharpEnum_IsIndexed()
    {
        await IndexContentAsync("Status.cs", "public enum Status { Active, Inactive }");

        Search("Status").Should().ContainSingle(r => r.Symbol.Kind == "enum");
    }

    [Fact]
    public async Task Search_CSharpRecord_IsIndexed()
    {
        await IndexContentAsync("Point.cs", "public record Point(int X, int Y);");

        Search("Point").Should().ContainSingle(r => r.Symbol.Kind == "record");
    }

    // C# method declarations

    [Fact]
    public async Task Search_CSharpPublicMethod_IsIndexed()
    {
        await IndexContentAsync("Calc.cs", """
            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """);

        var methods = Search("Add");
        methods.Should().Contain(r => r.Symbol.Name == "Add" && r.Symbol.Kind == "method",
            because: "C# method declarations must be indexed via _msDotNetMethodPattern");
    }

    [Fact]
    public async Task Search_CSharpAsyncMethod_IsIndexed()
    {
        await IndexContentAsync("Service.cs", """
            public class Service
            {
                public async Task<string> GetDataAsync(string id) { return id; }
            }
            """);

        Search("GetDataAsync").Should().Contain(r => r.Symbol.Name == "GetDataAsync");
    }

    [Fact]
    public async Task Search_CSharpStaticMethod_IsIndexed()
    {
        await IndexContentAsync("Helper.cs", """
            public static class Helper
            {
                public static string Format(string input) => input;
            }
            """);

        Search("Format").Should().Contain(r => r.Symbol.Name == "Format");
    }

    [Fact]
    public async Task Search_CSharpPrivateMethod_IsIndexed()
    {
        await IndexContentAsync("Svc.cs", """
            public class Svc
            {
                private void InternalProcess() { }
            }
            """);

        Search("InternalProcess").Should().Contain(r => r.Symbol.Name == "InternalProcess");
    }

    [Fact]
    public async Task Search_CSharpOverrideMethod_IsIndexed()
    {
        await IndexContentAsync("Derived.cs", """
            public class Derived : Base
            {
                public override string ToString() => "Derived";
            }
            """);

        Search("ToString").Should().Contain(r => r.Symbol.Name == "ToString");
    }

    // TypeScript / JavaScript

    [Fact]
    public async Task Search_TypeScriptClass_IsIndexed()
    {
        await IndexContentAsync("app.ts", "export class AppService { }");

        Search("AppService").Should().ContainSingle(r => r.Symbol.Kind == "class");
    }

    [Fact]
    public async Task Search_TypeScriptFunction_IsIndexed()
    {
        await IndexContentAsync("utils.ts", "export function formatDate(d: Date): string { return ''; }");

        Search("formatDate").Should().ContainSingle(r => r.Symbol.Name == "formatDate");
    }

    [Fact]
    public async Task Search_TypeScriptConstArrowFunction_IsIndexed()
    {
        await IndexContentAsync("handler.ts", "const handleClick = (event: MouseEvent) => { };");

        Search("handleClick").Should().Contain(r => r.Symbol.Name == "handleClick",
            because: "const arrow functions must be indexed via _msTypeScriptArrowPattern");
    }

    [Fact]
    public async Task Search_TypeScriptExportedAsyncArrow_IsIndexed()
    {
        await IndexContentAsync("api.ts", "export const fetchUser = async (id: string) => { };");

        Search("fetchUser").Should().Contain(r => r.Symbol.Name == "fetchUser");
    }

    // Search scoring / ranking

    [Fact]
    public async Task Search_ExactMatch_HasHighestScore()
    {
        await IndexContentAsync("Mix.cs", """
            public class UserService { }
            public class UserServiceExtensions { }
            """);

        var results = Search("UserService");
        results[0].Symbol.Name.Should().Be("UserService");
        results[0].RelevanceScore.Should().Be(1.0);
    }

    [Fact]
    public async Task Search_PrefixMatch_HasMediumScore()
    {
        await IndexContentAsync("Services.cs", """
            public class OrderService { }
            public class OrderServiceFactory { }
            """);

        var results = Search("OrderService");
        var factory = results.FirstOrDefault(r => r.Symbol.Name == "OrderServiceFactory");
        factory.Should().NotBeNull();
        factory!.RelevanceScore.Should().Be(0.8);
    }

    [Fact]
    public async Task Search_SubstringMatch_HasLowScore()
    {
        await IndexContentAsync("Things.cs", "public class PaymentProcessor { }");

        var results = Search("Processor");
        results.Should().Contain(r => r.Symbol.Name == "PaymentProcessor" && r.RelevanceScore == 0.5);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmpty()
    {
        await IndexContentAsync("A.cs", "public class A { }");

        Search("").Should().BeEmpty();
        Search("   ").Should().BeEmpty();
    }

    [Fact]
    public async Task Search_MaxResults_IsRespected()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"public class Thing{i} {{ }}"));
        await IndexContentAsync("Things.cs", lines);

        Search("Thing", maxResults: 5).Should().HaveCount(5);
    }

    // Incremental update

    [Fact]
    public async Task UpdateFilesAsync_AddNewSymbols_FindableAfterUpdate()
    {
        var svc = new CodeIndexService();
        var file = WriteFile("New.cs", "public class NewClass { }");

        await svc.UpdateFilesAsync([file]);

        svc.Search("NewClass").Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateFilesAsync_RemoveFile_SymbolsDisappear()
    {
        var svc = new CodeIndexService();
        var file = WriteFile("Gone.cs", "public class GoneClass { }");

        await svc.UpdateFilesAsync([file]);
        svc.Search("GoneClass").Should().ContainSingle();

        // Simulate file deletion
        File.Delete(file);
        await svc.UpdateFilesAsync([], removedFiles: [file]);
        svc.Search("GoneClass").Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateFilesAsync_ModifyFile_OldSymbolReplaced()
    {
        var svc = new CodeIndexService();
        var file = WriteFile("Evolving.cs", "public class OldName { }");
        await svc.UpdateFilesAsync([file]);

        // Replace file content with new symbol name
        File.WriteAllText(file, "public class NewName { }");
        await svc.UpdateFilesAsync([file]);

        svc.Search("OldName").Should().BeEmpty();
        svc.Search("NewName").Should().ContainSingle();
    }

    // Line number accuracy

    [Fact]
    public async Task Search_LineNumber_IsCorrect()
    {
        await IndexContentAsync("Lined.cs", """
            // line 1 comment
            // line 2 comment
            public class LineThreeClass { }
            """);

        var match = Search("LineThreeClass").Should().ContainSingle().Which;
        match.Symbol.Line.Should().Be(3);
    }

    // SymbolCount and Clear

    [Fact]
    public async Task Clear_RemovesAllSymbols()
    {
        var svc = new CodeIndexService();
        var file = WriteFile("S.cs", "public class S { }");
        await svc.UpdateFilesAsync([file]);

        svc.SymbolCount.Should().BeGreaterThan(0);
        svc.Clear();
        svc.SymbolCount.Should().Be(0);
    }

    // Helpers

    private readonly CodeIndexService _svc = new();

    private async Task IndexContentAsync(string filename, string content)
    {
        WriteFile(filename, content);
        await _svc.BuildIndexAsync(_tmpDir);
    }

    private string WriteFile(string filename, string content)
    {
        var path = Path.Combine(_tmpDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    private IReadOnlyList<OneCode.Infrastructure.Abstractions.CodeSymbolMatch> Search(
        string query, int maxResults = 50) => _svc.Search(query, maxResults);
}

// CodeIndexService — kind filter, path scope, prefix search, fuzzy search
// (migrated from CodeSearchFeatureTests)

public sealed class CodeIndexServiceFilterTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "CodeIndexFilter_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly CodeIndexService _svc = new();

    public CodeIndexServiceFilterTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task Search_KindFilter_Interface_ReturnsOnlyInterfaces()
    {
        await IndexAsync("Mixed.cs", """
            public class MyService { }
            public interface IMyService { }
            public enum MyStatus { Active }
            """);

        var interfaces = _svc.Search("My", kindFilter: "interface");
        interfaces.Should().OnlyContain(r => r.Symbol.Kind == "interface");
        interfaces.Should().Contain(r => r.Symbol.Name == "IMyService");
    }

    [Fact]
    public async Task Search_KindFilter_Class_ReturnsOnlyClasses()
    {
        await IndexAsync("Mixed.cs", """
            public class MyService { }
            public interface IMyService { }
            public enum MyStatus { Active }
            """);

        var classes = _svc.Search("My", kindFilter: "class");
        classes.Should().OnlyContain(r => r.Symbol.Kind == "class");
    }

    [Theory]
    [InlineData("CLASS")]
    [InlineData("Class")]
    [InlineData("class")]
    public async Task Search_KindFilter_CaseInsensitive(string kindFilter)
    {
        await IndexAsync("Types.cs", "public class Foo { }");

        _svc.Search("Foo", kindFilter: kindFilter).Should().ContainSingle();
    }

    [Fact]
    public async Task Search_KindFilter_MethodsOnly()
    {
        await IndexAsync("Service.cs", """
            public class DataService
            {
                public string GetData(int id) { return ""; }
                public void SaveData(string data) { }
            }
            """);

        var methods = _svc.Search("Data", kindFilter: "method");
        methods.Should().OnlyContain(r => r.Symbol.Kind == "method");
        methods.Select(r => r.Symbol.Name).Should().Contain("GetData");
    }

    [Fact]
    public async Task Search_KindFilter_NoMatch_ReturnsEmpty()
    {
        await IndexAsync("OnlyClass.cs", "public class Alpha { }");

        // Search for Alpha but filtered to methods  — should return empty
        _svc.Search("Alpha", kindFilter: "method").Should().BeEmpty();
    }

    [Fact]
    public async Task Search_PathScope_LimitsToDirectory()
    {
        // Create two subdirectories with symbols of the same name
        Directory.CreateDirectory(Path.Combine(_tmpDir, "src"));
        Directory.CreateDirectory(Path.Combine(_tmpDir, "tests"));

        File.WriteAllText(Path.Combine(_tmpDir, "src", "Service.cs"), "public class SharedName { }");
        File.WriteAllText(Path.Combine(_tmpDir, "tests", "ServiceTests.cs"), "public class SharedName { }");

        await _svc.BuildIndexAsync(_tmpDir);

        var srcPath = Path.Combine(_tmpDir, "src");
        var srcOnly = _svc.Search("SharedName", pathScope: srcPath);

        srcOnly.Should().AllSatisfy(r =>
            r.Symbol.FilePath.Should().StartWith(srcPath, because: "path scope filter must be applied"));
        srcOnly.Should().HaveCount(1);
    }

    [Fact]
    public async Task Search_PathScope_NullReturnsAll()
    {
        Directory.CreateDirectory(Path.Combine(_tmpDir, "a"));
        Directory.CreateDirectory(Path.Combine(_tmpDir, "b"));
        File.WriteAllText(Path.Combine(_tmpDir, "a", "A.cs"), "public class Identical { }");
        File.WriteAllText(Path.Combine(_tmpDir, "b", "B.cs"), "public class Identical { }");

        await _svc.BuildIndexAsync(_tmpDir);

        _svc.Search("Identical", pathScope: null).Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_KindAndPathScope_BothFiltersApplied()
    {
        Directory.CreateDirectory(Path.Combine(_tmpDir, "domain"));
        File.WriteAllText(Path.Combine(_tmpDir, "domain", "Entity.cs"), """
            public class Entity { }
            public interface IEntity { }
            """);
        File.WriteAllText(Path.Combine(_tmpDir, "Other.cs"), "public class Entity { }");

        await _svc.BuildIndexAsync(_tmpDir);

        var domainPath = Path.Combine(_tmpDir, "domain");
        var results = _svc.Search("Entity", kindFilter: "interface", pathScope: domainPath);

        results.Should().ContainSingle();
        results[0].Symbol.Name.Should().Be("IEntity");
    }

    // Helpers

    private async Task IndexAsync(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_tmpDir, fileName), content);
        await _svc.BuildIndexAsync(_tmpDir);
    }
}

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
    public async Task ExactMatch_HasHigherScore_ThanPrefixMatch()
    {
        await IndexAsync("Exact.cs", """
            public class Get { }
            public class GetUser { }
            """);

        var results = _svc.Search("Get");

        var exactResult = results.First(r => r.Symbol.Name == "Get");
        var prefixResult = results.First(r => r.Symbol.Name == "GetUser");
        exactResult.RelevanceScore.Should().BeGreaterThan(prefixResult.RelevanceScore);
    }

    [Fact]
    public async Task SubstringSearch_MatchesMiddleOfName()
    {
        await IndexAsync("Substr.cs", """
            public class IUserRepositoryFactory { }
            """);

        var results = _svc.Search("Repository");

        results.Should().Contain(r => r.Symbol.Name == "IUserRepositoryFactory");
    }

    [Fact]
    public async Task Clear_ResetsSortedKeys()
    {
        await IndexAsync("X.cs", "public class ClearMe { }");

        _svc.Clear();

        _svc.Search("ClearMe").Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateFiles_RebuildsSortedKeys()
    {
        // Start with one symbol
        await IndexAsync("A.cs", "public class OldSymbol { }");
        _svc.Search("OldSymbol").Should().NotBeEmpty();

        // Add a new file with a different symbol
        var newFile = Path.Combine(_tmpDir, "B.cs");
        File.WriteAllText(newFile, "public class NewSymbol { }");
        await _svc.UpdateFilesAsync(new[] { newFile });

        // Both should now be findable
        _svc.Search("NewSymbol").Should().NotBeEmpty();
        _svc.Search("OldSymbol").Should().NotBeEmpty();
    }

    // Helpers

    private async Task IndexAsync(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_tmpDir, fileName), content);
        await _svc.BuildIndexAsync(_tmpDir);
    }
}

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
    public static void LevenshteinDistance_KnownValues()
    {
        // "" to "abc" = 3
        OneCode.Core.Text.StringDistance.Levenshtein("", "abc").Should().Be(3);
        // same string = 0
        OneCode.Core.Text.StringDistance.Levenshtein("hello", "hello").Should().Be(0);
        // single substitution
        OneCode.Core.Text.StringDistance.Levenshtein("GetUsr", "GetUser").Should().Be(1);
        // single deletion
        OneCode.Core.Text.StringDistance.Levenshtein("GetUserr", "GetUser").Should().Be(1);
        // two edits
        OneCode.Core.Text.StringDistance.Levenshtein("GtUsr", "GetUser").Should().Be(2);
    }

    [Fact]
    public async Task FuzzySearch_ExactMatchExists_NoFuzzyDuplicates()
    {
        await IndexAsync("Exact.cs", "public class GetUser { }");

        // Exact match wins; fuzzy should not add the same symbol twice
        var results = _svc.Search("GetUser");

        results.Where(r => r.Symbol.Name == "GetUser").Should().HaveCount(1);
    }

    // Helpers

    private async Task IndexAsync(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_tmpDir, fileName), content);
        await _svc.BuildIndexAsync(_tmpDir);
    }
}
