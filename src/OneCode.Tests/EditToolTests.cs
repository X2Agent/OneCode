using System.Text;
using OneCode.Core.Tools;
using OneCode.App.Tools;
using NSubstitute;

namespace OneCode.Tests;

// EditTool — search-and-replace safety guarantees

public sealed class EditToolTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "EditToolTests_" + Guid.NewGuid().ToString("N")[..8]);

    public EditToolTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    // exact match

    [Fact]
    public async Task Edit_ReplacesUniqueOccurrence_Successfully()
    {
        var file = Write("a.cs", "class Foo { }");
        var result = await RunEditAsync(file, "class Foo { }", "class Bar { }");

        result.Content.Should().NotStartWith("Error");
        (await File.ReadAllTextAsync(file)).Should().Be("class Bar { }");
    }

    [Fact]
    public async Task Edit_ZeroOccurrences_ReturnsError()
    {
        var file = Write("a.cs", "class Foo { }");
        var result = await RunEditAsync(file, "class MISSING { }", "class X { }");

        result.Content.Should().Contain("Could not find");
    }

    [Fact]
    public async Task Edit_MultipleOccurrences_ReturnsError()
    {
        var file = Write("a.cs", "int x = 1; int x = 1;");
        var result = await RunEditAsync(file, "int x = 1;", "int y = 2;");

        result.Content.Should().Contain("2 occurrences");
    }

    [Fact]
    public async Task Edit_FileNotFound_SuggestsAlternative()
    {
        // Write a file with a similar name so the "did you mean" logic fires
        Write("MyClass.cs", "// placeholder");

        var result = await RunEditAsync(
            Path.Combine(_tmpDir, "MyClazz.cs"), "old", "new");

        result.Content.Should().Contain("MyClass.cs");
    }

    // encoding/line-ending preservation

    [Fact]
    public async Task Edit_PreservesCrlfLineEndings()
    {
        var file = Write("crlf.cs", "line1\r\nFIND_ME\r\nline3");
        await RunEditAsync(file, "FIND_ME", "REPLACED");

        var bytes = await File.ReadAllBytesAsync(file);
        var text = Encoding.UTF8.GetString(bytes);
        // Replacement line must still use CRLF
        text.Should().Contain("REPLACED\r\n");
        text.Should().NotContain("REPLACED\n");
    }

    [Fact]
    public async Task Edit_PreservesUtf8BomEncoding()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var content = Encoding.UTF8.GetBytes("class BomClass { }");
        var fileBytes = bom.Concat(content).ToArray();
        var file = Path.Combine(_tmpDir, "bom.cs");
        await File.WriteAllBytesAsync(file, fileBytes);

        await RunEditAsync(file, "BomClass", "FixedClass");

        var written = await File.ReadAllBytesAsync(file);
        written.Should().StartWith(bom, "BOM must be preserved");
        Encoding.UTF8.GetString(written, 3, written.Length - 3).Should().Contain("FixedClass");
    }

    // LSP notification

    [Fact]
    public async Task Edit_NotifiesLspAfterSuccessfulEdit()
    {
        var file = Write("notify.cs", "var x = 1;");
        var notifier = Substitute.For<ILspNotifier>();

        var tool = new EditTool(notifier, CreateWd(), ssh: null!);
        var result = await tool.EditAsync(file, "var x = 1;", "var y = 2;", ct: TestContext.Current.CancellationToken);

        // Business output: the edit must have succeeded and the file must be modified
        result.Content.Should().NotStartWith("Error");
        (await File.ReadAllTextAsync(file)).Should().Be("var y = 2;");
        await notifier.Received(1).NotifyFileUpdatedAsync(file, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_DoesNotNotifyLsp_WhenEditFails()
    {
        var file = Write("no-notify.cs", "var x = 1;");
        var notifier = Substitute.For<ILspNotifier>();

        var tool = new EditTool(notifier, CreateWd(), ssh: null!);
        var result = await tool.EditAsync(file, "MISSING", "x", ct: TestContext.Current.CancellationToken);

        // Business output: the edit must have failed and the file must be unchanged
        result.Content.Should().Contain("Could not find");
        (await File.ReadAllTextAsync(file)).Should().Be("var x = 1;");
        await notifier.DidNotReceive().NotifyFileUpdatedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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

    private async Task<ToolResult> RunEditAsync(string path, string oldStr, string newStr)
    {
        var tool = new EditTool(NoOpLspNotifier.Instance, CreateWd(), ssh: null!);
        return await tool.EditAsync(path, oldStr, newStr, ct: TestContext.Current.CancellationToken);
    }
}

// EditTool — insert_after / insert_before modes (P3)

public sealed class EditInsertModeTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "EditInsertTests_" + Guid.NewGuid().ToString("N")[..8]);

    public EditInsertModeTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task Edit_InsertAfter_InsertsTextAfterAnchor_LeavingAnchorIntact()
    {
        var file = Write("a.cs",
            "using System;\n" +
            "using System.Linq;\n" +
            "\n" +
            "namespace X {}\n");

        var result = await ExecAsync(file,
            oldStr: "using System.Linq;",
            newStr: "\nusing System.Collections.Generic;",
            mode: "insert_after");

        result.Content.Should().NotStartWith("Error");
        var text = await File.ReadAllTextAsync(file);
        text.Should().Contain("using System.Linq;\nusing System.Collections.Generic;",
            "anchor must be preserved and new text inserted after it");
    }

    [Fact]
    public async Task Edit_InsertBefore_InsertsTextBeforeAnchor_LeavingAnchorIntact()
    {
        var file = Write("b.cs",
            "public class Foo\n" +
            "{\n" +
            "    public void Bar() {}\n" +
            "}\n");

        var result = await ExecAsync(file,
            oldStr: "    public void Bar()",
            newStr: "    public void Prepended() {}\n\n",
            mode: "insert_before");

        result.Content.Should().NotStartWith("Error");
        var text = await File.ReadAllTextAsync(file);
        text.Should().Contain("public void Prepended() {}\n\n    public void Bar()",
            "new method must appear before anchor; anchor preserved");
    }

    [Fact]
    public async Task Edit_DefaultMode_StillReplacesAnchor()
    {
        var file = Write("c.cs", "var x = OLD;\n");

        var result = await ExecAsync(file, "var x = OLD;", "var x = NEW;", "replace");

        result.Content.Should().NotStartWith("Error");
        (await File.ReadAllTextAsync(file)).Should().Be("var x = NEW;\n");
    }

    [Fact]
    public async Task Edit_InsertAfter_DryRun_DoesNotWrite()
    {
        var file = Write("d.cs", "line1\nline2\n");

        var result = await ExecAsync(file,
            oldStr: "line1",
            newStr: "\nINSERTED",
            mode: "insert_after",
            dryRun: true);

        result.Content.Should().Contain("+++");
        (await File.ReadAllTextAsync(file)).Should().Be("line1\nline2\n",
            "dry_run must not modify the file");
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

    private async Task<ToolResult> ExecAsync(string path, string oldStr, string newStr,
        string mode = "replace", bool dryRun = false)
    {
        var tool = new EditTool(NoOpLspNotifier.Instance, CreateWd(), ssh: null!);
        return await tool.EditAsync(path, oldStr, newStr, mode: mode, dryRun: dryRun, ct: TestContext.Current.CancellationToken);
    }
}
