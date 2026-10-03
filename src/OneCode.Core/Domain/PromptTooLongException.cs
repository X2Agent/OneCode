namespace OneCode.Core.Domain;

/// <summary>
/// 当检测到 prompt-too-long 条件（从工具结果或异常）时抛出，
/// 由上层 Runner 捕获以触发激进压缩 + 模型回退恢复管道。
/// </summary>
public sealed class PromptTooLongException(string message) : Exception(message);
