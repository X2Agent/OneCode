using OneCode.Core.Domain;

namespace OneCode.Core.Exec;

/// <summary>Shell 执行请求，不携带具体执行后端类型。</summary>
/// <remarks>
/// <see cref="AdditionalDirectories"/> 为会话经 /add-dir 授权的额外根目录，
/// 持久 shell 的 cwd 围栏用它判定「越界」，缺省为 null（仅工作目录本身合法）；
/// Stateless/SSH 后端忽略此参数。
/// </remarks>
public sealed record ShellExecutionRequest(
    string Command,
    string WorkingDirectory,
    string Shell,
    int TimeoutSeconds,
    SessionId? ConversationId = null,
    IReadOnlyList<string>? AdditionalDirectories = null);
