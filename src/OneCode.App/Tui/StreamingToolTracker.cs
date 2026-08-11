namespace OneCode.App.Tui;

// 流式工具调用跟踪——从 ChatTranscriptView 提取。
// 封装 ToolId → 行索引 + 开始时间的映射，以及去重守卫（跨 ContinueStreaming 的重复 ToolDone）。
// 行列表的增删仍由 ChatTranscriptView 负责；本类仅管理匹配状态。

internal sealed class StreamingToolTracker
{
    private readonly Dictionary<string, (int LineIndex, long StartTick)> _pendingToolLines = new();
    private readonly HashSet<string> _seenToolIds = new();

    /// <summary>注册一个工具调用的开始，记录行索引和开始时间戳。</summary>
    /// <param name="toolId">工具调用 ID。</param>
    /// <param name="lineIndex">在 _streamingStatusLines 中的行索引。</param>
    public void RegisterStart(string toolId, int lineIndex)
    {
        _pendingToolLines[toolId] = (lineIndex, Stopwatch.GetTimestamp());
        _seenToolIds.Add(toolId);
    }

    /// <summary>
    /// 按 ToolId 精确匹配并移除挂起项。
    /// 调用方需检查返回的 LineIndex 是否仍在有效范围内。
    /// </summary>
    public bool TryMatchByToolId(string toolId, out (int LineIndex, long StartTick) entry)
    {
        return _pendingToolLines.TryGetValue(toolId, out entry)
            && _pendingToolLines.Remove(toolId);
    }

    /// <summary>检测该 ToolId 是否曾通过 RegisterStart 见过（即使已被 ContinueStreaming 提交到历史）。</summary>
    public bool WasSeen(string toolId)
        => _seenToolIds.Contains(toolId);

    /// <summary>
    /// Formats the elapsed time since <paramref name="startTick"/> as a plain duration string
    /// (e.g. <c>"420ms"</c> or <c>"1.2s"</c>). Callers wrap with their own formatting
    /// (parentheses, prefixes, etc.) to keep presentation consistent across tool calls,
    /// thinking markers, and other timing displays.
    /// </summary>
    public static string FormatDuration(long startTick)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTick);
        return elapsed.TotalMilliseconds < 1000
            ? $"{(int)elapsed.TotalMilliseconds}ms"
            : $"{elapsed.TotalSeconds:F1}s";
    }

    /// <summary>
    /// 将所有 <c>LineIndex &gt;= fromIndex</c> 的挂起项偏移 <paramref name="delta"/>。
    /// 用于思考块增删后校正其后工具行的索引，避免 ToolDone 更新到错误行。
    /// </summary>
    public void ShiftLineIndicesFrom(int fromIndex, int delta)
    {
        if (delta == 0 || _pendingToolLines.Count == 0) return;

        foreach (var key in _pendingToolLines.Keys.ToList())
        {
            var (lineIndex, startTick) = _pendingToolLines[key];
            if (lineIndex >= fromIndex)
                _pendingToolLines[key] = (lineIndex + delta, startTick);
        }
    }

    /// <summary>清空所有挂起项（用于 ContinueStreaming 重置轮次）。</summary>
    public void ClearPending() => _pendingToolLines.Clear();

    /// <summary>完全重置（用于 BeginStreaming/EndStreaming）。</summary>
    public void Clear()
    {
        _pendingToolLines.Clear();
        _seenToolIds.Clear();
    }
}
