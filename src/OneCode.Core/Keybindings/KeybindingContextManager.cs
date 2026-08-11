namespace OneCode.Core.Keybindings;

/// <summary>
/// 上下文管理器，管理活跃上下文集合。
/// 上下文优先级：注册的活跃上下文 > 焦点视图上下文 > Global。
/// </summary>
public sealed class KeybindingContextManager
{
    private readonly HashSet<string> _activeContexts = new();
    private string? _focusContext;
    private readonly object _lock = new();

    /// <summary>
    /// 当前活跃的上下文集合（包含 Global）。
    /// </summary>
    public IReadOnlySet<string> ActiveContexts
    {
        get
        {
            lock (_lock)
            {
                var result = new HashSet<string>(_activeContexts);
                if (_focusContext is not null)
                {
                    result.Add(_focusContext);
                }
                // Global 始终活跃
                result.Add(KeybindingDefaults.ContextGlobal);
                return result;
            }
        }
    }

    /// <summary>
    /// 当前焦点视图上下文。
    /// </summary>
    public string? FocusContext
    {
        get
        {
            lock (_lock)
            {
                return _focusContext;
            }
        }
        set
        {
            lock (_lock)
            {
                _focusContext = value;
            }
        }
    }

    /// <summary>
    /// 推入一个活跃上下文。
    /// </summary>
    public void PushContext(string context)
    {
        if (!KeybindingDefaults.IsValidContext(context))
        {
            return;
        }

        lock (_lock)
        {
            _activeContexts.Add(context);
        }
    }

    /// <summary>
    /// 弹出一个活跃上下文。
    /// </summary>
    public void PopContext(string context)
    {
        lock (_lock)
        {
            _activeContexts.Remove(context);
        }
    }
}
