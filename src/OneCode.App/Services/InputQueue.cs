namespace OneCode.App.Services;

/// <summary>
/// 队列中的单条输入——包含文本和可选的图片路径列表。
/// 图片由 <c>OnUserSubmitted</c> 通过 <c>TakePendingImages</c> 从 Prompt 中取出后一并入队，
/// 出队时原样传递给 <c>HandleSubmitCoreAsync</c>，避免排队提交时图片丢失。
/// </summary>
public sealed record QueuedInput(string Text, IReadOnlyList<string>? Images = null);

/// <summary>
/// 输入队列——统一的"对话完成后自动取下一条继续执行"机制。
///
/// 合并了原 <c>OneCodeToplevel._commandQueue</c>（斜杠命令队列）和 prompt 队列的需求：
///   - query 运行时用户输入（斜杠命令或自然语言）自动入队，不打断当前 query
///   - query 完成后自动按 FIFO 顺序出队执行
///   - 用户可通过 <c>/queue</c> 命令主动预排 prompt
///
/// <b>不持久化、不按对话隔离</b>：进程内单队列，切换对话时不清空（但队列内容在当前对话上下文中执行）。
/// 队列中的输入是上下文依赖的，跨会话恢复无意义，故仅内存存储。
/// </summary>
public sealed class InputQueue
{
    private readonly Queue<QueuedInput> _queue = new();
    private readonly object _lock = new();
    private readonly ILogger<InputQueue>? _logger;

    public InputQueue(ILogger<InputQueue>? logger = null) => _logger = logger;

    /// <summary>
    /// 添加输入到队列末尾。
    /// </summary>
    /// <param name="text">输入文本。</param>
    /// <param name="images">可选的图片路径列表，出队时原样传递给执行路径。</param>
    public void Enqueue(string text, IReadOnlyList<string>? images = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        lock (_lock)
        {
            _queue.Enqueue(new QueuedInput(text, images));
            _logger?.LogDebug("Input enqueued (count={Count}): {Preview}", _queue.Count, Truncate(text, 60));
        }
    }

    /// <summary>
    /// 取出并移除队首输入。队列空时返回 null。
    /// </summary>
    public QueuedInput? Dequeue()
    {
        lock (_lock)
        {
            if (_queue.Count == 0)
                return null;

            var item = _queue.Dequeue();
            _logger?.LogDebug("Input dequeued (remaining={Count}): {Preview}", _queue.Count, Truncate(item.Text, 60));
            return item;
        }
    }

    /// <summary>
    /// 返回当前队列快照（不移除）。索引 0 为队首。
    /// </summary>
    public IReadOnlyList<QueuedInput> PeekAll()
    {
        lock (_lock)
        {
            return _queue.ToArray();
        }
    }

    /// <summary>
    /// 清空队列。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _logger?.LogDebug("Input queue cleared");
        }
    }

    /// <summary>
    /// 移除指定索引处的输入。索引越界时返回 false。
    /// </summary>
    public bool RemoveAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _queue.Count)
                return false;

            // Queue 不支持按索引删除——快照后重建
            var snapshot = _queue.ToArray();
            _queue.Clear();
            for (var i = 0; i < snapshot.Length; i++)
            {
                if (i != index)
                    _queue.Enqueue(snapshot[i]);
            }

            _logger?.LogDebug("Removed input at index {Index} (remaining={Count})", index, _queue.Count);
            return true;
        }
    }

    /// <summary>
    /// 返回队列长度。
    /// </summary>
    public int Count
    {
        get { lock (_lock) { return _queue.Count; } }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
