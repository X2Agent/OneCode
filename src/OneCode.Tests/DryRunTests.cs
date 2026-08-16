using System.Text;
using OneCode.Core.Tools;
using OneCode.App.Tools;
using NSubstitute;

namespace OneCode.Tests;

// EditTool + WriteTool — dry_run diff preview (P2)

public sealed class DryRunTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "DryRunTests_" + Guid.NewGuid().ToString("N")[..8]);

    public DryRunTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task Edit_DryRun_ReturnsDiff_WithoutModifyingFile()
    {
        var file = Write("a.cs", "var x = 1;\nvar y = 2;\n");
        var tool = new EditTool(NoOpLspNotifier.Instance, CreateWd(), ssh: null!);

        var result = await tool.EditAsync(file, "var x = 1;", "var x = 99;", dryRun: true, ct: TestContext.Current.CancellationToken);

        result.Content.Should().Contain("-var x = 1;");
        result.Content.Should().Contain("+var x = 99;");
        // File must be unchanged
        (await File.ReadAllTextAsync(file)).Should().Contain("var x = 1;");
    }

    [Fact]
    public async Task Write_DryRun_ReturnsDiff_WithoutModifyingFile()
    {
        var file = Write("b.cs", "line1\nline2\n");
        var tool = new WriteTool(NoOpLspNotifier.Instance, CreateWd(), ssh: null!);

        var result = await tool.WriteAsync(file, "line1\nlineX\n", dryRun: true, ct: TestContext.Current.CancellationToken);

        result.Content.Should().Contain("-line2");
        result.Content.Should().Contain("+lineX");
        // File unchanged
        (await File.ReadAllTextAsync(file)).Should().Contain("line2");
    }

    [Fact]
    public async Task Write_DryRun_NewFile_ReturnsAllInsertedLines()
    {
        var file = Path.Combine(_tmpDir, "new.cs");
        var tool = new WriteTool(NoOpLspNotifier.Instance, CreateWd(), ssh: null!);

        var result = await tool.WriteAsync(file, "namespace X {}", dryRun: true, ct: TestContext.Current.CancellationToken);

        result.Content.Should().Contain("+namespace X {}");
        File.Exists(file).Should().BeFalse("dry_run must not create file");
    }

    // Helpers

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private IWorkingDirectoryAccessor CreateWd()
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_tmpDir);
        return wd;
    }
}
