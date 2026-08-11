namespace OneCode.Infrastructure.Skills;

/// <summary>Parsed metadata and body for a SKILL.md document.</summary>
public sealed record SkillDocument(
    string Name,
    string Description,
    string Body,
    string? ArgumentHint = null,
    IReadOnlyList<string>? ArgumentNames = null,
    bool UserInvocable = true,
    bool DisableModelInvocation = false)
{
    public IReadOnlyList<string> ArgumentNames { get; init; } = ArgumentNames ?? [];
}

internal sealed class SkillFrontmatterRaw
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ArgumentHint { get; set; }
    public string[]? ArgumentNames { get; set; }
    public bool? UserInvocable { get; set; }
    public bool? DisableModelInvocation { get; set; }
}
