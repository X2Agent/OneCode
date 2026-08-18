namespace OneCode.Core.Commands;

/// <summary>
/// Core command contract.
/// 元数据成员（Category/Aliases 等）以默认接口成员（DIM）形式内联在本接口中，
/// 具体命令通常继承 <see cref="Command"/> 基类并仅 override 需要的成员。
/// （原 ICommandMetadata 可选接口已并入本接口——所有命令经 Command 基类均具备元数据，
/// 消费方不再需要 is-check 模式。）
/// </summary>
public interface ICommand
{
    string Name { get; }

    string Description { get; }

    Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default);

    CommandCategory Category => CommandCategory.Builtin;

    /// <summary>命令别名列表（不含前导 /）</summary>
    IReadOnlyList<string> Aliases => Array.Empty<string>();

    bool IsHidden => false;

    bool IsEnabled() => true;

    /// <summary>参数提示（显示在补全 UI 中）</summary>
    string? ArgumentHint => null;

    string? ProgressMessage => null;

    /// <summary>如果为 true，则绕过 query 队列立即执行</summary>
    bool Immediate => false;

    CommandSource Source => CommandSource.Builtin;
}

/// <summary>
/// Result of executing a command, expressed as a discriminated union.
/// </summary>
public abstract record CommandResult
{
    public sealed record TextResult(string Value) : CommandResult;
    public sealed record SilentResult() : CommandResult;
    public sealed record ExitResult() : CommandResult;
    public sealed record PromptResult(string Content, string[]? AllowedTools = null) : CommandResult;
    public sealed record ErrorResult(string Message) : CommandResult;

    /// <summary>
    /// Resumes a durable workflow (Goal/Team) from a checkpoint.
    /// The dispatch layer routes this directly to the appropriate workflow resume stream,
    /// bypassing the LLM query pipeline.
    /// </summary>
    public sealed record ResumeWorkflowResult(string SessionId, WorkflowResumeKind Kind) : CommandResult;

    public static CommandResult Text(string value) => new TextResult(value);
    public static CommandResult Silent() => new SilentResult();
    public static CommandResult Exit() => new ExitResult();
    public static CommandResult Prompt(string content, string[]? allowedTools = null) => new PromptResult(content, allowedTools);
    public static CommandResult Error(string message) => new ErrorResult(message);
    public static CommandResult ResumeWorkflow(string sessionId, WorkflowResumeKind kind) => new ResumeWorkflowResult(sessionId, kind);
}

/// <summary>
/// Identifies the kind of durable workflow to resume.
/// </summary>
public enum WorkflowResumeKind
{
    Goal,
    Team
}

public enum CommandSource
{
    Builtin,
    Skill,
    Mcp,
    Workflow,
    Dynamic
}
