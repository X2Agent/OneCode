namespace OneCode.Core.Tools;

/// <summary>
/// Describes how a tool participates in the approval protocol.
/// Security decisions remain owned by the permission checker; this value only
/// tells the agent runtime whether the tool must be placed behind an approval
/// protocol boundary.
/// </summary>
public enum ToolApprovalMode
{
    /// <summary>The tool is never sent through the approval protocol.</summary>
    Never,

    /// <summary>Approval depends on the current permission mode and input.</summary>
    Conditional,

    /// <summary>The tool always requires an approval protocol boundary.</summary>
    Always,
}

/// <summary>
/// Central tool policy derived from registration metadata.
/// </summary>
public sealed record ToolPolicy(
    string Name,
    ToolRisk Risk,
    ToolApprovalMode ApprovalMode,
    bool IsConcurrencySafe,
    bool IsVisible);

public static class ToolPolicyDefaults
{
    public static ToolApprovalMode ForRisk(ToolRisk risk) =>
        risk switch
        {
            ToolRisk.ReadOnly => ToolApprovalMode.Never,
            ToolRisk.Destructive => ToolApprovalMode.Always,
            ToolRisk.Dynamic => ToolApprovalMode.Conditional,
            _ => ToolApprovalMode.Conditional,
        };
}
