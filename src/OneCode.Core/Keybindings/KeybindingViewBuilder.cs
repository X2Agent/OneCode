namespace OneCode.Core.Keybindings;

/// <summary>
/// 生效绑定的来源分类。
/// </summary>
public enum KeybindingSource
{
    /// <summary>与默认绑定一致。</summary>
    Default,

    /// <summary>用户覆盖默认或新增的绑定。</summary>
    Custom,

    /// <summary>用户显式解绑（action 为 null）。</summary>
    Unbound,
}

/// <summary>
/// 可显示的生效绑定视图条目。
/// </summary>
public sealed record KeybindingView(
    string Context,
    string KeyDisplay,
    string? Action,
    KeybindingSource Source);

/// <summary>
/// 将合并后的绑定条目（默认在前、用户在后，后匹配生效）转换为显示视图：
/// 按 (Context, 规范化按键) 去重取最后一条，并与默认绑定对比标记来源。
/// 供 TUI overlay 与命令文本输出共用。
/// </summary>
public static class KeybindingViewBuilder
{
    public static IReadOnlyList<KeybindingView> Build(IReadOnlyList<KeybindingEntry> mergedBindings)
    {
        // 同一 (context, key) 保留最后一条——用户绑定追加在默认之后，后匹配的生效。
        var effective = new Dictionary<(string Context, string Key), KeybindingEntry>();
        foreach (var entry in mergedBindings)
        {
            var key = NormalizeEntryKey(entry);
            effective[(entry.Context, key)] = entry;
        }

        // 默认绑定索引：(context, key) → action
        var defaults = new Dictionary<(string Context, string Key), string?>();
        foreach (var entry in KeybindingDefaults.GetDefaultParsedBindings())
        {
            defaults[(entry.Context, NormalizeEntryKey(entry))] = entry.Action;
        }

        var views = new List<KeybindingView>(effective.Count);
        foreach (var ((context, _), entry) in effective)
        {
            var source = Classify(entry, defaults);
            views.Add(new KeybindingView(
                context,
                ToTitleCase(KeybindingParser.ChordToDisplayString(entry.Chord)),
                entry.Action,
                source));
        }

        // 按 KeybindingDefaults.AllContexts 的顺序分组，组内按按键排序，保证稳定输出。
        var contextOrder = new Dictionary<string, int>(
            KeybindingDefaults.AllContexts.Select((c, i) => new KeyValuePair<string, int>(c, i)),
            StringComparer.Ordinal);
        return views
            .OrderBy(v => contextOrder.TryGetValue(v.Context, out var order) ? order : int.MaxValue)
            .ThenBy(v => v.Context, StringComparer.Ordinal)
            .ThenBy(v => v.KeyDisplay, StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeEntryKey(KeybindingEntry entry) =>
        KeybindingParser.NormalizeKeyForComparison(KeybindingParser.ChordToString(entry.Chord));

    private static KeybindingSource Classify(
        KeybindingEntry entry,
        IReadOnlyDictionary<(string Context, string Key), string?> defaults)
    {
        if (entry.Action is null)
            return KeybindingSource.Unbound;

        return defaults.TryGetValue((entry.Context, NormalizeEntryKey(entry)), out var defaultAction)
            && defaultAction == entry.Action
            ? KeybindingSource.Default
            : KeybindingSource.Custom;
    }

    /// <summary>
    /// "ctrl+shift+d" → "Ctrl+Shift+D"；和弦保留空格分隔（"Ctrl+X Ctrl+K"）。
    /// </summary>
    internal static string ToTitleCase(string keyDisplay)
    {
        return string.Join(' ', keyDisplay.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => string.Join('+', part.Split('+')
                .Select(p => p.Length <= 1
                    ? p.ToUpperInvariant()
                    : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()))));
    }
}
