namespace OneCode.WebUI.Services;

/// <summary>流式尾段字级错峰的渲染计划：稳定前缀（纯文本，Razor 原样输出自动转义）+ 带 span 的 token。</summary>
public sealed record StreamTailPlan(string StableText, IReadOnlyList<TailSpan> Spans);

/// <summary>一个错峰 span：绝对字符偏移作 Blazor <c>@key</c>（跨渲染保住 DOM，动画不被打断）、错峰索引、显示文本。</summary>
public sealed record TailSpan(int Offset, int Index, string Text);

/// <summary>
/// 流式尾段的字级错峰状态机（每条 <see cref="WebMessage"/> 流式期间由 MessageList 持有）：
/// 为新增字符分配 span 与同一增量内的错峰索引；键 = 绝对字符偏移，跨渲染稳定，
/// Blazor 按 key 复用 DOM 节点，已在播的「模糊→清晰」动画不会被重建打断。
/// 窗口（<see cref="Backlog"/>）外的旧 span 收敛为纯文本，防止长段落 span 无界增长。
/// </summary>
public sealed class StreamingTailState
{
    /// <summary>同一增量内错峰索引上限：16 × 30ms = 480ms，长增量不滞显。</summary>
    public const int MaxStagger = 16;

    /// <summary>span 保留窗口（字符）：远超任何错峰动画的完成时长，收敛不产生可见跳变。</summary>
    private const int Backlog = 600;

    private readonly Dictionary<int, int> _indexByOffset = [];
    private WebMessage? _owner;
    private int _shown;

    /// <summary>
    /// 生成纯文本尾段的渲染计划。调用前须已用
    /// <see cref="StreamingTextTokenizer.IsPlainText(string)"/> 判定尾段为纯文本。
    /// </summary>
    /// <param name="owner">流式消息实例：换消息即整体复位。</param>
    /// <param name="tailStart">尾段在完整内容中的绝对起始偏移。</param>
    /// <param name="tail">尾段文本。</param>
    public StreamTailPlan Plan(WebMessage owner, int tailStart, string tail)
    {
        ResetIfOwnerChanged(owner);

        var tailEnd = tailStart + tail.Length;

        // 上轮已展示长度之后的字符是本轮增量：在增量内分配 0..n 的错峰索引。
        var freshStart = Math.Clamp(_shown, tailStart, tailEnd);
        for (var offset = freshStart; offset < tailEnd; offset++)
            _indexByOffset[offset] = Math.Min(offset - freshStart, MaxStagger);
        _shown = tailEnd;

        // 收敛：块轮转残留（键 < tailStart）与窗口外的键清除，对应字符回落纯文本。
        var keepFrom = Math.Max(tailStart, tailEnd - Backlog);
        List<int>? stale = null;
        foreach (var key in _indexByOffset.Keys)
        {
            if (key < keepFrom)
                (stale ??= []).Add(key);
        }
        if (stale is not null)
            foreach (var key in stale)
                _indexByOffset.Remove(key);

        if (_indexByOffset.Count == 0)
            return new StreamTailPlan(tail, []);

        var firstSpan = _indexByOffset.Keys.Min();
        var stableLength = firstSpan - tailStart;
        var spans = new List<TailSpan>();
        var consumed = stableLength;
        foreach (var token in StreamingTextTokenizer.Tokenize(tail[stableLength..]))
        {
            // token 首字符的键即该 span 的 key 与错峰索引；token 携带自身尾随空白。
            spans.Add(new TailSpan(tailStart + consumed, _indexByOffset[tailStart + consumed], token));
            consumed += token.Length;
        }

        return new StreamTailPlan(tail[..stableLength], spans);
    }

    /// <summary>尾段走整段 Markdown 渲染（或无尾段）时：标记全部已展示并清掉 span 状态。</summary>
    public void MarkShown(WebMessage owner, int contentLength)
    {
        ResetIfOwnerChanged(owner);
        _indexByOffset.Clear();
        _shown = contentLength;
    }

    /// <summary>会话切换时整体复位，防止跨会话残留的偏移状态。</summary>
    public void Reset()
    {
        _indexByOffset.Clear();
        _owner = null;
        _shown = 0;
    }

    private void ResetIfOwnerChanged(WebMessage owner)
    {
        if (ReferenceEquals(_owner, owner))
            return;

        _owner = owner;
        _indexByOffset.Clear();
        _shown = 0;
    }
}
