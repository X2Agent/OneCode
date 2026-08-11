namespace OneCode.Core.Domain;

/// <summary>
/// 当检测到 prompt-too-long 条件（从工具结果或异常）时抛出，
/// 由上层 Runner 捕获以触发激进压缩 + 模型回退恢复管道。
///
/// 从 App/MainAgentRunner 下沉到 Core.Domain，使 Infrastructure 层的
/// PromptTooLongRecoveryRunMiddleware 可直接引用，避免反向依赖。
/// </summary>
public sealed class PromptTooLongException : Exception
{
    public PromptTooLongException(string message) : base(message) { }
}
