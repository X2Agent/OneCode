using OneCode.Core.Coordinator;
using OneCode.Core.Goals;

namespace OneCode.App.Tui;

/// <summary>
/// 底部待办与 Goal 子目标面板（内容区与 Agent 状态栏之间）：
/// 同时支持渲染 agent 的 <c>todos_*</c> 清单快照与 GOAL 模式下的拆分子目标树（含验收标准、
/// 重试轮次、评估结论）。支持折叠（默认紧凑 4~5 行）与展开（最高 12 行，Ctrl+O 或点击标题切换）。
/// </summary>
internal sealed class TodoStripView : View
{
    /// <summary>折叠态最多展示的条目数（不含头部行与汇总行），防止清单挤压对话区。</summary>
    public const int MaxItems = 4;

    /// <summary>展开态最多展示的内容行数（不含头部行），兼顾详情可读性与主对话区视野。</summary>
    public const int MaxExpandedRows = 12;

    /// <summary>未自定义键位时的默认切换快捷键文案。</summary>
    public const string DefaultToggleShortcutDisplay = "Ctrl+O";

    private readonly Func<string>? _toggleShortcutDisplayProvider;
    private IReadOnlyList<TodoListItem> _items = [];
    private IReadOnlyList<GoalStepSnapshot> _goalSteps = [];
    private IReadOnlyList<GoalStepExecutionEvidence> _goalExecutions = [];
    private int _currentGoalIndex;
    private GoalBudgetUsage? _goalUsage;
    private GoalBudget? _goalBudget;
    private bool _isExpanded;

    /// <summary>当展开/折叠态发生切换时触发，通知宿主（<see cref="ReplShell"/>）重算高度布局。</summary>
    public event Action? ExpansionChanged;

    public TodoStripView(Func<string>? toggleShortcutDisplayProvider = null)
    {
        _toggleShortcutDisplayProvider = toggleShortcutDisplayProvider;
        CanFocus = false;
        TabStop = TabBehavior.NoStop;
    }

    /// <summary>当前是否处于展开详情态。</summary>
    public bool IsExpanded => _isExpanded;

    /// <summary>当前是否有可展示的待办或子目标内容。</summary>
    public bool HasContent => _goalSteps.Count > 0 || _items.Count > 0;

    /// <summary>切换展开/折叠态并通知宿主刷新高度布局。</summary>
    public void ToggleExpanded()
    {
        if (!HasContent)
            return;

        _isExpanded = !_isExpanded;
        SetNeedsDraw();
        ExpansionChanged?.Invoke();
    }

    /// <summary>显式设置展开/折叠态。</summary>
    public void SetExpanded(bool expanded)
    {
        if (_isExpanded == expanded)
            return;

        _isExpanded = expanded;
        SetNeedsDraw();
        ExpansionChanged?.Invoke();
    }

    /// <summary>用新待办快照替换内容；空快照由调用方负责隐藏（见 <c>ReplShell</c>）。</summary>
    public void Update(IReadOnlyList<TodoListItem> items)
    {
        _items = items;
        SetNeedsDraw();
    }

    /// <summary>更新 GOAL 模式子目标计划与执行回执快照。</summary>
    public void UpdateGoalPlan(
        IReadOnlyList<GoalStepSnapshot> steps,
        IReadOnlyList<GoalStepExecutionEvidence> executions,
        int currentIndex,
        GoalBudgetUsage? usage = null,
        GoalBudget? budget = null)
    {
        _goalSteps = steps;
        _goalExecutions = executions;
        _currentGoalIndex = currentIndex;
        _goalUsage = usage;
        _goalBudget = budget;
        SetNeedsDraw();
    }

    /// <summary>清空 GOAL 子目标状态（切换会话或非 Goal 模式重置时调用）。</summary>
    public void ClearGoalPlan()
    {
        _goalSteps = [];
        _goalExecutions = [];
        _currentGoalIndex = 0;
        _goalUsage = null;
        _goalBudget = null;
        SetNeedsDraw();
    }

    /// <summary>当前快照占用的行数（头部 1 + 条目 + 可选汇总）；无内容时为 0。</summary>
    public int RequiredRows
    {
        get
        {
            if (_goalSteps.Count == 0 && _items.Count == 0)
                return 0;

            var lines = BuildContentLines();
            var cap = _isExpanded ? MaxExpandedRows : MaxItems;
            var itemRows = Math.Min(lines.Count, cap);
            var overflowRow = lines.Count > cap ? 1 : 0;
            return 1 + itemRows + overflowRow;
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0 || !HasContent)
            return true;

        var lines = BuildContentLines();
        var cap = _isExpanded ? MaxExpandedRows : MaxItems;

        // 头部：反色条，与 AgentStatusBar 的曲面底色一致，视觉上属于同一层状态区。
        Move(0, 0);
        SetAttribute(new Attribute(TuiPalette.Accent, TuiPalette.BgInput));
        AddStr(PadOrTruncate(BuildHeaderText(), width));

        var row = 1;
        for (var i = 0; i < lines.Count && row <= cap; i++, row++)
        {
            var line = lines[i];
            Move(0, row);
            SetAttribute(new Attribute(line.Color, TuiPalette.BgInput));
            AddStr(PadOrTruncate(line.Text, width));
        }

        if (lines.Count > cap)
        {
            Move(0, row);
            SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgInput));
            var shortcut = GetToggleShortcutDisplay();
            var hint = _isExpanded
                ? $"  {TuiGlyphs.Ellipsis} 其余 {lines.Count - cap} 行"
                : $"  {TuiGlyphs.Ellipsis} 其余 {lines.Count - cap} 项（按 {shortcut} 展开详情）";
            AddStr(PadOrTruncate(hint, width));
        }

        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked)
            && mouse.Position is { Y: 0 }
            && HasContent)
        {
            ToggleExpanded();
            return true;
        }

        return base.OnMouseEvent(mouse);
    }

    internal string GetHeaderTextForTest() => BuildHeaderText();

    internal IReadOnlyList<string> GetContentLineTextsForTest() =>
        BuildContentLines().Select(l => l.Text).ToList();

    private string GetToggleShortcutDisplay()
    {
        var custom = _toggleShortcutDisplayProvider?.Invoke();
        return string.IsNullOrWhiteSpace(custom) ? DefaultToggleShortcutDisplay : custom;
    }

    private string BuildHeaderText()
    {
        var shortcut = GetToggleShortcutDisplay();
        var toggleHint = _isExpanded ? $"[{shortcut} 折叠]" : $"[{shortcut} 展开详情]";
        if (_goalSteps.Count > 0)
        {
            var completed = _goalSteps.Count(s => s.State == GoalStepState.Completed);
            var activeStep = _currentGoalIndex >= 0 && _currentGoalIndex < _goalSteps.Count
                ? _goalSteps[_currentGoalIndex]
                : null;
            var activeText = activeStep is not null && activeStep.State == GoalStepState.InProgress
                ? $" · 正在执行 #{activeStep.Id}"
                : "";
            var budgetText = FormatBudgetBadge();
            return $" {TuiGlyphs.BrandMark} 目标进度 ({completed}/{_goalSteps.Count}){activeText}{budgetText}  {toggleHint}";
        }

        var remaining = _items.Count(item => !item.IsComplete);
        return $" {TuiGlyphs.BrandMark} 待办 ({_items.Count - remaining}/{_items.Count})  {toggleHint}";
    }

    private string FormatBudgetBadge()
    {
        if (_goalUsage is not { } usage || _goalBudget is not { } budget)
            return "";

        List<string> parts = [];
        if (budget.MaxSubGoalAttempts > 0)
            parts.Add($"轮次 {usage.TotalAttempts}/{budget.MaxSubGoalAttempts}");
        if (budget.MaxTotalTokens is { } maxTok && maxTok > 0)
            parts.Add($"Token {usage.TotalTokens / 1000.0:0.#}k/{maxTok / 1000.0:0.#}k");
        return parts.Count == 0 ? "" : $" · {string.Join(" · ", parts)}";
    }

    private List<StripLine> BuildContentLines()
    {
        List<StripLine> lines = [];

        if (_goalSteps.Count > 0)
        {
            var execById = _goalExecutions
                .GroupBy(e => e.GoalId)
                .ToDictionary(g => g.Key, g => g.Last());

            for (var i = 0; i < _goalSteps.Count; i++)
            {
                var step = _goalSteps[i];
                execById.TryGetValue(step.Id, out var exec);
                var (icon, color) = step.SupersededByReplan
                    ? ("✗", TuiPalette.FgMuted)
                    : step.State switch
                    {
                        GoalStepState.Completed => (TuiGlyphs.Complete, TuiPalette.FgMuted),
                        GoalStepState.InProgress => ("▶", TuiPalette.Accent),
                        GoalStepState.Failed => ("✗", TuiPalette.Error),
                        GoalStepState.Skipped => ("○", TuiPalette.FgSecondary),
                        _ => (TuiGlyphs.Pending, TuiPalette.FgPrimary),
                    };

                var attemptSuffix = exec is { Attempts: > 0 }
                    ? $" ({exec.Attempts}/{OneCode.App.Services.Agent.GoalLoopDefaults.MaxAttemptsPerSubGoal}轮)"
                    : step.State == GoalStepState.InProgress
                        ? $" (执行中)"
                        : "";
                var replanSuffix = step.SupersededByReplan ? " [已重规划]" : "";

                lines.Add(new StripLine($"  {icon} #{step.Id} {step.Description}{attemptSuffix}{replanSuffix}", color));

                if (_isExpanded)
                {
                    if (!string.IsNullOrWhiteSpace(step.SuccessCriteria))
                        lines.Add(new StripLine($"      验收标准: {step.SuccessCriteria}", TuiPalette.FgSecondary));
                    if (exec is { ChangedFiles.Count: > 0 })
                        lines.Add(new StripLine($"      变更文件: {string.Join(", ", exec.ChangedFiles.Select(static p => Path.IsPathRooted(p) && !string.IsNullOrEmpty(Path.GetFileName(p)) ? Path.GetFileName(p) : p))}", TuiPalette.FgSecondary));
                    if (exec is not null && !string.IsNullOrWhiteSpace(exec.Evaluation))
                        lines.Add(new StripLine($"      评估结论: {exec.Evaluation}", step.State == GoalStepState.Failed ? TuiPalette.Error : TuiPalette.FgSecondary));
                }
            }

            // 在展开态或当前有活跃内部待办时，附带展示当前子目标下的 todos_* 清单
            if (_items.Count > 0)
            {
                var activeTodos = _isExpanded ? _items : _items.Where(t => !t.IsComplete).Take(2).ToList();
                foreach (var todo in activeTodos)
                {
                    var glyph = todo.IsComplete ? TuiGlyphs.Complete : TuiGlyphs.Pending;
                    var todoColor = todo.IsComplete ? TuiPalette.FgMuted : TuiPalette.FgPrimary;
                    lines.Add(new StripLine($"      {glyph} [步骤待办] {todo.Title}", todoColor));
                }
            }

            return lines;
        }

        foreach (var item in _items)
        {
            if (item.IsComplete)
            {
                lines.Add(new StripLine($"  {TuiGlyphs.Complete} #{item.Id} {item.Title}", TuiPalette.FgMuted));
            }
            else
            {
                lines.Add(new StripLine($"  {TuiGlyphs.Pending} #{item.Id} {item.Title}", TuiPalette.FgPrimary));
            }

            if (_isExpanded && !string.IsNullOrWhiteSpace(item.Description))
            {
                lines.Add(new StripLine($"      {item.Description}", TuiPalette.FgSecondary));
            }
        }

        return lines;
    }

    /// <summary>按显示宽度补齐或截断（窄终端下标题优先保证行结构完整）。</summary>
    private static string PadOrTruncate(string text, int width)
    {
        if (text.Length >= width)
            return text[..Math.Max(0, width)];

        return text + new string(' ', width - text.Length);
    }

    private readonly record struct StripLine(string Text, Color Color);
}
