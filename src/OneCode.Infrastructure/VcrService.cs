namespace OneCode.Infrastructure;

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

public static class VcrModeParser
{
    /// <summary>
    /// 解析 <c>ONECODE_VCR</c> 环境变量值为 <see cref="VcrMode"/>。
    /// <list type="bullet">
    /// <item>空 / "off"（不区分大小写）→ <see cref="VcrMode.Inactive"/></item>
    /// <item>"record"（不区分大小写）→ <see cref="VcrMode.Record"/></item>
    /// <item>"replay"（不区分大小写）→ <see cref="VcrMode.Replay"/></item>
    /// <item>其他任何非空值 → <see cref="VcrMode.Inactive"/>（fail-safe：拼错环境变量时关闭 VCR，
    /// 避免静默用过期 fixture 产生隐蔽 bug）</item>
    /// </list>
    /// </summary>
    public static VcrMode Parse(string? mode)
    {
        if (string.IsNullOrEmpty(mode))
            return VcrMode.Inactive;

        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            return VcrMode.Inactive;

        if (string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase))
            return VcrMode.Record;

        if (string.Equals(mode, "replay", StringComparison.OrdinalIgnoreCase))
            return VcrMode.Replay;

        return VcrMode.Inactive;
    }

    public static bool IsActive(this VcrMode mode) => mode != VcrMode.Inactive;

    public static bool IsRecording(this VcrMode mode) => mode == VcrMode.Record;
}
