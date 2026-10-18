using OneCode.Core.Text;

namespace OneCode.App.Tui;

/// <summary>
/// 补全列表中的命令条目 —— 内置命令 / 技能 / MCP 共用此结构。
/// 携带 <see cref="Source"/> 以支持分组显示（Claude Code 风格）：
/// 输入 / 时补全列表按来源分组，用分隔行区分。
/// </summary>
public sealed record SlashCommandEntry(
    string Name,
    string Description,
    CommandSource Source = CommandSource.Builtin,
    string? ArgumentHint = null);

/// <summary>
/// 管理 <see cref="ChatInputView"/> 的斜杠命令、工作区文件与会话 ID 补全状态。
/// 文本度量辅助见 <see cref="CompletionTextMetrics"/>。
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
    /// 运行时替换斜杠命令列表（如技能、MCP 动态命令加载或刷新后调用）。
    /// </summary>
    public void UpdateCommands(IReadOnlyList<SlashCommandEntry> commands)
    {
        _allCommands = commands;
        _typeaheadEngine?.UpdateCommands(commands);
    }

    internal const string CommandsPopupTitle = " commands (Tab 切换 · Enter 选择 · Esc 关闭) ";
    internal const string FilesPopupTitle = " files (Tab 切换 · Enter 选择 · Esc 关闭) ";
    internal const string SessionsPopupTitle = " sessions (Tab 切换 · Enter 选择 · Esc 关闭) ";
    internal const string EmptyWorkspaceFilesHint = "  （工作区暂无可关联文件）";
    internal const string NoMatchingFilesHint = "  （无匹配文件）";

    public bool IsCompletionActive => _isCompletionVisible;
    public int SelectedIndex => _selectedIndex;
    public IReadOnlyList<SlashCommandEntry> FilteredCommands => _filtered;
    public List<string>? GenericCompletions => _genericCompletions;
    public string PopupTitle { get; private set; } = CommandsPopupTitle;

    /// <summary>当补全弹窗可见性或高度变化时触发（首个参数为是否可见，第二个参数为高度）。</summary>
    public event Action<bool, int>? CompletionStateChanged;

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

    /// <summary>
    /// 模糊匹配阈值——与 <c>CommandRegistry.Suggest</c> 的 "Did you mean" 判定一致。
    /// </summary>
    private const double FuzzyMatchThreshold = 0.7;

    /// <summary>
    /// 触发模糊匹配的最小查询长度。短查询（1~2 字符）的 JaroWinkler 分数普遍偏高，
    /// 模糊匹配会把大量无关命令混入菜单，重新引入噪音；拼写纠错只在较长输入时有意义。
    /// </summary>
    private const int MinFuzzyQueryLength = 3;

    public void UpdateCompletionList(string prefix)
    {
        var query = prefix.TrimStart('/').ToLowerInvariant();

        List<SlashCommandEntry> matched;
        if (query.Length == 0)
        {
            matched = _allCommands.ToList();
        }
        else
        {
            // 前缀命中优先（保持注册顺序），再用 JaroWinkler 容忍拼写错误
            // （如 "keyband" → "keybindings"）。描述/参数提示不参与匹配。
            var prefixMatches = _allCommands
                .Where(c => c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var fuzzyMatches = query.Length >= MinFuzzyQueryLength
                ? _allCommands
                    .Where(c => !c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    .Select(c => (Command: c, Score: StringDistance.JaroWinkler(c.Name, query)))
                    .Where(x => x.Score > FuzzyMatchThreshold)
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.Command)
                    .ToList()
                : [];

            matched = [.. prefixMatches, .. fuzzyMatches];
        }

        if (matched.Count == 0)
        {
            Hide();
            return;
        }

        // 按 Source 分组，每组前插入分隔行（Claude Code 风格）。
        List<SlashCommandEntry> grouped = [];
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
            var w = CompletionTextMetrics.DisplayWidth(c.Name);
            if (w > maxNameWidth) maxNameWidth = w;
        }
        var nameWidth = Math.Clamp(maxNameWidth, 7, 24);

        // 计算描述可用宽度：
        //   弹窗内容区 ≈ 终端宽度 - 4（左边距1 + 左右边框2 + Dim.Fill(-1)）
        //   描述起始列 = "/" + nameWidth + " " = nameWidth + 2
        //   描述可用宽度 = 内容区宽度 - 描述起始列
        // 当终端过窄（可用宽度 < 20）时不换行，交由 ListView 自行截断。
        var consoleWidth = CompletionTextMetrics.TryGetConsoleWidth();
        var descAvailable = consoleWidth > 0
            ? consoleWidth - 4 - nameWidth - 2
            : 0;
        if (descAvailable < 20) descAvailable = 0;

        // 构建 _filtered（含续行）和 displayItems。
        // 长描述按 descAvailable 做自动换行，续行缩进对齐到描述列，且不可被选中。
        _filtered = [];
        List<string> displayItems = [];
        var indent = new string(' ', nameWidth + 2);

        foreach (var entry in grouped)
        {
            _filtered.Add(entry);

            if (IsSeparator(entry))
            {
                displayItems.Add($"  ── {entry.Description} ──");
                continue;
            }

            var cmdPart = $"/{CompletionTextMetrics.PadRightDisplay(entry.Name, nameWidth)}";

            // 参数提示（ArgumentHint）以「·」分隔追加在描述后，随描述一起参与换行布局。
            var hintSuffix = string.IsNullOrEmpty(entry.ArgumentHint) ? string.Empty : $" · {entry.ArgumentHint}";
            var fullDesc = entry.Description + hintSuffix;

            if (descAvailable <= 0 || CompletionTextMetrics.DisplayWidth(fullDesc) <= descAvailable)
            {
                displayItems.Add($"{cmdPart} {fullDesc}");
                continue;
            }

            var descLines = CompletionTextMetrics.WordWrap(fullDesc, descAvailable);
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
        PopupTitle = CommandsPopupTitle;

        var height = Math.Min(_filtered.Count + 2, 18);
        ShowPopup(displayItems, height);
    }

    private static bool IsSeparator(SlashCommandEntry c)
        => c.Name.StartsWith(SeparatorPrefix, StringComparison.Ordinal);

    /// <summary>分隔行或续行——均不可被选中（导航时跳过）。</summary>
    private static bool IsNonSelectable(SlashCommandEntry c)
        => IsSeparator(c)
        || c.Name.StartsWith(ContinuationPrefix, StringComparison.Ordinal);

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

    // 通用补全（@ 工作区文件 / 会话 ID / 普通文件路径）

    public void TryTypeaheadCompletion(string currentLine)
    {
        if (_typeaheadEngine is null) { Hide(); return; }

        _lastCompletionInput = currentLine;
        var items = _typeaheadEngine.GetCompletionItems(currentLine);
        if (items.Count > 0)
        {
            ShowGenericCompletion(items);
            return;
        }

        if (TypeaheadCompletionEngine.TryExtractAtFileQuery(currentLine, out var query))
        {
            ShowAtEmptyHintPopup(query);
            return;
        }

        Hide();
    }

    private void ShowGenericCompletion(List<(string Display, string Insert)> items)
    {
        _filtered = [];
        _genericDisplayItems = items.Select(i => i.Display).ToList();
        _genericCompletions = items.Select(i => i.Insert).ToList();
        _selectedIndex = 0;
        var input = _lastCompletionInput ?? string.Empty;
        PopupTitle = TypeaheadCompletionEngine.IsSessionIdCompletionInput(input)
            ? SessionsPopupTitle
            : TypeaheadCompletionEngine.IsSlashCommandCompletionInput(input)
                ? CommandsPopupTitle
                : FilesPopupTitle;

        var height = Math.Min(_genericCompletions.Count + 2, 12);
        ShowPopup(_genericDisplayItems, height);
    }

    private void ShowAtEmptyHintPopup(string query)
    {
        var hint = string.IsNullOrEmpty(query) ? EmptyWorkspaceFilesHint : NoMatchingFilesHint;
        _filtered = [];
        _genericDisplayItems = [hint];
        _genericCompletions = [];
        _selectedIndex = 0;
        PopupTitle = FilesPopupTitle;
        ShowPopup(_genericDisplayItems, height: 3);
    }

    private string? _lastCompletionInput;

    public void CycleNext()
    {
        if (_genericCompletions is { Count: > 0 })
        {
            var count = _genericCompletions.Count;
            _selectedIndex = (_selectedIndex + 1) % Math.Max(1, count);
            return;
        }
        // 斜杠命令模式：跳过分组分隔行与续行。
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
        // 斜杠命令模式：跳过分组分隔行与续行。
        var n = _filtered.Count;
        if (n == 0) return;
        do
        {
            _selectedIndex = (_selectedIndex - 1 + n) % n;
        } while (IsNonSelectable(_filtered[_selectedIndex]));
    }

    /// <summary>
    /// 返回当前选中的补全插入文本并关闭弹窗；无有效选中项时返回 null。
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
