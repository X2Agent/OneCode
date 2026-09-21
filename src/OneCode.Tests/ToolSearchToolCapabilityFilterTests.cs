using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Tools;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// S1 行为契约：ToolSearch 的能力约束必须在排名与截断**之前**生效。
/// </summary>
/// <remarks>
/// 修复前实现先取全局前 20 条命中，再按 profile 能力过滤。当不允许的工具占据了高分位时，
/// 合法工具会因为名额被挤占而整条漏掉——这是漏召回，不是越权放行。
/// </remarks>
public sealed class ToolSearchToolCapabilityFilterTests
{
    [Fact]
    public void Search_DisallowedToolsOutrankAllowed_AllowedToolStillReturned()
    {
        var metadata = new ToolMetadataRegistry();

        // 大量不允许的工具共享查询关键词并拿到高分。
        for (var i = 0; i < 40; i++)
        {
            metadata.Register(new ToolMetadata
            {
                Name = $"Blocked{i}",
                Risk = ToolRisk.ReadOnly,
                SearchHint = "deploy release pipeline",
                Keywords = ["deploy"],
            });
        }

        metadata.Register(new ToolMetadata
        {
            Name = "Allowed",
            Risk = ToolRisk.ReadOnly,
            SearchHint = "deploy release pipeline",
            Keywords = ["deploy"],
        });

        var sut = new ToolSearchTool(metadata, Substitute.For<ISessionToolSetManager>());

        ToolActivationContext.CurrentCapabilities = ToolCapabilitySet.CreateUnrestricted(["Allowed"]);
        try
        {
            var result = sut.Search("deploy", maxResults: 5);

            result.Content.Should().Contain("Allowed",
                "the capability constraint must prune candidates before Top-K, not after");
            result.Content.Should().NotContain("Blocked");
        }
        finally
        {
            ToolActivationContext.CurrentCapabilities = null;
        }
    }

    /// <summary>
    /// 反证：能力约束仍然生效——不允许的工具不得因为过滤顺序改变而被放出来。
    /// </summary>
    [Fact]
    public void Search_DisallowedTools_AreNeverReturned()
    {
        var metadata = new ToolMetadataRegistry();
        metadata.Register(new ToolMetadata
        {
            Name = "Allowed",
            Risk = ToolRisk.ReadOnly,
            SearchHint = "deploy release",
            Keywords = ["deploy"],
        });
        metadata.Register(new ToolMetadata
        {
            Name = "Blocked",
            Risk = ToolRisk.ReadOnly,
            SearchHint = "deploy release",
            Keywords = ["deploy"],
        });

        var sut = new ToolSearchTool(metadata, Substitute.For<ISessionToolSetManager>());

        ToolActivationContext.CurrentCapabilities = ToolCapabilitySet.CreateUnrestricted(["Allowed"]);
        try
        {
            var result = sut.Search("deploy", maxResults: 20);

            result.Content.Should().Contain("Allowed");
            result.Content.Should().NotContain("Blocked");
        }
        finally
        {
            ToolActivationContext.CurrentCapabilities = null;
        }
    }
}
