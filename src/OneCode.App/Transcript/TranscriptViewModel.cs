namespace OneCode.App.Transcript;

/// <summary>
/// 对话区导航（Transcript 模式）共享视图模型。
/// 游标以「第 N 个可交互行」为锚（而非绝对行号）——展开/折叠会插入或移除
/// 详情行导致绝对行号漂移，但可交互行的序数保持稳定；每次移动前重新拉取
/// 可交互行快照并钳制游标，流式插入新行时也不会悬空。纯 C#、无 UI 依赖，
/// 便于单元测试，Web 宿主后续直接复用同一模型。
/// </summary>
public sealed class TranscriptViewModel
{
    private readonly Func<IReadOnlyList<int>> _interactiveLineProvider;
    private IReadOnlyList<int> _snapshot = [];

    /// <summary>当前游标指向的可交互行序数；-1 表示尚未定位。</summary>
    public int CursorOrdinal { get; private set; } = -1;

    /// <summary>当前游标对应的绝对行号；无可交互行时为 -1。</summary>
    public int CursorLine => CursorOrdinal >= 0 && CursorOrdinal < _snapshot.Count
        ? _snapshot[CursorOrdinal]
        : -1;

    /// <summary>当前快照中的可交互行数量。</summary>
    public int InteractiveCount => _snapshot.Count;

    public TranscriptViewModel(Func<IReadOnlyList<int>> interactiveLineProvider)
    {
        _interactiveLineProvider = interactiveLineProvider ?? throw new ArgumentNullException(nameof(interactiveLineProvider));
    }

    /// <summary>
    /// 重新拉取可交互行快照并把游标钳制到有效范围。
    /// 负值（未定位）保持原样——进入端由 <see cref="Move"/>/<see cref="MoveToEdge"/> 决定。
    /// </summary>
    public void Refresh()
    {
        _snapshot = _interactiveLineProvider();
        if (_snapshot.Count == 0)
        {
            CursorOrdinal = -1;
            return;
        }
        if (CursorOrdinal >= _snapshot.Count)
            CursorOrdinal = _snapshot.Count - 1;
    }

    /// <summary>按方向移动游标（+1 下一个 / -1 上一个）。返回是否发生了移动。</summary>
    public bool Move(int delta)
    {
        Refresh();
        if (_snapshot.Count == 0)
            return false;

        // 首次移动：从最近端进入列表，避免从 -1 跳过整段内容。
        if (CursorOrdinal < 0)
        {
            CursorOrdinal = delta > 0 ? 0 : _snapshot.Count - 1;
            return true;
        }

        var target = Math.Clamp(CursorOrdinal + Math.Sign(delta), 0, _snapshot.Count - 1);
        if (target == CursorOrdinal)
            return false;
        CursorOrdinal = target;
        return true;
    }

    /// <summary>跳到第一个（first）或最后一个（!first）可交互行。</summary>
    public bool MoveToEdge(bool first)
    {
        Refresh();
        if (_snapshot.Count == 0)
            return false;
        var target = first ? 0 : _snapshot.Count - 1;
        if (target == CursorOrdinal && CursorLine >= 0)
            return false;
        CursorOrdinal = target;
        return true;
    }

    /// <summary>清除游标（退出导航模式时调用）。</summary>
    public void Reset()
    {
        CursorOrdinal = -1;
        _snapshot = [];
    }
}
