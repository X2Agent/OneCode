using OneCode.Infrastructure;

namespace OneCode.Tests;

// CodeIndexService — kind filter and path scope

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

        _svc.Search("Alpha", kindFilter: "method").Should().BeEmpty();
    }

    [Fact]
    public async Task Search_PathScope_LimitsToDirectory()
    {
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

    private async Task IndexAsync(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_tmpDir, fileName), content);
        await _svc.BuildIndexAsync(_tmpDir);
    }
}
