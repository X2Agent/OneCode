using OneCode.Core.Domain;

namespace OneCode.Core.Exec;

/// <summary>Shell 执行请求，不携带具体执行后端类型。</summary>
public sealed record ShellExecutionRequest(
    string Command,
    string WorkingDirectory,
    string Shell,
    int TimeoutSeconds,
    SessionId? ConversationId = null);