namespace OneCode.Core.Domain;

/// <summary>单次工具调用的结构化记录。</summary>
public sealed record ToolCallRecord(
    string ToolName,
    string? TargetFilePath,
    bool IsSuccess,
    DateTimeOffset Timestamp,
    TimeSpan Duration);
