using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Tui;
using OneCode.Core.Tools;
using NSubstitute;

namespace OneCode.Tests;

public sealed class ToolCapabilitySetTests
{
    [Fact]
    public void Intersect_CannotExpandParentNamesOrFlags()
    {
        var parent = Capabilities(["Read", "Agent"], allowDynamic: false, allowSubAgents: false);
        var child = Capabilities(["Read", "Write", "Agent"], allowDynamic: true, allowSubAgents: true);

        var effective = parent.Intersect(child);

        effective.AllowedToolNames.Should().BeEquivalentTo(["Read", "Agent"]);
        effective.AllowDynamicActivation.Should().BeFalse();
        effective.AllowSubAgents.Should().BeFalse();
    }

    [Fact]
    public void Resolver_PlanMode_ExcludesWriteDynamicAndMcpTools()
    {
        var metadata = new ToolMetadataRegistry();
        var tools = new[]
        {
            Tool("Read", ToolRisk.ReadOnly, metadata),
            Tool("Write", ToolRisk.Destructive, metadata),
            Tool("Bash", ToolRisk.Dynamic, metadata),
            Tool("mcp__server__tool", ToolRisk.Dynamic, metadata),
            Tool("SubmitPlan", ToolRisk.Safe, metadata, ToolCategory.PlanAllowed),
        };
        var catalog = Substitute.For<IToolCatalog>();
        catalog.Tools.Returns(tools);
        catalog.Metadata.Returns(metadata);

        var effective = new ToolCapabilityResolver(catalog).Resolve(WorkingMode.Plan);

        effective.AllowedToolNames.Should().Contain(["Read", "SubmitPlan"]);
        effective.AllowedToolNames.Should().NotContain(["Write", "Bash", "mcp__server__tool"]);
    }

    [Fact]
    public void SessionActivation_RejectsToolOutsideCurrentCapabilities()
    {
        var metadata = new ToolMetadataRegistry();
        var read = Tool("Read", ToolRisk.ReadOnly, metadata);
        var write = Tool("Write", ToolRisk.Destructive, metadata);
        var catalog = Substitute.For<IToolCatalog>();
        catalog.Tools.Returns([read, write]);
        catalog.Metadata.Returns(metadata);
        catalog.Find(Arg.Any<string>()).Returns(call =>
            new[] { read, write }.FirstOrDefault(tool => tool.Name == call.Arg<string>()));

        var session = new SessionToolSet(catalog, metadata);
        var plan = Capabilities(["Read"], allowDynamic: true, allowSubAgents: true);

        session.Activate("Write", plan).Should().BeFalse();
        session.GetTools("write", plan).Select(tool => tool.Name).Should().NotContain("Write");
    }

    private static ToolCapabilitySet Capabilities(
        IEnumerable<string> names,
        bool allowDynamic,
        bool allowSubAgents)
        => new()
        {
            AllowedToolNames = names.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            AllowedCategories = ToolCategory.PlanAllowed,
            MaximumRisk = ToolRisk.ReadOnly,
            AllowDynamicActivation = allowDynamic,
            AllowSubAgents = allowSubAgents,
        };

    private static AIFunction Tool(
        string name,
        ToolRisk risk,
        ToolMetadataRegistry metadata,
        ToolCategory category = ToolCategory.None)
    {
        metadata.Register(new ToolMetadata
        {
            Name = name,
            Risk = risk,
            Category = category,
            LoadPolicy = ToolLoadPolicy.Contextual,
        });
        return AIFunctionFactory.Create(() => name, name: name);
    }
}
