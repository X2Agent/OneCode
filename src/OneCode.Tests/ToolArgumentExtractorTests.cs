using System.Text.Json;
using Microsoft.Extensions.AI;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// <see cref="ToolArgumentExtractor"/> 单元测试。
///
/// 覆盖以下两类场景：
/// 1. 类型错误：仅判 <c>arguments is JsonElement</c>，而运行时
///    <c>ctx.Arguments</c> 实际是 <c>AIFunctionArguments</c>（继承自
///    <c>Dictionary&lt;string, object?&gt;</c>），导致提取逻辑根本进不去。
/// 2. key 不一致：工具参数名是 <c>filePath</c>，
///    而提取方查 <c>path</c>/<c>file_path</c>，名字对不上。
///
/// 本测试集覆盖上述两类场景。
/// </summary>
public sealed class ToolArgumentExtractorTests
{
    // ExtractFilePath: 字典入参（运行时主路径，AIFunctionArguments）

    [Fact]
    public void ExtractFilePath_AIFunctionArguments_WithFilePathKey_ReturnsPath()
    {
        // 模拟运行时中间件收到的 ctx.Arguments：AIFunctionArguments 字典
        // 工具实际参数名是 filePath（WriteTool/EditTool/ReadTool 的 C# 参数名）
        var args = new AIFunctionArguments
        {
            ["filePath"] = "/workspace/src/Program.cs",
            ["content"] = "xxx",
        };

        var path = ToolArgumentExtractor.ExtractFilePath(args);

        path.Should().Be("/workspace/src/Program.cs");
    }

    [Fact]
    public void ExtractFilePath_AIFunctionArguments_WithJsonElementStringValue_ReturnsPath()
    {
        // AIFunctionArguments 的值也可能是 JsonElement（MAF 反序列化路径）
        using var pathDoc = JsonDocument.Parse("\"/workspace/foo.cs\"");
        var args = new AIFunctionArguments
        {
            ["filePath"] = pathDoc.RootElement.Clone(),
        };

        var path = ToolArgumentExtractor.ExtractFilePath(args);

        path.Should().Be("/workspace/foo.cs");
    }

    [Fact]
    public void ExtractFilePath_PlainDictionary_WithFilePathKey_ReturnsPath()
    {
        // 普通字典也应支持（IDictionary<string, object?> 接口路径）
        IDictionary<string, object?> args = new Dictionary<string, object?>
        {
            ["filePath"] = "/workspace/bar.cs",
        };

        var path = ToolArgumentExtractor.ExtractFilePath(args);

        path.Should().Be("/workspace/bar.cs");
    }

    [Fact]
    public void ExtractFilePath_AIFunctionArguments_WithFileUnderscorePathKey_ReturnsPath()
    {
        // MCP 工具使用 file_path（snake_case 约定）
        var args = new AIFunctionArguments
        {
            ["file_path"] = "/workspace/mcp.cs",
        };

        var path = ToolArgumentExtractor.ExtractFilePath(args);

        path.Should().Be("/workspace/mcp.cs");
    }

    [Fact]
    public void ExtractFilePath_AIFunctionArguments_FilePathTakesPrecedenceOverFileUnderscorePath()
    {
        // 同时存在 filePath 和 file_path 时，优先返回 filePath（工具实际参数名）
        var args = new AIFunctionArguments
        {
            ["filePath"] = "/workspace/primary.cs",
            ["file_path"] = "/workspace/secondary.cs",
        };

        var path = ToolArgumentExtractor.ExtractFilePath(args);

        path.Should().Be("/workspace/primary.cs");
    }

    [Fact]
    public void ExtractFilePath_AIFunctionArguments_WithoutAnyPathKey_ReturnsNull()
    {
        var args = new AIFunctionArguments
        {
            ["command"] = "dotnet build",
        };

        var path = ToolArgumentExtractor.ExtractFilePath(args);

        path.Should().BeNull();
    }

    [Fact]
    public void ExtractFilePath_NullArguments_ReturnsNull()
    {
        var path = ToolArgumentExtractor.ExtractFilePath(null);

        path.Should().BeNull();
    }

    // ExtractFilePath: JsonElement 入参

    [Fact]
    public void ExtractFilePath_JsonElement_WithFilePathKey_ReturnsPath()
    {
        using var doc = JsonDocument.Parse(@"{""filePath"":""/w/x.cs""}");
        var el = doc.RootElement.Clone();

        var path = ToolArgumentExtractor.ExtractFilePath(el);

        path.Should().Be("/w/x.cs");
    }

    [Fact]
    public void ExtractFilePath_JsonElement_NonObject_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("\"just a string\"");
        var el = doc.RootElement.Clone();

        var path = ToolArgumentExtractor.ExtractFilePath(el);

        path.Should().BeNull();
    }

    // ExtractInputString: 权限规则匹配用的输入字符串

    [Fact]
    public void ExtractInputString_BashTool_ReturnsCommand()
    {
        using var doc = JsonDocument.Parse(@"{""command"":""git status""}");
        var input = doc.RootElement.Clone();

        var result = ToolArgumentExtractor.ExtractInputString("Bash", input);

        result.Should().Be("git status");
    }

    [Fact]
    public void ExtractInputString_PowerShellTool_ReturnsCommand()
    {
        using var doc = JsonDocument.Parse(@"{""command"":""Get-Process""}");
        var input = doc.RootElement.Clone();

        var result = ToolArgumentExtractor.ExtractInputString("PowerShell", input);

        result.Should().Be("Get-Process");
    }

    [Fact]
    public void ExtractInputString_WriteTool_ReturnsFilePath()
    {
        // WriteTool 参数名是 filePath，旧实现查 file_path 导致返回 null
        using var doc = JsonDocument.Parse(@"{""filePath"":""/etc/passwd"",""content"":""x""}");
        var input = doc.RootElement.Clone();

        var result = ToolArgumentExtractor.ExtractInputString("Write", input);

        result.Should().Be("/etc/passwd");
    }

    [Fact]
    public void ExtractInputString_EditTool_ReturnsFilePath()
    {
        using var doc = JsonDocument.Parse(@"{""filePath"":""/w/a.cs"",""oldString"":""x""}");
        var input = doc.RootElement.Clone();

        var result = ToolArgumentExtractor.ExtractInputString("Edit", input);

        result.Should().Be("/w/a.cs");
    }

    [Fact]
    public void ExtractInputString_ReadTool_ReturnsFilePath()
    {
        using var doc = JsonDocument.Parse(@"{""filePath"":""/w/b.cs""}");
        var input = doc.RootElement.Clone();

        var result = ToolArgumentExtractor.ExtractInputString("Read", input);

        result.Should().Be("/w/b.cs");
    }

    [Fact]
    public void ExtractInputString_WebFetchTool_ReturnsUrl()
    {
        using var doc = JsonDocument.Parse(@"{""url"":""https://example.com""}");
        var input = doc.RootElement.Clone();

        var result = ToolArgumentExtractor.ExtractInputString("WebFetch", input);

        result.Should().Be("https://example.com");
    }

    [Fact]
    public void ExtractInputString_FileUnderscorePathKey_ReturnsPath()
    {
        // MCP 工具使用 file_path（snake_case 约定）
        using var doc = JsonDocument.Parse(@"{""file_path"":""/w/mcp.cs""}");
        var input = doc.RootElement.Clone();

        var result = ToolArgumentExtractor.ExtractInputString("Write", input);

        result.Should().Be("/w/mcp.cs");
    }

    [Fact]
    public void ExtractInputString_StringInput_ReturnsString()
    {
        using var doc = JsonDocument.Parse("\"raw string\"");
        var input = doc.RootElement.Clone();

        var result = ToolArgumentExtractor.ExtractInputString("UnknownTool", input);

        result.Should().Be("raw string");
    }

    [Fact]
    public void ExtractInputString_UnknownToolObjectWithoutKnownFields_ReturnsNull()
    {
        using var doc = JsonDocument.Parse(@"{""custom_field"":""value""}");
        var input = doc.RootElement.Clone();

        var result = ToolArgumentExtractor.ExtractInputString("CustomTool", input);

        // 统一方法返回 null，调用方（如 YoloClassifier）自行决定 fallback 行为
        result.Should().BeNull();
    }

    [Fact]
    public void ExtractInputString_IsCaseInsensitive()
    {
        using var doc = JsonDocument.Parse(@"{""command"":""ls""}");
        var input = doc.RootElement.Clone();

        var resultLower = ToolArgumentExtractor.ExtractInputString("bash", input);
        var resultUpper = ToolArgumentExtractor.ExtractInputString("BASH", input);

        resultLower.Should().Be("ls");
        resultUpper.Should().Be("ls");
    }
}
