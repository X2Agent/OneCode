namespace OneCode.Core.Ai;

/// <summary>
/// VCR 模式。决定 HTTP/LLM 请求是录制还是回放。
/// </summary>
public enum VcrMode
{
    /// <summary>VCR 未激活，所有请求走真实网络。</summary>
    Inactive,

    /// <summary>回放模式：命中 fixture 时返回缓存；未命中走真实网络但不录制。</summary>
    Replay,

    /// <summary>录制模式：真实响应被写入 fixture 文件。</summary>
    Record,
}

public static class VcrModeExtensions
{
    public static bool IsActive(this VcrMode mode) => mode != VcrMode.Inactive;

    public static bool IsRecording(this VcrMode mode) => mode == VcrMode.Record;
}
