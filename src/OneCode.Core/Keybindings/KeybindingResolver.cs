namespace OneCode.Core.Keybindings;

/// <summary>
/// 快捷键解析器，支持上下文优先级与和弦序列。
/// 维护和弦状态（pending 前缀），通过 Escape 或超时自动清除。
/// 线程安全：所有状态访问通过 <see cref="_lock"/> 同步。
/// </summary>
public sealed class KeybindingResolver
{
    /// <summary>和弦超时时间（毫秒）</summary>
    private const int ChordTimeoutMs = 1000;

    private readonly object _lock = new();
    private ParsedKeystroke[]? _pendingChord;
    private DateTime _chordStartTime;
    private KeybindingEntry[] _bindings = [];

    /// <summary>当前所有绑定条目（返回快照副本）</summary>
    public KeybindingEntry[] Bindings
    {
        get { lock (_lock) return _bindings; }
    }

    /// <summary>设置绑定条目（线程安全替换）</summary>
    public void SetBindings(KeybindingEntry[] bindings)
    {
        lock (_lock)
        {
            _bindings = bindings ?? [];
            _pendingChord = null;
        }
    }

    /// <summary>
    /// 解析按键输入，返回匹配结果。
    /// 支持上下文过滤、用户覆盖默认（后匹配的生效）、和弦序列。
    /// </summary>
    /// <param name="keyInput">键输入</param>
    /// <param name="activeContexts">活跃上下文集合</param>
    /// <returns>解析结果</returns>
    public KeyResolveReturn Resolve(IKeyInput keyInput, IReadOnlySet<string> activeContexts)
    {
        lock (_lock)
        {
            return ResolveInternal(keyInput, activeContexts);
        }
    }

    private KeyResolveReturn ResolveInternal(IKeyInput keyInput, IReadOnlySet<string> activeContexts)
    {
        if (_pendingChord is not null)
        {
            var elapsed = (DateTime.UtcNow - _chordStartTime).TotalMilliseconds;
            if (elapsed > ChordTimeoutMs)
            {
                _pendingChord = null;
            }
        }

        // escape 取消当前和弦
        if (keyInput.IsEscape && _pendingChord is not null)
        {
            _pendingChord = null;
            return new KeyResolveReturn(KeyResolveResult.ChordCancelled);
        }

        var currentKeystroke = KeybindingMatcher.BuildKeystroke(keyInput);
        if (currentKeystroke is null)
        {
            if (_pendingChord is not null)
            {
                _pendingChord = null;
                return new KeyResolveReturn(KeyResolveResult.ChordCancelled);
            }
            return new KeyResolveReturn(KeyResolveResult.None);
        }

        // 构建待测试的和弦序列
        ParsedKeystroke[] testChord;
        if (_pendingChord is not null)
        {
            testChord = [.. _pendingChord, currentKeystroke];
        }
        else
        {
            testChord = [currentKeystroke];
        }

        // 按上下文过滤绑定
        var contextBindings = _bindings
            .Where(b => activeContexts.Contains(b.Context))
            .ToList();

        var hasLongerChords = HasLongerChordMatches(testChord, contextBindings);

        if (hasLongerChords)
        {
            _pendingChord = testChord;
            _chordStartTime = DateTime.UtcNow;
            return new KeyResolveReturn(KeyResolveResult.ChordStarted);
        }

        KeybindingEntry? exactMatch = null;
        foreach (var binding in contextBindings)
        {
            if (ChordExactlyMatches(testChord, binding))
            {
                exactMatch = binding;
            }
        }

        if (exactMatch is not null)
        {
            _pendingChord = null;
            if (exactMatch.Action is null)
            {
                return new KeyResolveReturn(KeyResolveResult.Unbound);
            }
            return new KeyResolveReturn(KeyResolveResult.Match, exactMatch.Action);
        }

        // 无匹配且无更长和弦
        if (_pendingChord is not null)
        {
            _pendingChord = null;
            return new KeyResolveReturn(KeyResolveResult.ChordCancelled);
        }

        return new KeyResolveReturn(KeyResolveResult.None);
    }

    /// <summary>
    /// 检查是否有更长的和弦可以匹配当前前缀。
    /// 按和弦字符串分组，确保 null 覆盖（解绑）正确遮蔽默认绑定。
    /// </summary>
    private bool HasLongerChordMatches(ParsedKeystroke[] testChord, List<KeybindingEntry> contextBindings)
    {
        var chordWinners = new Dictionary<string, string?>();

        foreach (var binding in contextBindings)
        {
            if (binding.Chord.Length > testChord.Length &&
                ChordPrefixMatches(testChord, binding))
            {
                var chordKey = KeybindingParser.ChordToString(binding.Chord);
                chordWinners[chordKey] = binding.Action;
            }
        }

        foreach (var action in chordWinners.Values)
        {
            if (action is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 检查和弦前缀是否匹配某个绑定的和弦开头。
    /// </summary>
    private static bool ChordPrefixMatches(ParsedKeystroke[] prefix, KeybindingEntry binding)
    {
        if (prefix.Length >= binding.Chord.Length) return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            var prefixKey = prefix[i];
            var bindingKey = binding.Chord[i];
            if (prefixKey is null || bindingKey is null) return false;
            if (!KeybindingMatcher.KeystrokesEqual(prefixKey, bindingKey)) return false;
        }

        return true;
    }

    /// <summary>
    /// 检查和弦是否完全匹配某个绑定的和弦。
    /// </summary>
    private static bool ChordExactlyMatches(ParsedKeystroke[] chord, KeybindingEntry binding)
    {
        if (chord.Length != binding.Chord.Length) return false;

        for (var i = 0; i < chord.Length; i++)
        {
            var chordKey = chord[i];
            var bindingKey = binding.Chord[i];
            if (chordKey is null || bindingKey is null) return false;
            if (!KeybindingMatcher.KeystrokesEqual(chordKey, bindingKey)) return false;
        }

        return true;
    }
}
