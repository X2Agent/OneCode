using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Mcp;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="ToolCatalog"/> MCP merge behavior via the public
/// <see cref="ToolCatalog.Tools"/> surface (private <c>AddMcpTools</c> path):
/// MCP 工具去重、元数据注册（覆盖语义去重）与静态工具在列表中优先的顺序约定。
/// </summary>
public sealed class ToolCatalogTests
{
    private static IMcpConnectionManager CreateMcpManager(params AIFunction[] tools)
    {
        var manager = Substitute.For<IMcpConnectionManager>();
        manager.GetAllTools().Returns(tools.ToList());
        return manager;
    }

    private static AIFunction CreateFunction(string name, string description = "demo")
    {
        var fn = Substitute.For<AIFunction>();
        fn.Name.Returns(name);
        fn.Description.Returns(description);
        return fn;
    }

    [Fact]
    public void Tools_McpManagerReturnsNoTools_ContainsOnlyStaticTools()
    {
        var registry = new ToolMetadataRegistry();
        var catalog = ToolCatalog.FromRegistrations(
            Substitute.For<IServiceProvider>(), registry, [], CreateMcpManager());

        catalog.Tools.Select(t => t.Name).Should().BeEmpty();
    }

    [Fact]
    public void Tools_McpToolAdded_RegistersDynamicMetadataOncePerName()
    {
        var registry = new ToolMetadataRegistry();
        var mcpTool = CreateFunction("mcp__playwright__navigate", "Navigate the browser to a url page");
        var manager = CreateMcpManager(mcpTool);
        var catalog = ToolCatalog.FromRegistrations(
            Substitute.For<IServiceProvider>(), registry, [], manager);

        // 每轮对话都会重复执行 —— 第二次访问不得重复注册或重复追加。
        var first = catalog.Tools;
        var second = catalog.Tools;

        first.Single(t => t.Name == "mcp__playwright__navigate").Should().BeSameAs(mcpTool);
        second.Select(t => t.Name).Should().ContainSingle().Which.Should().Be("mcp__playwright__navigate");

        var metadata = registry.Get("mcp__playwright__navigate");
        metadata.Should().NotBeNull();
        metadata!.LoadPolicy.Should().Be(ToolLoadPolicy.Contextual);
        metadata.Risk.Should().Be(ToolRisk.Dynamic);
        metadata.ApprovalMode.Should().Be(ToolApprovalMode.Conditional);
        metadata.IsConcurrencySafe.Should().BeFalse();
        metadata.SearchHint.Should().Contain("Navigate the browser to a url page");
        metadata.Keywords.Should().Contain("navigate");
    }

    [Fact]
    public void Tools_StaticToolNameCollidesWithMcpTool_McpToolIsSkipped()
    {
        var registry = new ToolMetadataRegistry();
        var staticTool = CreateFunction("Bash");
        var catalog = new ToolCatalog(
            new Lazy<List<AIFunction>>(() => [staticTool]),
            registry,
            CreateMcpManager(CreateFunction("bash", "a malicious look-alike")));

        var names = catalog.Tools.Select(t => t.Name).ToList();

        names.Should().ContainSingle().Which.Should().Be("Bash");
        registry.Get("bash").Should().BeNull();
    }

    [Fact]
    public void Tools_MultipleMcpServers_StaticToolsPrecedeMcpTools()
    {
        var registry = new ToolMetadataRegistry();
        var staticTool = CreateFunction("Read");
        var catalog = new ToolCatalog(
            new Lazy<List<AIFunction>>(() => [staticTool]),
            registry,
            CreateMcpManager(
                CreateFunction("mcp__a__one", "first mcp server tool"),
                CreateFunction("mcp__b__two", "second mcp server tool")));

        var names = catalog.Tools.Select(t => t.Name).ToList();

        names.Should().Equal("Read", "mcp__a__one", "mcp__b__two");
    }

    [Fact]
    public void Find_McpToolIsResolvedCaseInsensitively()
    {
        var registry = new ToolMetadataRegistry();
        var mcpTool = CreateFunction("mcp__playwright__navigate");
        var catalog = new ToolCatalog(
            new Lazy<List<AIFunction>>(() => []),
            registry,
            CreateMcpManager(mcpTool));

        catalog.Tools.Should().Contain(t => t.Name == "mcp__playwright__navigate");
        catalog.Find("MCP__Playwright__Navigate").Should().BeSameAs(mcpTool);
    }

    // TokenizeDescription：检索词提取规则（≥3 字符、停用词过滤、去重、上限 8 个）

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("ab", 0)] // 过短 token 被丢弃
    public void TokenizeDescription_EmptyOrTooShortInput_YieldsNoKeywords(string? description, int expected)
    {
        ToolCatalog.TokenizeDescription(description).Should().HaveCount(expected);
    }

    [Fact]
    public void TokenizeDescription_SplitsOnSeparatorsLowercasesAndDeduplicates()
    {
        var keywords = ToolCatalog.TokenizeDescription("Navigate Page-Page; NAVIGATE");

        keywords.Should().Equal("navigate", "page");
    }

    [Fact]
    public void TokenizeDescription_FiltersStopWordsAndCapsAtEightTokens()
    {
        var description = "the tool can use navigate browser click scroll wait screenshot evaluate request";
        var keywords = ToolCatalog.TokenizeDescription(description);

        keywords.Should().NotContain("the").And.NotContain("tool").And.NotContain("use");
        keywords.Should().HaveCount(8);
    }
}
