using System.Collections.Immutable;
using OneCode.App.Tui;

namespace OneCode.App.Query;

public interface IToolCapabilityResolver
{
    ToolCapabilitySet Resolve(WorkingMode mode);
}

/// <summary>
/// Resolves the immutable tool visibility boundary before a provider request is built.
/// Unknown or unclassified tools are excluded from Plan mode by default.
/// </summary>
public sealed class ToolCapabilityResolver(IToolCatalog catalog) : IToolCapabilityResolver
{
    public ToolCapabilitySet Resolve(WorkingMode mode)
    {
        var visibleNames = catalog.Tools
            .Where(tool => catalog.Metadata.Get(tool.Name) is { IsVisible: true, IsEnabled: true })
            .Select(tool => tool.Name)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        if (mode != WorkingMode.Plan)
            return ToolCapabilitySet.CreateUnrestricted(visibleNames);

        var allowed = visibleNames
            .Where(name =>
            {
                var metadata = catalog.Metadata.Get(name);
                return metadata is not null
                    && (metadata.Risk is ToolRisk.Safe or ToolRisk.ReadOnly
                        || (metadata.Category & ToolCategory.PlanAllowed) != 0)
                    && metadata.Risk != ToolRisk.Dynamic;
            })
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        return new ToolCapabilitySet
        {
            AllowedToolNames = allowed,
            AllowedCategories = ToolCategory.PlanAllowed,
            MaximumRisk = ToolRisk.ReadOnly,
            AllowDynamicActivation = true,
            AllowSubAgents = true,
        };
    }
}
