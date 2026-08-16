using System.Text;
using OneCode.Core.Tools;
using OneCode.App.Tools;
using NSubstitute;

namespace OneCode.Tests;

// WriteTool — create / overwrite file

public sealed class WriteToolTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "WriteToolTests_" + Guid.NewGuid().ToString("N")[..8]);

    public WriteToolTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    // basic write

    [Fact]
    public async Task Write_CreatesNewFile_WithContent()
    {
        var file = Path.Combine(_tmpDir, "new.cs");
        var result = await RunWriteAsync(file, "namespace X {}");

        result.Content.Should().NotStartWith("Error");
        (await File.ReadAllTextAsync(file)).Should().Be("namespace X {}");
    }

    [Fact]
    public async Task Write_CreatesParentDirectories_Automatically()
    {
        var file = Path.Combine(_tmpDir, "sub", "deep", "file.cs");
        var result = await RunWriteAsync(file, "// content");

        result.Content.Should().NotStartWith("Error");
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task Write_OverwritesExistingFile_WithNewContent()
    {
        var file = Path.Combine(_tmpDir, "overwrite.cs");
        await File.WriteAllTextAsync(file, "old content");

        await RunWriteAsync(file, "new content");

        (await File.ReadAllTextAsync(file)).Should().Be("new content");
    }

    // line-ending preservation

    [Fact]
    public async Task Write_PreservesCrlfLineEndings_WhenOverwritingCrlfFile()
    {
        var file = Path.Combine(_tmpDir, "crlf.cs");
        // Create file with CRLF endings
        await File.WriteAllBytesAsync(file,
            Encoding.UTF8.GetBytes("existing\r\ncontent\r\n"));

        // Write new content with LF endings (as LLM typically sends)
        await RunWriteAsync(file, "line1\nline2\nline3");

        var written = await File.ReadAllBytesAsync(file);
        var text = Encoding.UTF8.GetString(written);
        text.Should().Contain("\r\n", "CRLF must be preserved");
        text.Should().NotMatchRegex("(?<!\r)\n", "no bare LF should remain");
    }

    [Fact]
    public async Task Write_PreservesLfLineEndings_WhenOverwritingLfFile()
    {
        var file = Path.Combine(_tmpDir, "lf.cs");
        await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("existing\ncontent\n"));

        await RunWriteAsync(file, "line1\r\nline2\r\nline3");

        var written = await File.ReadAllBytesAsync(file);
        var text = Encoding.UTF8.GetString(written);
        text.Should().NotContain("\r\n", "bare CRLF must be normalised away");
    }

    [Fact]
    public async Task Write_PreservesUtf8Bom_WhenOverwritingBomFile()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var file = Path.Combine(_tmpDir, "bom.cs");
        await File.WriteAllBytesAsync(file, bom.Concat(Encoding.UTF8.GetBytes("old")).ToArray());

        await RunWriteAsync(file, "new content");

        var written = await File.ReadAllBytesAsync(file);
        written.Should().StartWith(bom, "UTF-8 BOM must be preserved");
    }

    [Fact]
    public async Task Write_NewFile_HasNoLineEndingTransformation()
    {
        // New files have no existing style — content written as-is (LF)
        var file = Path.Combine(_tmpDir, "newlf.cs");
        await RunWriteAsync(file, "line1\nline2");

        var text = await File.ReadAllTextAsync(file);
        text.Should().Be("line1\nline2");
    }

    // LSP notification

    [Fact]
    public async Task Write_NotifiesLspAfterSuccessfulWrite()
    {
        var file = Path.Combine(_tmpDir, "notify.cs");
        var notifier = Substitute.For<ILspNotifier>();

        var tool = new WriteTool(notifier, CreateWd(), ssh: null!);
        var result = await tool.WriteAsync(file, "class X {}", ct: TestContext.Current.CancellationToken);

        // Business output: the write must have succeeded and the file must exist
        result.Content.Should().NotStartWith("Error");
        (await File.ReadAllTextAsync(file)).Should().Be("class X {}");
        await notifier.Received(1).NotifyFileUpdatedAsync(file, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Write_ReturnsCharacterCount_InSuccessMessage()
    {
        var file = Path.Combine(_tmpDir, "count.cs");
        var content = "hello world";
        var result = await RunWriteAsync(file, content);

        result.Content.Should().NotStartWith("Error");
        result.Content.Should().Contain(content.Length.ToString(CultureInfo.InvariantCulture));
    }

    // Helpers

    private IWorkingDirectoryAccessor CreateWd()
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_tmpDir);
        return wd;
    }

    private async Task<ToolResult> RunWriteAsync(string path, string content)
    {
        var tool = new WriteTool(NoOpLspNotifier.Instance, CreateWd(), ssh: null!);
        return await tool.WriteAsync(path, content, ct: TestContext.Current.CancellationToken);
    }
}

// WriteTool — validation (P2)

public sealed class WriteToolValidationTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "WriteValidationTests_" + Guid.NewGuid().ToString("N")[..8]);

    public WriteToolValidationTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task Write_RejectsPathTraversal()
    {
        var result = await ExecAsync("../../../etc/passwd", "evil");
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Write_RejectsEmptyPath()
    {
        var result = await ExecAsync("", "something");
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Write_AcceptsNormalPath()
    {
        var file = Path.Combine(_tmpDir, "valid.cs");
        var result = await ExecAsync(file, "class X {}");
        result.Content.Should().NotStartWith("Error");
    }

    private async Task<ToolResult> ExecAsync(string path, string content)
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_tmpDir);
        var tool = new WriteTool(NoOpLspNotifier.Instance, wd, ssh: null!);
        return await tool.WriteAsync(path, content, ct: TestContext.Current.CancellationToken);
    }
}
