using Microsoft.Extensions.AI;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// <see cref="ToolTargetPathResolver"/> 的行为锁：目标路径只能来自工具 schema，
/// 且「解析不出来」必须与「工具没有路径概念」区分开——前者拒绝，后者放行。
/// 用例中的 AIFunction 由 <see cref="AIFunctionFactory"/> 从真实签名生成 schema，
/// 避免手写 schema 与生产编组口径漂移。
/// </summary>
public sealed class ToolTargetPathResolverTests
{
    private static AIFunction Function(string name, Delegate body) => AIFunctionFactory.Create(body, name: name);

    private static Dictionary<string, object?> Args() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?> ArgsOf(params (string Key, object? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
            dict[key] = value;
        return dict;
    }

    [Fact]
    public void Resolve_CanonicalFilePath_ReturnsResolved()
    {
        var function = Function("Read", (string filePath) => filePath);

        var result = ToolTargetPathResolver.Resolve(function, ArgsOf(("filePath", "src/a.cs")));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("src/a.cs");
        result.PrimaryPath.Should().Be("src/a.cs");
    }

    [Fact]
    public void Resolve_McpSnakeCaseFile_ReturnsResolved()
    {
        var function = Function("mcp__fs__read", (string file_path) => file_path);

        var result = ToolTargetPathResolver.Resolve(function, ArgsOf(("file_path", "/w/mcp.cs")));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("/w/mcp.cs");
    }

    [Fact]
    public void Resolve_TierBSuffixParameter_ReturnsResolved()
    {
        var function = Function("Custom", (string? targetPath = null) => targetPath ?? "");

        var result = ToolTargetPathResolver.Resolve(function, ArgsOf(("targetPath", "src/x.cs")));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("src/x.cs");
    }

    [Fact]
    public void Resolve_OptionalPathOmitted_ReturnsNone()
    {
        // Grep/Glob/LS 的 path 可选：未提供时不替工具假设「默认就是 cwd」。
        var function = Function("Grep", (string pattern, string? path = null) => pattern + path);

        var result = ToolTargetPathResolver.Resolve(function, ArgsOf(("pattern", "TODO")));

        result.Kind.Should().Be(ToolTargetPathKind.None);
        result.HasPath.Should().BeFalse();
    }

    [Fact]
    public void Resolve_RequiredPathMissing_ReturnsUnresolvableWithActionableReason()
    {
        var function = Function("Write", (string filePath, string content) => filePath + content);

        var result = ToolTargetPathResolver.Resolve(function, ArgsOf(("content", "x")));

        result.Kind.Should().Be(ToolTargetPathKind.Unresolvable);
        result.Reason.Should().Contain("Write").And.Contain("filePath");
    }

    [Fact]
    public void Resolve_RequiredPathEmpty_ReturnsUnresolvable()
    {
        var function = Function("Read", (string filePath) => filePath);

        var result = ToolTargetPathResolver.Resolve(function, ArgsOf(("filePath", "   ")));

        result.Kind.Should().Be(ToolTargetPathKind.Unresolvable);
    }

    [Fact]
    public void Resolve_NonStringValue_ReturnsUnresolvable()
    {
        var function = Function("Custom", (string? path = null) => path ?? "");

        var result = ToolTargetPathResolver.Resolve(function, ArgsOf(("path", 42)));

        result.Kind.Should().Be(ToolTargetPathKind.Unresolvable);
        result.Reason.Should().Contain("path");
    }

    [Fact]
    public void Resolve_PathDeclaredNonString_ReturnsUnresolvable()
    {
        var function = Function("Custom", (int path) => path.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var result = ToolTargetPathResolver.Resolve(function, ArgsOf(("path", 7)));

        result.Kind.Should().Be(ToolTargetPathKind.Unresolvable);
        result.Reason.Should().Contain("integer");
    }

    [Fact]
    public void Resolve_JsonElementStringValue_ReturnsResolved()
    {
        // 运行时参数字典里可能是未反序列化的 JsonElement。
        var function = Function("Read", (string filePath) => filePath);
        using var doc = System.Text.Json.JsonDocument.Parse("\"src/b.cs\"");

        var result = ToolTargetPathResolver.Resolve(
            function, ArgsOf(("filePath", doc.RootElement.Clone())));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("src/b.cs");
    }

    [Fact]
    public void Resolve_PatternParameterOnly_ReturnsNone()
    {
        // 通用名词不是路径键：不能用它们把「无路径概念」误判成「无法解析」。
        var function = Function("Custom", (string pattern) => pattern);

        ToolTargetPathResolver.Resolve(function, ArgsOf(("pattern", "x"))).Kind
            .Should().Be(ToolTargetPathKind.None);
    }

    [Fact]
    public void Resolve_TargetParameterOnly_ReturnsNone()
    {
        var function = Function("Custom", (string target) => target);

        ToolTargetPathResolver.Resolve(function, ArgsOf(("target", "x"))).Kind
            .Should().Be(ToolTargetPathKind.None);
    }

    [Fact]
    public void Resolve_DirectoryParameterOnly_ReturnsNone()
    {
        var function = Function("Custom", (string directory) => directory);

        ToolTargetPathResolver.Resolve(function, ArgsOf(("directory", "x"))).Kind
            .Should().Be(ToolTargetPathKind.None);
    }

    [Fact]
    public void Resolve_NoParameters_ReturnsNone()
    {
        var function = Function("TodosList", () => "ok");

        var result = ToolTargetPathResolver.Resolve(function, Args());

        result.Kind.Should().Be(ToolTargetPathKind.None);
    }

    [Fact]
    public void Resolve_CanonicalKeyTakesPriority_BothPathsCollected()
    {
        // 同时存在规范键与后缀参数时：规范键决定 PrimaryPath 的优先级，但后缀参数同样必须受检——
        // 只收规范键会让 backupPath 之类的第二个路径形参数完全脱离边界校验。
        var function = Function("Custom", (string path, string? backupPath = null) => path + backupPath);

        var result = ToolTargetPathResolver.Resolve(
            function, ArgsOf(("path", "main.cs"), ("backupPath", "bak.cs")));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("main.cs", "bak.cs");
        result.PrimaryPath.Should().Be("main.cs");
    }

    [Fact]
    public void Resolve_MultipleSuffixParams_AllCollectedInSchemaOrder()
    {
        // 反证锚点：无规范键时若只收第一个后缀参数，模型把越界目标放进 targetPath 即可绕过
        // 工作区边界（MCP 文件工具的典型形态）——两条路径都必须进入校验集合。
        var function = Function(
            "Copy", (string sourcePath, string targetPath) => sourcePath + targetPath);

        var result = ToolTargetPathResolver.Resolve(
            function, ArgsOf(("sourcePath", "src/a.cs"), ("targetPath", "dst/b.cs")));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("src/a.cs", "dst/b.cs");
        result.PrimaryPath.Should().Be("src/a.cs");
    }

    [Fact]
    public void Resolve_SecondSuffixParamRequiredButMissing_ReturnsUnresolvable()
    {
        // 越界目标的另一形态：必填后缀参数缺失必须拒绝整次调用，
        // 不让第一个合法路径掩盖第二个路径参数的不可用。
        var function = Function(
            "Copy", (string sourcePath, string targetPath) => sourcePath + targetPath);

        var result = ToolTargetPathResolver.Resolve(
            function, ArgsOf(("sourcePath", "src/a.cs")));

        result.Kind.Should().Be(ToolTargetPathKind.Unresolvable);
        result.Reason.Should().Contain("targetPath");
    }

    [Fact]
    public void Resolve_PathShapedNameInAnyCase_IsCollected()
    {
        // 参数取值按 OrdinalIgnoreCase；PascalCase 的 Path 形态也必须被收集，不因大小写掉出保护范围。
        var function = Function("Custom", (string? Path = null) => Path);

        var result = ToolTargetPathResolver.Resolve(function, ArgsOf(("Path", "src/x.cs")));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("src/x.cs");
    }

    // ---- 多规范键：Lsp 的 file 是主参数，path 是可选检索范围 ----

    private static AIFunction LspShaped() => Function(
        "Lsp", (string action, string file = "", string? path = null) => action + file + path);

    [Fact]
    public void Resolve_FileAndOptionalPathDeclared_OnlyFileProvided_ReturnsFile()
    {
        // 反证锚点：只认 path 时，path 缺省会把整次调用判成「无路径」，file 完全不受检查。
        var result = ToolTargetPathResolver.Resolve(
            LspShaped(), ArgsOf(("action", "hover"), ("file", "src/a.cs")));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("src/a.cs");
    }

    [Fact]
    public void Resolve_FileAndPathBothProvided_ReturnsEveryPathInPriorityOrder()
    {
        // 规范键优先级 path 高于 file；两条都要进入校验，PrimaryPath 取优先级最高的一条。
        var result = ToolTargetPathResolver.Resolve(
            LspShaped(), ArgsOf(("action", "references"), ("file", "src/a.cs"), ("path", "src")));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("src", "src/a.cs");
        result.PrimaryPath.Should().Be("src");
    }

    [Fact]
    public void Resolve_FileAndPathDeclared_OnlyPathProvided_ReturnsPath()
    {
        // 符号检索形态（references by symbol）：只给 path，file 缺省为空串，不得被当成「必填为空」。
        var result = ToolTargetPathResolver.Resolve(
            LspShaped(), ArgsOf(("action", "references"), ("path", "src")));

        result.Kind.Should().Be(ToolTargetPathKind.Resolved);
        result.Paths.Should().Equal("src");
    }

    [Fact]
    public void Resolve_FileAndPathDeclared_NeitherProvided_ReturnsNone()
    {
        // 反证：多键并存不等于「必须有路径」——两者都是可选且都没给时，仍是无路径约束。
        var result = ToolTargetPathResolver.Resolve(LspShaped(), ArgsOf(("action", "workspaceSymbol")));

        result.Kind.Should().Be(ToolTargetPathKind.None);
        result.HasPath.Should().BeFalse();
    }

    [Fact]
    public void Resolve_FileAndPathDeclared_FileNonStringWhilePathValid_ReturnsUnresolvable()
    {
        // 一个可用的键不能掩盖另一个键的畸形值：任一路径参数不可用即拒绝整次调用。
        var function = Function(
            "Lsp", (string action, string? file = null, string? path = null) => action + file + path);

        var result = ToolTargetPathResolver.Resolve(
            function, ArgsOf(("action", "hover"), ("file", 42), ("path", "src")));

        result.Kind.Should().Be(ToolTargetPathKind.Unresolvable);
        result.Reason.Should().Contain("file");
    }
}
