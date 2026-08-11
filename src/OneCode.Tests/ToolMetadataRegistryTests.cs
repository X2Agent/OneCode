using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="ToolMetadataRegistry"/> — registration, lookup, and metadata queries.
/// </summary>
public sealed class ToolMetadataRegistryTests
{
    [Fact]
    public void Register_SingleTool_CanBeFoundByName()
    {
        var registry = new ToolMetadataRegistry();
        registry.Register(new ToolMetadata { Name = "MyTool" });

        registry.Get("MyTool")!.Name.Should().Be("MyTool");
    }

    [Fact]
    public void Register_ToolWithAliases_FoundByAlias()
    {
        var registry = new ToolMetadataRegistry();
        registry.Register(new ToolMetadata { Name = "Bash", Aliases = ["Shell", "sh"] });

        registry.Get("Shell")!.Name.Should().Be("Bash");
        registry.Get("sh")!.Name.Should().Be("Bash");
    }

    [Fact]
    public void Get_CaseInsensitiveLookup_Succeeds()
    {
        var registry = new ToolMetadataRegistry();
        registry.Register(new ToolMetadata { Name = "Read" });

        registry.Get("read")!.Name.Should().Be("Read");
        registry.Get("READ")!.Name.Should().Be("Read");
    }

    [Fact]
    public void Clear_AfterRegister_EmptiesRegistry()
    {
        var registry = new ToolMetadataRegistry();
        registry.Register(new ToolMetadata { Name = "Read" });
        registry.Clear();

        registry.Get("Read").Should().BeNull();
    }

    [Fact]
    public void GetPolicy_UnregisteredTool_DefaultsToDestructive()
    {
        var registry = new ToolMetadataRegistry();

        var policy = registry.GetPolicy("UnknownTool");

        policy.Risk.Should().Be(ToolRisk.Destructive);
        policy.ApprovalMode.Should().Be(ToolApprovalMode.Always);
    }

    [Fact]
    public void GetPolicy_RegisteredTool_ReturnsMetadataPolicy()
    {
        var registry = new ToolMetadataRegistry();
        registry.Register(new ToolMetadata
        {
            Name = "Read",
            Risk = ToolRisk.ReadOnly,
            IsConcurrencySafe = true,
        });

        var policy = registry.GetPolicy("Read");

        policy.Risk.Should().Be(ToolRisk.ReadOnly);
        policy.IsConcurrencySafe.Should().BeTrue();
    }

    [Fact]
    public void GetVisibleToolNames_ExcludesHiddenAndDisabledTools()
    {
        var registry = new ToolMetadataRegistry();
        registry.Register(new ToolMetadata { Name = "Read" });
        registry.Register(new ToolMetadata { Name = "ToolSearch", IsVisible = false });
        registry.Register(new ToolMetadata { Name = "Disabled", IsEnabled = false });

        registry.GetVisibleToolNames().Should().BeEquivalentTo(["Read"]);
    }
}
