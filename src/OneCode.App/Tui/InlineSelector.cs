namespace OneCode.App.Tui;

using OneCode.Core.Keybindings;

/// <summary>
/// An inline selector that renders options directly within the conversation view.
/// Users navigate with ↑↓ arrows and confirm with Enter, dismiss with Esc.
/// Replaces modal overlays for tool permission prompts and other confirmations.
/// 按键经 <see cref="KeybindingResolver"/> 的 <see cref="KeybindingDefaults.ContextSelector"/>
/// 上下文分发（解析时并入，无 push/pop 生命周期）；独立构造（工具层/测试）时
/// 回退到默认绑定，行为一致。用户可通过 keybindings.json 重映射 selector:*。
/// </summary>
public sealed class InlineSelector
{
    private readonly string _title;
    private readonly IReadOnlyList<InlineSelectorOption> _options;
    private readonly string? _prompt;
    private readonly bool _useInformationRequestCard;
    private int _selectedIndex;
    private readonly TaskCompletionSource<InlineSelectorResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 独立构造（无宿主注入）时的回退键位系统：默认绑定 + 空活跃上下文。
    private static readonly KeybindingResolver FallbackResolver = CreateFallbackResolver();

    private KeybindingResolver _keyResolver = FallbackResolver;
    private readonly KeybindingContextManager _keyContextManager = new();

    private static KeybindingResolver CreateFallbackResolver()
    {
        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        return resolver;
    }

    public InlineSelector(
        string title,
        IReadOnlyList<InlineSelectorOption> options,
        int defaultIndex = 0,
        string? prompt = null,
        bool useInformationRequestCard = false)
    {
        _title = title;
        _options = options;
        _prompt = prompt;
        _useInformationRequestCard = useInformationRequestCard;
        _selectedIndex = Math.Clamp(defaultIndex, 0, options.Count - 1);
    }

    /// <summary>
    /// 注入全局键位系统（ReplShell.ShowInlineSelector 调用），使用户覆盖的
    /// selector:* 绑定生效；不注入则使用默认绑定回退。
    /// </summary>
    internal void AttachKeybindingSystem(KeybindingResolver keyResolver, KeybindingContextManager keyContextManager)
    {
        _keyResolver = keyResolver;
        _keyContextManager.FocusContext = keyContextManager.FocusContext;
    }

    public string Title => _title;
    public IReadOnlyList<InlineSelectorOption> Options => _options;
    public string? Prompt => _prompt;
    public bool UseInformationRequestCard => _useInformationRequestCard;
    public int SelectedIndex => _selectedIndex;
    public Task<InlineSelectorResult> ResultTask => _tcs.Task;

    /// <summary>Handle a key press. Returns true if key was consumed.</summary>
    public bool HandleKey(Key kb)
    {
        // Selector 上下文在解析时并入：仅当本选择器接管键盘时消费 selector:*。
        var contexts = new HashSet<string>(_keyContextManager.ActiveContexts, StringComparer.Ordinal)
        {
            KeybindingDefaults.ContextSelector,
        };
        switch (TuiKeyAdapter.ResolveAction(kb, _keyResolver, contexts))
        {
            case KeybindingDefaults.ActionSelectorPrevious:
                if (_selectedIndex > 0) _selectedIndex--;
                return true;

            case KeybindingDefaults.ActionSelectorNext:
                if (_selectedIndex < _options.Count - 1) _selectedIndex++;
                return true;

            case KeybindingDefaults.ActionSelectorConfirm:
                _tcs.TrySetResult(new InlineSelectorResult(_selectedIndex, _options[_selectedIndex].Id));
                return true;

            case KeybindingDefaults.ActionSelectorDismiss:
                Dismiss();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Completes the selector as dismissed. Idempotent — a later successful
    /// Enter/Esc is a no-op via <c>TrySetResult</c>.
    /// </summary>
    public void Dismiss() => _tcs.TrySetResult(InlineSelectorResult.Dismissed);

    /// <summary>
    /// Renders the selector state as FormattedLines for embedding in the conversation view
    /// instead of using a separate View. Used by ChatTranscriptView.ShowInlineSelector.
    /// </summary>
    public static IReadOnlyList<FormattedLine> RenderAsLines(
        string title,
        IReadOnlyList<InlineSelectorOption> options,
        int selectedIndex,
        string? prompt = null,
        bool useInformationRequestCard = false,
        int viewWidth = TuiSpacing.DefaultContentWidth)
    {
        var lines = useInformationRequestCard
            ? QuestionCardRenderer.RenderHeader(title, prompt, viewWidth: viewWidth)
            : RenderStandardHeader(title, prompt);

        // Options — bullet + label + description on same line
        for (var i = 0; i < options.Count; i++)
        {
            var isSelected = i == selectedIndex;
            var bullet = isSelected ? TuiGlyphs.RoleBullet : TuiGlyphs.Pending;
            var labelColor = isSelected ? TuiPalette.Accent : TuiPalette.FgPrimary;

            var segs = new List<LineSegment>
            {
                new("  ", TuiPalette.BgPrimary),
                new($"{bullet} ", isSelected ? TuiPalette.Accent : TuiPalette.FgMuted),
                new(options[i].Label, labelColor),
            };

            if (options[i].Description is { Length: > 0 } desc)
                segs.Add(new($"  {desc}", TuiPalette.FgMuted));

            lines.Add(FormattedLine.FromSegments(segs.ToArray()));
        }

        // Hints
        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        lines.Add(FormattedLine.FromSegments(new[]
        {
            new LineSegment($"  {TuiGlyphs.ArrowUp}{TuiGlyphs.ArrowDown} ", TuiPalette.FgSecondary),
            new LineSegment("选择", TuiPalette.FgMuted),
            new LineSegment(" · ", TuiPalette.FgMuted),
            new LineSegment("Enter", TuiPalette.FgSecondary),
            new LineSegment(" 确认", TuiPalette.FgMuted),
            new LineSegment(" · ", TuiPalette.FgMuted),
            new LineSegment("Esc", TuiPalette.FgSecondary),
            new LineSegment(" 取消", TuiPalette.FgMuted),
        }));

        return lines;
    }

    private static List<FormattedLine> RenderStandardHeader(string title, string? prompt)
    {
        var lines = new List<FormattedLine>
        {
            FormattedLine.Plain("", TuiPalette.BgPrimary),
            FormattedLine.FromSegments(new[]
            {
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment(title, TuiPalette.Warning),
            }),
        };
        if (!string.IsNullOrWhiteSpace(prompt))
            lines.Add(FormattedLine.Plain($"  {prompt}", TuiPalette.FgPrimary));
        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        return lines;
    }
}

public sealed record InlineSelectorOption(string Id, string Label, string? Description = null);

public sealed record InlineSelectorResult(int SelectedIndex, string SelectedId)
{
    public static readonly InlineSelectorResult Dismissed = new(-1, "dismissed");
    public bool IsDismissed => SelectedIndex < 0;
}
