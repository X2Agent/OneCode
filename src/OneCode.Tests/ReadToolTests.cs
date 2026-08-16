using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;
using OneCode.Infrastructure;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="ReadTool"/> — covers path-safety boundary checks,
/// offset/limit pagination, binary detection, large-file streaming
/// (ReadTool uses streaming instead of the 10 MB hard limit enforced
/// by Edit/Write/Inspection tools), output truncation, and error handling.
/// </summary>
public sealed class ReadToolTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _projectDir;
    private readonly string _outsideDir;

    public ReadToolTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"ReadToolTests_{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_sandboxDir, "project");
        _outsideDir = Path.Combine(_sandboxDir, "outside");
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxDir, recursive: true); } catch { /* best effort */ }
    }

    private IWorkingDirectoryAccessor CreateWd(string? workingDir = null, IReadOnlyList<string>? additionalDirs = null)
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(workingDir ?? _projectDir);
        wd.AdditionalDirectories.Returns(additionalDirs ?? Array.Empty<string>());
        return wd;
    }

    private ReadTool CreateTool(string? workingDir = null, IReadOnlyList<string>? additionalDirs = null)
        => new(CreateWd(workingDir, additionalDirs), ssh: null!);

    private string WriteFile(string relativeName, string content)
    {
        var path = Path.Combine(_projectDir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task WriteBytesAsync(string path, long byteCount, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 8192, useAsync: true);
        var chunkSize = 64 * 1024;
        var buffer = new byte[chunkSize];
        var remaining = byteCount;
        while (remaining > 0)
        {
            var toWrite = (int)Math.Min(remaining, chunkSize);
            await stream.WriteAsync(buffer.AsMemory(0, toWrite), ct);
            remaining -= toWrite;
        }
    }

    private static void AssertRejected(ToolResult result)
    {
        result.Content.Should().StartWith("Error:");
        result.Content.Should().Match(s => s.Contains("outside the working directory")
                                || s.Contains("protected system directory")
                                || s.Contains("Access denied"),
            "path must be rejected for traversal or protected-dir access");
    }

    // Path safety

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("../../outside/secret.txt")]
    [InlineData("../outside/file.txt")]
    public async Task ReadAsync_TraversalPath_ReturnsError(string traversal)
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("hello.txt", "hello world");
        var tool = CreateTool();

        var result = await tool.ReadAsync(traversal, ct: ct);

        AssertRejected(result);
    }

    [Fact]
    public async Task ReadAsync_AbsolutePathOutsideWorkingDir_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var outsideFile = Path.Combine(_outsideDir, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "secret content", ct);
        var tool = CreateTool();

        var result = await tool.ReadAsync(outsideFile, ct: ct);

        AssertRejected(result);
    }

    // Additional directories (/add-dir)

    [Fact]
    public async Task ReadAsync_PathInsideAdditionalDir_ReturnsContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var addDir = Path.Combine(_sandboxDir, "added");
        Directory.CreateDirectory(addDir);
        var externalFile = Path.Combine(addDir, "external.txt");
        await File.WriteAllTextAsync(externalFile, "from add-dir", ct);
        var tool = CreateTool(additionalDirs: new[] { addDir });

        var result = await tool.ReadAsync(externalFile, ct: ct);

        result.Content.Should().NotStartWith("Error:");
        result.Content.Should().Contain("from add-dir");
    }

    [Fact]
    public async Task ReadAsync_PathOutsideWorkingDirAndAdditionalDir_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var addDir = Path.Combine(_sandboxDir, "added");
        Directory.CreateDirectory(addDir);
        // _outsideDir is neither the working dir nor in additionalDirs
        var outsideFile = Path.Combine(_outsideDir, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "secret", ct);
        var tool = CreateTool(additionalDirs: new[] { addDir });

        var result = await tool.ReadAsync(outsideFile, ct: ct);

        AssertRejected(result);
    }

    [Fact]
    public async Task ReadAsync_PathInsideWorkingDir_ReturnsContent()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("hello.txt", "hello world");
        var tool = CreateTool();

        var result = await tool.ReadAsync("hello.txt", ct: ct);

        result.Content.Should().NotStartWith("Error:");
        result.Content.Should().Contain("hello world");
        result.Content.Should().Contain("File:");
        result.Content.Should().Contain("Lines 1-1 of 1:");
    }

    [Fact]
    public async Task ReadAsync_MissingFileInsideWorkingDir_ReturnsFileNotFoundError()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = CreateTool();

        var result = await tool.ReadAsync("nonexistent.txt", ct: ct);

        result.Content.Should().StartWith("Error:");
        result.Content.Should().Contain("File not found");
    }

    // Offset / Limit pagination

    [Fact]
    public async Task ReadAsync_OffsetStartsAtSpecifiedLine()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("multi.txt", "line1\nline2\nline3\nline4\nline5");
        var tool = CreateTool();

        var result = await tool.ReadAsync("multi.txt", offset: 3, limit: 2, ct: ct);

        result.Content.Should().Contain("line3");
        result.Content.Should().Contain("line4");
        result.Content.Should().NotContain("line2");
        result.Content.Should().NotContain("line5");
        result.Content.Should().Contain("Lines 3-4 of 5:");
    }

    [Fact]
    public async Task ReadAsync_LimitRestrictsLineCount()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("many.txt", "a\nb\nc\nd\ne\nf\ng\nh");
        var tool = CreateTool();

        var result = await tool.ReadAsync("many.txt", offset: 1, limit: 3, ct: ct);

        result.Content.Should().Contain("Lines 1-3 of 8:");
        result.Content.Should().Contain("additional lines were omitted");
    }

    [Fact]
    public async Task ReadAsync_NegativeOffset_ClampedToOne()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("clamp.txt", "first\nsecond");
        var tool = CreateTool();

        var result = await tool.ReadAsync("clamp.txt", offset: -5, limit: 1, ct: ct);

        result.Content.Should().NotStartWith("Error:");
        result.Content.Should().Contain("first");
        result.Content.Should().Contain("Lines 1-1 of 2:");
    }

    [Fact]
    public async Task ReadAsync_ZeroLimit_ClampedToOne()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("clamp.txt", "first\nsecond");
        var tool = CreateTool();

        var result = await tool.ReadAsync("clamp.txt", offset: 1, limit: 0, ct: ct);

        result.Content.Should().NotStartWith("Error:");
        result.Content.Should().Contain("first");
    }

    [Fact]
    public async Task ReadAsync_LargeFile_AddsLargeFileNotice()
    {
        var ct = TestContext.Current.CancellationToken;
        // LargeFileLineThreshold = 400 — create 500 lines to trigger the notice
        var lines = Enumerable.Range(1, 500).Select(i => $"line{i}").ToArray();
        WriteFile("large.txt", string.Join("\n", lines));
        var tool = CreateTool();

        var result = await tool.ReadAsync("large.txt", offset: 1, limit: 10, ct: ct);

        result.Content.Should().Contain("file has 500 lines");
        result.Content.Should().Contain("Use offset and limit");
    }

    [Fact]
    public async Task ReadAsync_FullFileRead_NoOmittedLinesNotice()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("small.txt", "a\nb\nc");
        var tool = CreateTool();

        var result = await tool.ReadAsync("small.txt", offset: 1, limit: 10, ct: ct);

        result.Content.Should().NotContain("additional lines were omitted");
        result.Content.Should().Contain("Lines 1-3 of 3:");
    }

    // Binary detection

    [Fact]
    public async Task ReadAsync_BinaryFile_ReturnsBinaryError()
    {
        var ct = TestContext.Current.CancellationToken;
        var binPath = Path.Combine(_projectDir, "binary.dat");
        // Write bytes that include a null byte (0x00) which triggers binary detection
        var bytes = new byte[] { 0x41, 0x42, 0x00, 0x43, 0x44 };
        await File.WriteAllBytesAsync(binPath, bytes, ct);
        var tool = CreateTool();

        var result = await tool.ReadAsync("binary.dat", ct: ct);

        result.Content.Should().StartWith("Error:");
        result.Content.Should().Contain("Cannot read binary file");
        result.Content.Should().Contain("binary.dat");
    }

    // Large file streaming

    [Fact]
    public async Task ReadAsync_FileLargerThan10MB_StreamingReadSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        // ReadTool uses streaming (ReadLinesStreamingAsync) instead of a hard 10 MB limit.
        // Files larger than PathsHelper.MaxFileReadSize (10 MB) — which EditTool/WriteTool
        // reject — must still be readable via offset/limit pagination.
        var bigPath = Path.Combine(_projectDir, "huge.txt");
        var lineContent = new string('x', 100) + "\n"; // 101 bytes per line
        var targetSize = PathsHelper.MaxFileReadSize + 1024; // just over 10 MB
        var lineCount = (int)(targetSize / 101);
        await WriteBytesAsync(bigPath, targetSize, ct);
        // Overwrite with line-structured content so ReadLineAsync yields predictable lines
        await using (var fs = new FileStream(bigPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 8192, useAsync: true))
        {
            for (var i = 0; i < lineCount; i++)
                await fs.WriteAsync(System.Text.Encoding.UTF8.GetBytes(lineContent), ct);
        }
        var tool = CreateTool();

        var result = await tool.ReadAsync("huge.txt", offset: 1, limit: 5, ct: ct);

        result.Content.Should().NotStartWith("Error:");
        result.Content.Should().Contain("Lines 1-5 of");
        result.Content.Should().Contain("file has");
    }

    [Fact]
    public async Task ReadAsync_OutputExceedingMaxChars_Truncated()
    {
        var ct = TestContext.Current.CancellationToken;
        // MaxResultSizeChars = 20_000. Create 200 lines of 200 chars each.
        // Formatted output per line: ~5 (line no) + 2 (arrow+space) + 200 (content) = ~207 chars.
        // 200 * 207 = 41_400 > 20_000 → truncation triggers.
        var longLine = new string('A', 200);
        var lines = Enumerable.Range(1, 200).Select(_ => longLine).ToArray();
        WriteFile("longlines.txt", string.Join("\n", lines));
        var tool = CreateTool();

        var result = await tool.ReadAsync("longlines.txt", offset: 1, limit: 200, ct: ct);

        result.Content.Should().Contain("[Output truncated at 20000 chars]");
    }

    // Line number formatting

    [Fact]
    public async Task ReadAsync_LineNumbers_PaddedWithArrowSeparator()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("fmt.txt", "first\nsecond\nthird");
        var tool = CreateTool();

        var result = await tool.ReadAsync("fmt.txt", ct: ct);

        // Line numbers are right-aligned (padded) and followed by "→ "
        result.Content.Should().Contain("1→ first");
        result.Content.Should().Contain("2→ second");
        result.Content.Should().Contain("3→ third");
    }

    [Fact]
    public async Task ReadAsync_MultiDigitLineNumbers_PaddedToSameWidth()
    {
        var ct = TestContext.Current.CancellationToken;
        var lines = Enumerable.Range(1, 12).Select(i => $"line{i}").ToArray();
        WriteFile("pad.txt", string.Join("\n", lines));
        var tool = CreateTool();

        var result = await tool.ReadAsync("pad.txt", ct: ct);

        // Lines 1-9 should be padded to width 2 (since max line number 12 has 2 digits)
        result.Content.Should().Contain(" 1→ line1");
        result.Content.Should().Contain("12→ line12");
    }
}
