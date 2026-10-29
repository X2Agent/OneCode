using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// 多文件协议工具的豁免锁：<c>ApplyWorkspaceEdit</c> 的路径嵌在 LSP 协议对象内部
/// （顶层只有 <c>workspaceEditJson</c>），边界由工具自身的 <c>PathsHelper.SafeResolve</c> 保证。
/// 解析器必须给出 <see cref="ToolTargetPathKind.None"/> 而不是「无法解析」，
/// 否则这个功能会被 Layer 0 永久拒绝。
/// </summary>
public sealed class ApplyWorkspaceEditExemptionTests
{
    private static AIFunction CreateFunction()
    {
        var tool = new ApplyWorkspaceEditTool(
            Substitute.For<IWorkingDirectoryAccessor>(),
            Substitute.For<ILspNotifier>());
        return AIFunctionFactory.Create(tool.ApplyAsync, name: "ApplyWorkspaceEdit");
    }

    [Fact]
    public void RealSchema_ExposesNoTopLevelPathProperty()
    {
        var function = CreateFunction();

        var properties = function.JsonSchema.GetProperty("properties");

        properties.TryGetProperty("workspaceEditJson", out _).Should().BeTrue();
        properties.TryGetProperty("filePath", out _).Should().BeFalse(
            "顶层只有协议对象；filePath 是工具内部辅助方法的局部变量");
    }

    [Fact]
    public void Resolve_ProtocolToolCall_ReturnsNone()
    {
        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workspaceEditJson"] = """{"documentChanges":[]}""",
        };

        var result = ToolTargetPathResolver.Resolve(CreateFunction(), arguments);

        result.Kind.Should().Be(ToolTargetPathKind.None);
    }

    [Fact]
    public void Scope_ProtocolTool_IsWrite()
    {
        // 分类仍是写类（Category=FileWrite）：豁免的是「无路径属性」，不是「不是文件工具」。
        ToolPathScope.Classify("ApplyWorkspaceEdit").Should().Be(ToolPathScopeKind.Write);
    }
}
