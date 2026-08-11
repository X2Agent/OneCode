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
