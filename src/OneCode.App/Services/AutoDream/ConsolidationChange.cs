namespace OneCode.App.Services.AutoDream;

/// <summary>Agent 输出的单条增量变更（待合并写入 MEMORY.md）。</summary>
internal sealed record ConsolidationChange(
    string Action,
    string Scope,
    string Key,
    string? Value,
    int? TtlHours);
