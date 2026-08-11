using Microsoft.Extensions.AI;

namespace OneCode.Core.Tools;

/// <summary>
/// Aggregated tool catalog: static registrations plus live MCP tools.
/// </summary>
public interface IToolCatalog
{
    ToolMetadataRegistry Metadata { get; }

    IReadOnlyList<AIFunction> Tools { get; }

    AIFunction? Find(string name);

    IReadOnlySet<string> GetVisibleToolNames();
}
