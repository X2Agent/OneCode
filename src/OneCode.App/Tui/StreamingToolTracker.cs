namespace OneCode.App.Tui;

// 流式工具调用跟踪：封装 ToolId → 行索引 + 开始时间的映射，以及去重守卫
// （跨 ContinueStreaming 的重复 ToolDone）。行列表增删仍由 ChatTranscriptView 负责。
// 另维护"同类工具组"（同轮相邻同名的完成行合并为组行，×N 递增）。

internal sealed class StreamingToolTracker
{
    private readonly Dictionary<string, (int LineIndex, long StartTick)> _pendingToolLines = new();
    private readonly HashSet<string> _seenToolIds = new();

    // 同类工具组：key = 工具名 + 代理归属（同名同归属才并组），value = 组行索引 + 成员。
    private readonly Dictionary<string, ToolGroupState> _groups = new(StringComparer.Ordinal);
    // 最近一次完成并入组的工具键——组只在"相邻同类"时延续，异名工具完成即断开。
    private string? _lastGroupKey;

    // 活跃子代理 fork 行：agentName → 行状态。Agent 工具调用期间接收
    // SubagentProgress 更新，原位重渲头行 + 实时活动行；工具完成即注销。
    private readonly Dictionary<string, SubagentLineState> _subagentLines = new(StringComparer.Ordinal);

    /// <summary>
    /// 一条活跃子代理进度行的登记状态：头行索引 + 折叠行目标文案（"Explore · 描述"，
    /// 进度刷新时原样保留）+ 实时活动行索引（-1 = 尚未插入）+ 最新进度快照
    /// （头行 tag 携带，展开时显示实时统计）。
    /// </summary>
    internal sealed record SubagentLineState(int LineIndex, string HeaderTarget)
    {
        public int LineIndex { get; set; } = LineIndex;
        public int ActivityLineIndex { get; set; } = -1;
        public TuiSubagentProgress? LatestProgress { get; set; }
    }

    /// <summary>登记一条活跃子代理进度行（按代理名索引，同名覆盖——同名并发 fork 极少见）。</summary>
    public void RegisterSubagentLine(string agentName, int lineIndex, string headerTarget)
        => _subagentLines[agentName] = new SubagentLineState(lineIndex, headerTarget);

    /// <summary>查找活跃子代理进度行；不存在返回 false。</summary>
    public bool TryGetSubagentLine(string agentName, out SubagentLineState state)
        => _subagentLines.TryGetValue(agentName, out state!);

    /// <summary>注销子代理进度行（Agent 工具调用完成时调用）。</summary>
    public void RemoveSubagentLine(string agentName) => _subagentLines.Remove(agentName);

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
    /// 尝试把一条已完成调用并入当前活跃的同类组（组存在、且上次完成的就是该组）。
    /// 成功时返回组行索引与成员快照（含新成员），调用方原位重渲组行。
    /// </summary>
    public bool TryAppendToGroup(
        string groupKey, string? args, string? result, long startTick, bool isError,
        out int lineIndex, out IReadOnlyList<ToolGroupEntry> members, out string totalDuration)
    {
        if (!string.Equals(_lastGroupKey, groupKey, StringComparison.Ordinal)
            || !_groups.TryGetValue(groupKey, out var group))
        {
            lineIndex = -1;
            members = [];
            totalDuration = string.Empty;
            return false;
        }

        group.Members.Add((args, result, startTick, isError));
        _lastGroupKey = groupKey;
        lineIndex = group.LineIndex;
        members = SnapshotMembers(group);
        totalDuration = FormatElapsed(TotalElapsed(group));
        return true;
    }

    /// <summary>以一条已完成调用开新组（首条成员即组行，渲染形态与单条完成行一致）。</summary>
    public void RegisterGroup(string groupKey, int lineIndex, string? args, string? result, long startTick, bool isError)
    {
        _groups[groupKey] = new ToolGroupState(lineIndex, [(args, result, startTick, isError)]);
        _lastGroupKey = groupKey;
    }

    private static IReadOnlyList<ToolGroupEntry> SnapshotMembers(ToolGroupState group)
        => group.Members
            .Select(member => new ToolGroupEntry(
                member.Args, member.Result, FormatElapsed(Stopwatch.GetElapsedTime(member.StartTick)), member.IsError))
            .ToList();

    private static TimeSpan TotalElapsed(ToolGroupState group)
        => group.Members.Aggregate(TimeSpan.Zero, (acc, member) => acc + Stopwatch.GetElapsedTime(member.StartTick));

    /// <summary>
    /// Formats the elapsed time since <paramref name="startTick"/> as a plain duration string
    /// (e.g. <c>"420ms"</c> or <c>"1.2s"</c>). Callers wrap with their own formatting
    /// (parentheses, prefixes, etc.) to keep presentation consistent across tool calls,
    /// thinking markers, and other timing displays.
    /// </summary>
    public static string FormatDuration(long startTick) => FormatElapsed(Stopwatch.GetElapsedTime(startTick));

    /// <summary>格式化一段已确定的时长（组累计、单条耗时共用）。</summary>
    public static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalMilliseconds < 1000
            ? $"{(int)elapsed.TotalMilliseconds}ms"
            : $"{elapsed.TotalSeconds:F1}s";

    /// <summary>
    /// 将所有 <c>LineIndex &gt;= fromIndex</c> 的挂起项偏移 <paramref name="delta"/>。
    /// 用于思考块增删后校正其后工具行的索引，避免 ToolDone 更新到错误行。
    /// 组行索引同样偏移。
    /// </summary>
    public void ShiftLineIndicesFrom(int fromIndex, int delta)
    {
        if (delta == 0)
            return;

        foreach (var key in _pendingToolLines.Keys.ToList())
        {
            var (lineIndex, startTick) = _pendingToolLines[key];
            if (lineIndex >= fromIndex)
                _pendingToolLines[key] = (lineIndex + delta, startTick);
        }

        foreach (var group in _groups.Values)
        {
            if (group.LineIndex >= fromIndex)
                group.LineIndex += delta;
        }

        foreach (var key in _subagentLines.Keys.ToList())
        {
            var state = _subagentLines[key];
            if (state.LineIndex >= fromIndex)
                state.LineIndex += delta;
            if (state.ActivityLineIndex >= fromIndex)
                state.ActivityLineIndex += delta;
        }
    }

    /// <summary>清空所有挂起项与活跃组（用于 ContinueStreaming 重置轮次——组不跨轮延续）。</summary>
    public void ClearPending()
    {
        _pendingToolLines.Clear();
        _groups.Clear();
        _lastGroupKey = null;
        _subagentLines.Clear();
    }

    /// <summary>完全重置（用于 BeginStreaming/EndStreaming）。</summary>
    public void Clear()
    {
        _pendingToolLines.Clear();
        _seenToolIds.Clear();
        _groups.Clear();
        _lastGroupKey = null;
        _subagentLines.Clear();
    }

    private sealed class ToolGroupState(int lineIndex, List<(string? Args, string? Result, long StartTick, bool IsError)> members)
    {
        public int LineIndex { get; set; } = lineIndex;
        public List<(string? Args, string? Result, long StartTick, bool IsError)> Members { get; } = members;
    }
}
