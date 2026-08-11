namespace OneCode.Core.Tasks;

public sealed record TaskMetadata(
    string? Name = null,
    string? Description = null,
    string? WorkingDirectory = null,
    string? ParentTaskId = null,
    Dictionary<string, string>? ExtraProperties = null);
