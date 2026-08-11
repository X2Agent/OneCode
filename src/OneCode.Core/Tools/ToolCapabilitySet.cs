namespace OneCode.Core.Tools;

/// <summary>
/// Immutable upper bound for tools visible to an agent run. Child layers may intersect
/// this set, but must never expand it.
/// </summary>
public sealed record ToolCapabilitySet
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public required ImmutableHashSet<string> AllowedToolNames { get; init; }
    public required ToolCategory AllowedCategories { get; init; }
    public required ToolRisk MaximumRisk { get; init; }
    public required bool AllowDynamicActivation { get; init; }
    public required bool AllowSubAgents { get; init; }

    public static ToolCapabilitySet CreateUnrestricted(IEnumerable<string> toolNames) => new()
    {
        AllowedToolNames = toolNames.ToImmutableHashSet(NameComparer),
        AllowedCategories = (ToolCategory)(-1),
        MaximumRisk = ToolRisk.Dynamic,
        AllowDynamicActivation = true,
        AllowSubAgents = true,
    };

    public bool IsAllowed(ToolMetadata? metadata, string toolName)
    {
        if (metadata is null || !metadata.IsVisible || !metadata.IsEnabled)
            return false;

        if (!AllowSubAgents && toolName is "Agent" or "ParallelAgents")
            return false;

        return AllowedToolNames.Contains(toolName);
    }

    public ToolCapabilitySet Intersect(ToolCapabilitySet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new ToolCapabilitySet
        {
            AllowedToolNames = AllowedToolNames.Intersect(other.AllowedToolNames, NameComparer)
                .ToImmutableHashSet(NameComparer),
            AllowedCategories = AllowedCategories & other.AllowedCategories,
            MaximumRisk = (ToolRisk)Math.Min((int)MaximumRisk, (int)other.MaximumRisk),
            AllowDynamicActivation = AllowDynamicActivation && other.AllowDynamicActivation,
            AllowSubAgents = AllowSubAgents && other.AllowSubAgents,
        };
    }
}
