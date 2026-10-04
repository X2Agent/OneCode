using System.Text.Json;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Tools;
using OneCode.Core.Domain;
using OneCode.Core.Tasks;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// 自定义子代理（.onecode/agents/*.md）：严格解析（错误显式报错不静默）、项目级覆盖用户级、
/// mtime 惰性重扫、派工参数解析与 allowed_tools 交集（只减不增）。
/// </summary>
public sealed class CustomAgentTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"custom-agents-{Guid.NewGuid():N}");
    private string ProjectAgentsDir => Path.Combine(_tempDir, "project", ".onecode", "agents");
    private string UserAgentsDir => Path.Combine(_tempDir, "user", ".onecode", "agents");

    public CustomAgentTests() => Directory.CreateDirectory(ProjectAgentsDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private CustomAgentCatalog CreateCatalog()
        => new(Path.Combine(_tempDir, "project"), userHome: Path.Combine(_tempDir, "user"));

    private static void WriteAgent(string dir, string fileName, string frontmatter, string body = "Do the thing.")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), $"---\n{frontmatter}\n---\n{body}");
    }

    // ---- 解析器 ----

    [Fact]
    public void Parse_ValidDefinition_ParsesAllFields()
    {
        var definition = AgentDefinitionParser.Parse(
            "---\nname: reviewer\ndescription: Reviews code\nallowed_tools:\n  - Read\n  - Grep\n---\nBe strict.",
            @"C:\x\reviewer.md");

        definition.Name.Should().Be("reviewer");
        definition.Description.Should().Be("Reviews code");
        definition.Instructions.Should().Be("Be strict.");
        definition.AllowedTools.Should().BeEquivalentTo("Read", "Grep");
        definition.SourcePath.Should().Be(@"C:\x\reviewer.md");
    }

    [Fact]
    public void Parse_MissingName_FallsBackToFileName()
    {
        var definition = AgentDefinitionParser.Parse(
            "---\ndescription: Reviews code\n---\nBody.", @"C:\x\security.md");

        definition.Name.Should().Be("security");
    }

    [Fact]
    public void Parse_MissingDescription_ThrowsWithFilePath()
    {
        var act = () => AgentDefinitionParser.Parse("---\nname: a\n---\nBody.", @"C:\x\a.md");

        act.Should().Throw<AgentDefinitionParseException>()
            .WithMessage("*C:\\x\\a.md*description*");
    }

    [Fact]
    public void Parse_UnknownFrontmatterField_ThrowsInsteadOfIgnoring()
    {
        // team.yaml 的教训：未知字段必须显式报错，不静默忽略
        var act = () => AgentDefinitionParser.Parse(
            "---\nname: a\ndescription: d\nmodel: gpt-x\n---\nBody.", @"C:\x\a.md");

        act.Should().Throw<AgentDefinitionParseException>()
            .WithMessage("*C:\\x\\a.md*");
    }

    [Fact]
    public void Parse_InvalidYaml_ThrowsWithLineNumber()
    {
        var act = () => AgentDefinitionParser.Parse(
            "---\nname: [unclosed\n---\nBody.", @"C:\x\a.md");

        act.Should().Throw<AgentDefinitionParseException>()
            .WithMessage("*line*");
    }

    [Fact]
    public void Parse_EmptyBody_Throws()
    {
        var act = () => AgentDefinitionParser.Parse("---\nname: a\ndescription: d\n---\n", @"C:\x\a.md");

        act.Should().Throw<AgentDefinitionParseException>().WithMessage("*instructions*");
    }

    [Fact]
    public void Parse_NoFrontmatter_Throws()
    {
        var act = () => AgentDefinitionParser.Parse("Just some text.", @"C:\x\a.md");

        act.Should().Throw<AgentDefinitionParseException>().WithMessage("*frontmatter*");
    }

    // ---- 目录：覆盖、重扫、错误呈现 ----

    [Fact]
    public void Catalog_ProjectOverridesSameNameUserAgent()
    {
        WriteAgent(UserAgentsDir, "reviewer.md", "name: reviewer\ndescription: user version");
        WriteAgent(ProjectAgentsDir, "reviewer.md", "name: reviewer\ndescription: project version");

        var catalog = CreateCatalog();

        catalog.Find("reviewer")!.Description.Should().Be("project version");
        catalog.GetAll().Should().ContainSingle();
    }

    [Fact]
    public void Catalog_RescansWhenFileAdded()
    {
        var catalog = CreateCatalog();
        catalog.Find("late").Should().BeNull();

        WriteAgent(ProjectAgentsDir, "late.md", "name: late\ndescription: added later");

        catalog.Find("late")!.Description.Should().Be("added later");
    }

    [Fact]
    public void Catalog_ParseError_SurfacesInLoadErrors()
    {
        WriteAgent(ProjectAgentsDir, "broken.md", "name: broken");

        var catalog = CreateCatalog();

        catalog.LoadErrors.Should().Contain(e => e.Contains("broken.md") && e.Contains("description"));
    }

    // ---- 解析（派工入口） ----

    [Fact]
    public void Resolve_BuiltinNames_PassThrough()
    {
        var catalog = CreateCatalog();

        foreach (var builtin in (string[])["general-purpose", "Explore", "Plan"])
        {
            var (definition, error) = CustomAgentResolver.Resolve(catalog, builtin);
            definition.Should().BeNull();
            error.Should().BeNull();
        }
    }

    [Fact]
    public void Resolve_UnknownName_ListsAvailable()
    {
        WriteAgent(ProjectAgentsDir, "reviewer.md", "name: reviewer\ndescription: reviews");
        var catalog = CreateCatalog();

        var (_, error) = CustomAgentResolver.Resolve(catalog, "nonexistent");

        error.Should().Contain("'reviewer'").And.Contain("general-purpose");
    }

    [Fact]
    public void Resolve_LoadErrors_ReportsThemAndBlocksDispatch()
    {
        WriteAgent(ProjectAgentsDir, "broken.md", "name: broken");
        var catalog = CreateCatalog();

        var (_, error) = CustomAgentResolver.Resolve(catalog, "anything");

        error.Should().Contain("broken.md");
    }

    // ---- AgentTool 派工 ----

    private static ICacheSafeParamsProvider NullParams() => new FixedCacheSafeParamsProvider(null);

    private sealed class FixedCacheSafeParamsProvider(CacheSafeParams? current) : ICacheSafeParamsProvider
    {
        public CacheSafeParams? Current { get; } = current;
    }

    private static ITaskService CreateTaskService()
    {
        var taskService = Substitute.For<ITaskService>();
        taskService.GetTaskToken(Arg.Any<string>()).Returns(TestContext.Current.CancellationToken);
        return taskService;
    }

    [Fact]
    public async Task AgentTool_CustomAgent_CarriesInstructionsAndAllowedTools()
    {
        WriteAgent(ProjectAgentsDir, "reviewer.md",
            "name: reviewer\ndescription: reviews\nallowed_tools:\n  - Read\n  - Grep",
            "Review code strictly.");
        var catalog = CreateCatalog();

        AgentRunRequest? captured = null;
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<AgentRunRequest>();
                return new AgentRunResult("reviewer", SessionId.NewId(), "done", 1, false);
            });
        var sut = new AgentTool(runner, NullParams(), CreateTaskService(), customAgents: catalog);

        var resultStr = await sut.RunAgentAsync("review the diff", agent: "reviewer");

        using var doc = JsonDocument.Parse(resultStr.Content);
        doc.RootElement.GetProperty("result").GetString().Should().Be("done");
        captured!.Instructions.Should().Be("Review code strictly.");
        captured.AllowedToolNames.Should().BeEquivalentTo("Read", "Grep");
    }

    [Fact]
    public async Task AgentTool_UnknownCustomAgent_ReturnsExplicitError()
    {
        var sut = new AgentTool(
            Substitute.For<IAgentRunner>(), NullParams(), CreateTaskService(),
            customAgents: CreateCatalog());

        var resultStr = await sut.RunAgentAsync("go", agent: "nonexistent");

        resultStr.IsError.Should().BeTrue();
        resultStr.Content.Should().Contain("nonexistent");
    }

    [Fact]
    public void AgentTool_Description_AppendsCustomAgentList()
    {
        WriteAgent(ProjectAgentsDir, "reviewer.md", "name: reviewer\ndescription: reviews code");
        var sut = new AgentTool(
            Substitute.For<IAgentRunner>(), NullParams(), CreateTaskService(),
            customAgents: CreateCatalog());

        var description = sut.BuildDescription();

        description.Should().Contain("'reviewer' — reviews code");
    }

    // ---- allowed_tools 交集（只减不增） ----

    [Fact]
    public void ApplyCustomAllowedTools_IntersectsAndNeverExpands()
    {
        var parent = ToolCapabilitySet.CreateUnrestricted(["Read", "Grep", "Bash"]);

        var merged = ForkedAgentRunner.ApplyCustomAllowedTools(parent, ["Read", "Bash", "Write"]);

        merged!.AllowedToolNames.Should().BeEquivalentTo("Read", "Bash");
    }

    [Fact]
    public void ApplyCustomAllowedTools_NoCustomLimit_ReturnsOriginal()
    {
        var parent = ToolCapabilitySet.CreateUnrestricted(["Read"]);

        ForkedAgentRunner.ApplyCustomAllowedTools(parent, null).Should().BeSameAs(parent);
        ForkedAgentRunner.ApplyCustomAllowedTools(parent, []).Should().BeSameAs(parent);
        ForkedAgentRunner.ApplyCustomAllowedTools(null, ["Read"]).Should().NotBeNull();
    }
}
