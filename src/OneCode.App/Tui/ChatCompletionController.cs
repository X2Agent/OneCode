namespace OneCode.App.Tui;

/// <summary>
/// 补全列表中的命令条目 —— 内置命令 / 技能 / MCP 共用此结构。
/// 携带 <see cref="Source"/> 以支持分组显示（Claude Code 风格）：
/// 输入 / 时补全列表按来源分组，用分隔行区分。
/// </summary>
public sealed record SlashCommandEntry(string Name, string Description, CommandSource Source = CommandSource.Builtin);

/// <summary>
/// Manages command and file-path completion for <see cref="ChatInputView"/>.
/// Extracted from ChatInputView to isolate completion state and filtering logic.
/// </summary>
internal sealed class ChatCompletionController
{
    private IReadOnlyList<SlashCommandEntry> _allCommands;
    private List<SlashCommandEntry> _filtered = [];
    private List<string>? _genericCompletions;
    private List<string>? _genericDisplayItems;
    private readonly TypeaheadCompletionEngine? _typeaheadEngine;

    private bool _isCompletionVisible;
    private int _selectedIndex;

    public ChatCompletionController(
        IReadOnlyList<SlashCommandEntry> commands,
        TypeaheadCompletionEngine? typeaheadEngine)
    {
        _allCommands = commands;
        _typeaheadEngine = typeaheadEngine;
    }

    /// <summary>
    /// Replaces the command list used for slash-completion at runtime.
    /// Call after dynamic commands (skills, MCP) are loaded or refreshed.
    /// </summary>
    public void UpdateCommands(IReadOnlyList<SlashCommandEntry> commands)
    {
        _allCommands = commands;
        _typeaheadEngine?.UpdateCommands(commands);
    }

    // Public state
    public bool IsCompletionActive => _isCompletionVisible;
    public int SelectedIndex => _selectedIndex;
    public IReadOnlyList<SlashCommandEntry> FilteredCommands => _filtered;
    public List<string>? GenericCompletions => _genericCompletions;

    /// <summary>Fires when visibility or height of the completion popup changes. Bool=true means visible.</summary>
    public event Action<bool, int>? CompletionStateChanged;

    // Slash-command completion

    /// <summary>
    /// 分隔行标记：Name 以此常量开头表示这是一个分组标题，不可被选中。
    /// </summary>
    private const string SeparatorPrefix = "\u0000SECTION\u0000";

    /// <summary>
    /// 续行标记：当描述过长换行时，续行的 Name 以此常量开头（后接主命令名），
    /// 用于在 _filtered 中标记不可选中的续行条目。
    /// </summary>
    private const string ContinuationPrefix = "\u0000CONT\u0000";

    /// <summary>Source → 分组标题（按显示顺序排列）。</summary>
    private static readonly (CommandSource Source, string Label)[] GroupOrder =
    [
        (CommandSource.Builtin,  "命令"),
        (CommandSource.Skill,    "技能"),
        (CommandSource.Mcp,      "MCP"),
        (CommandSource.Workflow, "工作流"),
        (CommandSource.Dynamic,  "动态"),
    ];

    public void UpdateCompletionList(string prefix)
    {
        var query = prefix.TrimStart('/').ToLowerInvariant();

        var matched = query.Length == 0
            ? _allCommands.ToList()
            : _allCommands
                .Where(c =>
                    c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (matched.Count == 0)
        {
            Hide();
            return;
        }

        // 按 Source 分组，每组前插入分隔行（Claude Code 风格）。
        var grouped = new List<SlashCommandEntry>();
        foreach (var (source, label) in GroupOrder)
        {
            var group = matched.Where(c => c.Source == source).ToList();
            if (group.Count == 0) continue;
            grouped.Add(new SlashCommandEntry($"{SeparatorPrefix}{label}", label, source));
            grouped.AddRange(group);
        }
        // 兜底：未归入任何已知分组的命令（理论上不会出现）。
        var ungrouped = matched.Where(c => !GroupOrder.Any(g => g.Source == c.Source)).ToList();
        if (ungrouped.Count > 0)
        {
            grouped.Add(new SlashCommandEntry($"{SeparatorPrefix}其他", "其他", CommandSource.Dynamic));
            grouped.AddRange(ungrouped);
        }

        // 动态计算命令名列宽度（按显示宽度，兼容中文等宽字符），限制在 7~24 列。
        var maxNameWidth = 0;
        foreach (var c in grouped)
        {
            if (IsSeparator(c)) continue;
            var w = DisplayWidth(c.Name);
            if (w > maxNameWidth) maxNameWidth = w;
        }
        var nameWidth = Math.Clamp(maxNameWidth, 7, 24);

        // 计算描述可用宽度：
        //   弹窗内容区 ≈ 终端宽度 - 4（左边距1 + 左右边框2 + Dim.Fill(-1)）
        //   描述起始列 = "/" + nameWidth + " " = nameWidth + 2
        //   描述可用宽度 = 内容区宽度 - 描述起始列
        // 当终端过窄（可用宽度 < 20）时不换行，交由 ListView 自行截断。
        var consoleWidth = TryGetConsoleWidth();
        var descAvailable = consoleWidth > 0
            ? consoleWidth - 4 - nameWidth - 2
            : 0;
        if (descAvailable < 20) descAvailable = 0;

        // 构建 _filtered（含续行）和 displayItems。
        // 长描述按 descAvailable 做自动换行，续行缩进对齐到描述列，且不可被选中。
        _filtered = [];
        var displayItems = new List<string>();
        var indent = new string(' ', nameWidth + 2);

        foreach (var entry in grouped)
        {
            _filtered.Add(entry);

            if (IsSeparator(entry))
            {
                displayItems.Add($"  ── {entry.Description} ──");
                continue;
            }

            var cmdPart = $"/{PadRightDisplay(entry.Name, nameWidth)}";

            if (descAvailable <= 0 || DisplayWidth(entry.Description) <= descAvailable)
            {
                displayItems.Add($"{cmdPart} {entry.Description}");
                continue;
            }

            var descLines = WordWrap(entry.Description, descAvailable);
            displayItems.Add($"{cmdPart} {descLines[0]}");
            for (var j = 1; j < descLines.Count; j++)
            {
                _filtered.Add(new SlashCommandEntry(
                    $"{ContinuationPrefix}{entry.Name}", descLines[j], entry.Source));
                displayItems.Add($"{indent}{descLines[j]}");
            }
        }

        _genericCompletions = null;
        _selectedIndex = NextSelectable(0, forward: true);

        var height = Math.Min(_filtered.Count + 2, 18);
        ShowPopup(displayItems, height);
    }

    private static bool IsSeparator(SlashCommandEntry c)
        => c.Name.StartsWith(SeparatorPrefix, StringComparison.Ordinal);

    /// <summary>分隔行或续行——均不可被选中（导航时跳过）。</summary>
    private static bool IsNonSelectable(SlashCommandEntry c)
        => IsSeparator(c)
        || c.Name.StartsWith(ContinuationPrefix, StringComparison.Ordinal);

    // 文本换行与显示宽度计算

    private static int TryGetConsoleWidth()
    {
        try
        {
            var w = Console.WindowWidth;
            return w > 0 ? w : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>估算字符在终端中的显示宽度（CJK 等宽字符为 2，其余为 1）。</summary>
    private static int CharWidth(char c)
    {
        if (c < 0x20 || c == 0x7F) return 0;
        if (c >= 0x1100 && (
            c <= 0x115F ||
            (c >= 0x2E80 && c <= 0x303E) ||
            (c >= 0x3041 && c <= 0x33FF) ||
            (c >= 0x3400 && c <= 0x4DBF) ||
            (c >= 0x4E00 && c <= 0x9FFF) ||
            (c >= 0xA000 && c <= 0xA4CF) ||
            (c >= 0xAC00 && c <= 0xD7A3) ||
            (c >= 0xF900 && c <= 0xFAFF) ||
            (c >= 0xFE30 && c <= 0xFE4F) ||
            (c >= 0xFF00 && c <= 0xFF60) ||
            (c >= 0xFFE0 && c <= 0xFFE6)))
            return 2;
        return 1;
    }

    private static int DisplayWidth(string s)
    {
        var width = 0;
        foreach (var c in s)
            width += CharWidth(c);
        return width;
    }

    /// <summary>按显示宽度右填充空格，使后续文本对齐。</summary>
    private static string PadRightDisplay(string s, int targetWidth)
    {
        var current = DisplayWidth(s);
        return current >= targetWidth ? s : s + new string(' ', targetWidth - current);
    }

    /// <summary>
    /// 按显示宽度对文本进行自动换行，尽量在空格处断行；
    /// 无空格时（如中文）按字符断行。
    /// </summary>
    private static List<string> WordWrap(string text, int maxWidth)
    {
        if (maxWidth <= 0)
            return string.IsNullOrEmpty(text) ? [""] : [text];
        if (string.IsNullOrEmpty(text))
            return [""];

        var lines = new List<string>();
        var pos = 0;

        while (pos < text.Length)
        {
            var remaining = text[pos..];
            if (DisplayWidth(remaining) <= maxWidth)
            {
                lines.Add(remaining);
                break;
            }

            // 找到不超过 maxWidth 的最大子串末尾。
            var end = pos;
            var width = 0;
            while (end < text.Length && width + CharWidth(text[end]) <= maxWidth)
            {
                width += CharWidth(text[end]);
                end++;
            }

            // 尝试在最近的空格处断行（避免单词被截断）。
            if (end < text.Length && end > pos)
            {
                var lastSpace = text.LastIndexOf(' ', end - 1, end - pos);
                if (lastSpace > pos)
                    end = lastSpace + 1;
            }

            lines.Add(text[pos..end].TrimEnd());
            pos = end;

            // 跳过前导空格
            while (pos < text.Length && text[pos] == ' ')
                pos++;
        }

        return lines.Count > 0 ? lines : [""];
    }

    /// <summary>从 start 开始找下一个可选条目（跳过分隔行）。</summary>
    private int NextSelectable(int start, bool forward)
    {
        if (_filtered.Count == 0) return 0;
        var idx = start;
        for (var i = 0; i < _filtered.Count; i++)
        {
            if (idx >= 0 && idx < _filtered.Count && !IsNonSelectable(_filtered[idx]))
                return idx;
            idx = forward ? idx + 1 : idx - 1;
            if (idx >= _filtered.Count) idx = 0;
            if (idx < 0) idx = _filtered.Count - 1;
        }
        return 0;
    }

    // Generic (file path / tool name) completion

    public void TryTypeaheadCompletion(string currentLine)
    {
        if (_typeaheadEngine is null) { Hide(); return; }

        _lastCompletionInput = currentLine;
        var completions = _typeaheadEngine.GetCompletions(currentLine);
        if (completions.Count > 0)
            ShowGenericCompletion(completions);
        else
            Hide();
    }

    private void ShowGenericCompletion(List<string> completions)
    {
        _genericCompletions = completions;
        _selectedIndex = 0;

        // Use the typeahead engine's display items (file name only, not full prefix)
        // so the popup shows clean entries like "readme.txt" instead of "hello @readme.txt".
        if (_typeaheadEngine is not null)
        {
            var items = _typeaheadEngine.GetCompletionItems(
                _lastCompletionInput ?? string.Empty);
            _genericDisplayItems = items.Select(i => i.Display).ToList();
            _genericCompletions = items.Select(i => i.Insert).ToList();
        }
        else
        {
            _genericDisplayItems = completions
                .Select(c => c.Length > 60 ? c[..60] : c)
                .ToList();
        }

        var height = Math.Min(_genericCompletions.Count + 2, 12);
        ShowPopup(_genericDisplayItems, height);
    }

    /// <summary>Stores the last input that triggered completion, for display extraction.</summary>
    private string? _lastCompletionInput;

    // Navigation

    public void CycleNext()
    {
        if (_genericCompletions is { Count: > 0 })
        {
            var count = _genericCompletions.Count;
            _selectedIndex = (_selectedIndex + 1) % Math.Max(1, count);
            return;
        }
        // Slash-command mode: skip separator rows.
        var n = _filtered.Count;
        if (n == 0) return;
        do
        {
            _selectedIndex = (_selectedIndex + 1) % n;
        } while (IsNonSelectable(_filtered[_selectedIndex]));
    }

    public void CyclePrevious()
    {
        if (_genericCompletions is { Count: > 0 })
        {
            var count = _genericCompletions.Count;
            _selectedIndex = (_selectedIndex - 1 + count) % Math.Max(1, count);
            return;
        }
        // Slash-command mode: skip separator rows.
        var n = _filtered.Count;
        if (n == 0) return;
        do
        {
            _selectedIndex = (_selectedIndex - 1 + n) % n;
        } while (IsNonSelectable(_filtered[_selectedIndex]));
    }

    public void SetSelectedIndex(int idx) => _selectedIndex = idx;

    // Accept / Hide

    /// <summary>
    /// Returns the accepted text, or null if nothing was selected.
    /// For generic completions, returns the completed string.
    /// For slash commands, returns "/commandName ".
    /// </summary>
    public string? Accept()
    {
        if (_genericCompletions is { Count: > 0 })
        {
            if (_selectedIndex >= 0 && _selectedIndex < _genericCompletions.Count)
            {
                var result = _genericCompletions[_selectedIndex];
                Hide();
                return result;
            }
            Hide();
            return null;
        }

        if (_selectedIndex < 0 || _selectedIndex >= _filtered.Count) { Hide(); return null; }

        var selected = _filtered[_selectedIndex];
        // Separator rows should never be accepted; treat as no-op.
        if (IsNonSelectable(selected)) { return null; }
        Hide();
        return $"/{selected.Name} ";
    }

    public void Hide()
    {
        if (!_isCompletionVisible) return;
        _isCompletionVisible = false;
        _genericCompletions = null;
        _genericDisplayItems = null;
        _lastCompletionInput = null;
        CompletionStateChanged?.Invoke(false, 0);
    }

    // Internal popup management

    public IReadOnlyList<string>? CurrentDisplayItems { get; private set; }

    private void ShowPopup(List<string> items, int height)
    {
        CurrentDisplayItems = items;
        if (!_isCompletionVisible)
        {
            _isCompletionVisible = true;
        }
        CompletionStateChanged?.Invoke(true, height);
    }
}
