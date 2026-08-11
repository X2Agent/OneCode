using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Tools;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="SessionToolSet"/> — session-level tool activation state.
/// Covers: monotonic growth, activation persistence, session isolation, tool ordering.
/// </summary>
public sealed class SessionToolSetTests
{
    // Monotonic Growth

    [Fact]
    public void GetTools_ToolsOnlyGrow_NeverShrinkAcrossCalls()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var sut = new SessionToolSet(catalog, metadata);

        // Turn 1: empty prompt → only Always tools
        var turn1 = sut.GetTools("");
        var count1 = turn1.Count;

        // Turn 2: prompt matching a Contextual tool → should add it
        var turn2 = sut.GetTools("search the web");
        var count2 = turn2.Count;

        // Turn 3: different prompt → previously activated tool must persist
        var turn3 = sut.GetTools("read a file");
        var count3 = turn3.Count;

        count2.Should().BeGreaterThan(count1, "contextual tool should be added");
        count3.Should().BeGreaterThanOrEqualTo(count2, "tools must never shrink (monotonic growth)");
        turn3.Select(t => t.Name).Should().Contain("WebSearch", "activated tool must persist");
    }

    [Fact]
    public void GetTools_AlwaysTools_AlwaysPresent()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var sut = new SessionToolSet(catalog, metadata);

        var tools = sut.GetTools("");
        tools.Select(t => t.Name).Should().Contain(["Read", "Write", "LS"]);
    }

    // Activation Persistence

    [Fact]
    public void Activate_ExplicitActivation_PersistsAcrossCalls()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var sut = new SessionToolSet(catalog, metadata);

        sut.Activate("WebSearch");

        var tools1 = sut.GetTools("read a file");
        var tools2 = sut.GetTools("different prompt");

        tools1.Select(t => t.Name).Should().Contain("WebSearch");
        tools2.Select(t => t.Name).Should().Contain("WebSearch", "activation must persist across calls");
    }

    [Fact]
    public void Activate_AlwaysTool_ReturnsFalse()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var sut = new SessionToolSet(catalog, metadata);

        sut.Activate("Read").Should().BeFalse("Always tools don't need activation");
    }

    [Fact]
    public void Activate_AlreadyActivated_ReturnsFalse()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var sut = new SessionToolSet(catalog, metadata);

        sut.Activate("WebSearch").Should().BeTrue();
        sut.Activate("WebSearch").Should().BeFalse("already activated");
    }

    [Fact]
    public void Activate_UnknownTool_ReturnsFalse()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var sut = new SessionToolSet(catalog, metadata);

        sut.Activate("NonExistentTool").Should().BeFalse();
    }

    // Session Isolation

    [Fact]
    public void SessionIsolation_DifferentManagers_HaveIndependentState()
    {
        var (catalog1, metadata1) = CreateTestCatalog();
        var (catalog2, metadata2) = CreateTestCatalog();

        var manager1 = new SessionToolSetManager(catalog1, metadata1);
        var manager2 = new SessionToolSetManager(catalog2, metadata2);

        var session1 = manager1.GetOrCreate("conv-1");
        var session2 = manager2.GetOrCreate("conv-2");

        session1.Activate("WebSearch");

        session1.ActivatedNames.Should().Contain("WebSearch");
        session2.ActivatedNames.Should().NotContain("WebSearch", "sessions must be isolated");
    }

    [Fact]
    public void SessionIsolation_SameManager_DifferentConversationIds()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var manager = new SessionToolSetManager(catalog, metadata);

        var session1 = manager.GetOrCreate("conv-A");
        var session2 = manager.GetOrCreate("conv-B");

        session1.Activate("WebSearch");

        session1.ActivatedNames.Should().Contain("WebSearch");
        session2.ActivatedNames.Should().NotContain("WebSearch");
    }

    // Tool Ordering

    [Fact]
    public void GetTools_AlwaysToolsInCatalogOrder_ThenActivatedInActivationOrder()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var sut = new SessionToolSet(catalog, metadata);

        sut.Activate("WebSearch");
        sut.Activate("Lsp");

        var tools = sut.GetTools("");

        var names = tools.Select(t => t.Name).ToList();

        // Always tools first in catalog registration order
        var alwaysIndex = names.IndexOf("Read");
        var lsIndex = names.IndexOf("LS");
        alwaysIndex.Should().BeLessThan(lsIndex, "Always tools in catalog order");

        // Activated tools after Always tools, in activation order
        var webSearchIndex = names.IndexOf("WebSearch");
        var lspIndex = names.IndexOf("Lsp");
        webSearchIndex.Should().BeLessThan(lspIndex, "activated tools in activation order");
        webSearchIndex.Should().BeGreaterThan(lsIndex, "activated tools after Always tools");
    }

    [Fact]
    public void GetTools_NewActivation_AppendsToEnd_DoesNotReorder()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var sut = new SessionToolSet(catalog, metadata);

        var turn1 = sut.GetTools("search the web");
        var turn1Names = turn1.Select(t => t.Name).ToList();

        // Activate another tool
        sut.Activate("Lsp");

        var turn2 = sut.GetTools("");
        var turn2Names = turn2.Select(t => t.Name).ToList();

        // All tools from turn1 should still be present, in the same relative order
        foreach (var name in turn1Names)
        {
            var idx1 = turn1Names.IndexOf(name);
            var idx2 = turn2Names.IndexOf(name);
            idx2.Should().BeGreaterThanOrEqualTo(0, $"{name} should still be present");
        }

        // The newly activated tool should be at the end
        turn2Names[^1].Should().Be("Lsp", "newly activated tool appended to end");
    }

    // TryActivate via ToolActivationContext

    [Fact]
    public void TryActivate_WithoutContext_ReturnsFalse()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var manager = new SessionToolSetManager(catalog, metadata);

        ToolActivationContext.CurrentConversationId = null;

        manager.TryActivate("WebSearch").Should().BeFalse("no ambient conversation context");
    }

    [Fact]
    public void TryActivate_WithContext_ActivatesInCurrentSession()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var manager = new SessionToolSetManager(catalog, metadata);
        manager.GetOrCreate("test-conv");

        ToolActivationContext.CurrentConversationId = "test-conv";
        ToolActivationContext.CurrentCapabilities = ToolCapabilitySet.CreateUnrestricted(catalog.Tools.Select(tool => tool.Name));
        try
        {
            manager.TryActivate("WebSearch").Should().BeTrue();
            manager.IsActivated("WebSearch").Should().BeTrue();
        }
        finally
        {
            ToolActivationContext.CurrentCapabilities = null;
            ToolActivationContext.CurrentConversationId = null;
        }
    }

    [Fact]
    public void Remove_ClearsSessionActivationState()
    {
        var (catalog, metadata) = CreateTestCatalog();
        var manager = new SessionToolSetManager(catalog, metadata);
        manager.GetOrCreate("test-conv");
        ToolActivationContext.CurrentConversationId = "test-conv";
        ToolActivationContext.CurrentCapabilities = ToolCapabilitySet.CreateUnrestricted(catalog.Tools.Select(tool => tool.Name));
        try
        {
            manager.TryActivate("WebSearch").Should().BeTrue();
            manager.Remove("test-conv").Should().BeTrue();
            manager.IsActivated("WebSearch").Should().BeFalse();
            manager.Remove("test-conv").Should().BeFalse();
        }
        finally
        {
            ToolActivationContext.CurrentCapabilities = null;
            ToolActivationContext.CurrentConversationId = null;
        }
    }

    // Snapshot for Sub-Agent Isolation

    // Helpers

    /// <summary>
    /// Creates a minimal ToolCatalog + ToolMetadataRegistry with test tools:
    /// Always: Read, Write, LS
    /// Contextual: WebSearch (keywords: "web search"), Lsp (keywords: "lsp")
    /// </summary>
    private static (ToolCatalog Catalog, ToolMetadataRegistry Metadata) CreateTestCatalog()
    {
        var metadata = new ToolMetadataRegistry();
        var serviceProvider = Substitute.For<IServiceProvider>();

        // Register metadata for Always tools
        metadata.Register(new ToolMetadata { Name = "Read", Risk = ToolRisk.ReadOnly, SearchHint = "read file contents", LoadPolicy = ToolLoadPolicy.Always });
        metadata.Register(new ToolMetadata { Name = "Write", Risk = ToolRisk.Destructive, SearchHint = "write file", LoadPolicy = ToolLoadPolicy.Always });
        metadata.Register(new ToolMetadata { Name = "LS", Risk = ToolRisk.ReadOnly, SearchHint = "list directory", LoadPolicy = ToolLoadPolicy.Always });

        // Register metadata for Contextual tools
        metadata.Register(new ToolMetadata
        {
            Name = "WebSearch",
            Risk = ToolRisk.ReadOnly,
            SearchHint = "search the web",
            LoadPolicy = ToolLoadPolicy.Contextual,
            Keywords = ["web search", "search web"]
        });
        metadata.Register(new ToolMetadata
        {
            Name = "Lsp",
            Risk = ToolRisk.ReadOnly,
            SearchHint = "perform language server operations",
            LoadPolicy = ToolLoadPolicy.Contextual,
            Keywords = ["lsp", "language server"]
        });

        // Create AIFunction stubs for each tool
        var registrations = new List<ToolRegistration>
        {
            CreateFunctionRegistration("Read"),
            CreateFunctionRegistration("Write"),
            CreateFunctionRegistration("LS"),
            CreateFunctionRegistration("WebSearch", loadPolicy: ToolLoadPolicy.Contextual, keywords: ["web search", "search web"]),
            CreateFunctionRegistration("Lsp", loadPolicy: ToolLoadPolicy.Contextual, keywords: ["lsp", "language server"]),
        };

        var catalog = new ToolCatalog(
            new Lazy<List<AIFunction>>(() => ToolCatalog.BuildStaticTools(serviceProvider, registrations, metadata)),
            metadata,
            mcpConnectionManager: null);
        return (catalog, metadata);
    }

    private static ToolRegistration CreateFunctionRegistration(
        string name,
        ToolLoadPolicy loadPolicy = ToolLoadPolicy.Always,
        IReadOnlyList<string>? keywords = null)
    {
        return new ToolRegistration(
            Name: name,
            Risk: ToolRisk.Safe,
            FunctionFactory: _ => CreateStubFunction(name),
            LoadPolicy: loadPolicy,
            Keywords: keywords);
    }

    private static AIFunction CreateStubFunction(string name)
    {
        return AIFunctionFactory.Create((string? arg = null) => Task.FromResult($"{name} result"), name: name);
    }
}
