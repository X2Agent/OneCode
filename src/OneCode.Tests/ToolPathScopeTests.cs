using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// <see cref="ToolPathScope"/> 的行为锁：路径触达范围必须完全由 <see cref="ToolNames"/> 元数据推导，
/// 且 Dynamic（MCP）只拿敏感读 + 边界，不拿写保护；provider 注入的会话状态工具必须落在 None，
/// 否则工作记忆与待办清单会被新增的路径规则误拦。
/// </summary>
public sealed class ToolPathScopeTests
{
    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("Delete")]
    [InlineData("ApplyWorkspaceEdit")]
    public void Classify_FileMutationTools_ReturnsWrite(string toolName)
        => ToolPathScope.Classify(toolName).Should().Be(ToolPathScopeKind.Write);

    [Theory]
    [InlineData("Read")]
    [InlineData("Grep")]
    [InlineData("Glob")]
    [InlineData("LS")]
    public void Classify_ReadOnlyTools_ReturnsReadOnly(string toolName)
        => ToolPathScope.Classify(toolName).Should().Be(ToolPathScopeKind.ReadOnly);

    [Fact]
    public void Classify_DynamicTool_ReturnsDynamic()
    {
        // 生产注册里 Bash 是 ToolRisk.Dynamic；MCP 工具经 ToolCatalog.AddMcpTools 打同一标记。
        ToolPathScope.Classify("Bash").Should().Be(ToolPathScopeKind.Dynamic);
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("SubmitPlan")]
    [InlineData("todos_add")]
    [InlineData("NotRegisteredTool")]
    public void Classify_NonFileTools_ReturnsNone(string toolName)
        => ToolPathScope.Classify(toolName).Should().Be(ToolPathScopeKind.None);

    [Fact]
    public void Classify_ProviderInjectedTools_ReturnNone()
    {
        // 反证：FileMemory 与待办清单工具由 provider 注入、只读写会话状态，
        // 一旦被划进 Dynamic/ReadOnly/Write，工作记忆与技能资源就会被误拦。
        foreach (var name in HarnessProviderTools.Names)
            ToolPathScope.Classify(name).Should().Be(ToolPathScopeKind.None, $"{name} 只触碰会话状态");
    }

    [Fact]
    public void ScopeRules_DynamicGetsSensitiveReadButNotWriteProtection()
    {
        ToolPathScope.RequiresSensitiveReadCheck(ToolPathScopeKind.Dynamic).Should().BeTrue();
        ToolPathScope.RequiresWriteCheck(ToolPathScopeKind.Dynamic).Should().BeFalse(
            "MCP 工具已受审批边界约束，叠加写保护会误伤只读的 MCP 工具");
        ToolPathScope.RequiresWriteCheck(ToolPathScopeKind.Write).Should().BeTrue();
        ToolPathScope.RequiresSensitiveReadCheck(ToolPathScopeKind.Write).Should().BeFalse(
            "写入类工具不套敏感读清单，避免 Write 与 Read 的判定相互污染");
        ToolPathScope.TouchesFiles(ToolPathScopeKind.None).Should().BeFalse();
    }
}
