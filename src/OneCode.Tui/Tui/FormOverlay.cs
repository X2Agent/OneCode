namespace OneCode.Tui;

/// <summary>A reusable label/field row for overlay forms.</summary>
public sealed class FormRow : View
{
    private const int FieldOffset = TuiSpacing.FormFieldX - TuiSpacing.OverlayContentX;

    public Label Label { get; }

    public View Field { get; }

    public FormRow(string labelText, View field)
    {
        ArgumentNullException.ThrowIfNull(field);

        X = TuiSpacing.OverlayContentX;
        Width = Dim.Fill(TuiSpacing.OverlayContentX);
        Height = 1;
        CanFocus = true;
        TabStop = TabBehavior.TabGroup;

        Label = new Label
        {
            Text = labelText,
            X = 0,
            Y = 0,
            Width = FieldOffset - 1,
            Height = 1,
            CanFocus = false,
        };
        Label.SetScheme(TuiStyles.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        Field = field;
        Field.X = FieldOffset;
        Field.Y = 0;
        Field.SetScheme(TuiStyles.MakeFieldScheme(TuiPalette.FgPrimary, TuiPalette.BgTerminalHeader));

        Add(Label, Field);
    }
}

/// <summary>Describes a validation failure and the field that should receive focus.</summary>
public sealed record FormValidationFailure(string Message, View? Target = null);

/// <summary>Common validators used by TUI forms.</summary>
public static class FormValidators
{
    public static FormValidationFailure? Required(string? value, string fieldName, View? target = null)
        => string.IsNullOrWhiteSpace(value)
            ? new FormValidationFailure($"{fieldName}不能为空。", target)
            : null;

    public static FormValidationFailure? IntegerRange(
        string? value,
        string fieldName,
        int minimum,
        int maximum,
        View? target,
        out int parsed)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            return new FormValidationFailure($"{fieldName}必须是整数。", target);

        return parsed < minimum || parsed > maximum
            ? new FormValidationFailure($"{fieldName}范围：{minimum}–{maximum}。", target)
            : null;
    }

    public static FormValidationFailure? HttpUrl(string? value, string fieldName, View? target = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new FormValidationFailure($"{fieldName}格式无效（需包含 http:// 或 https://）。", target);
        }

        return null;
    }
}

/// <summary>
/// Base class for form-style overlays. It centralizes row placement, validation feedback,
/// initial focus, and the primary/secondary action bar.
/// </summary>
public abstract class FormOverlay<TResult> : ResultOverlay<TResult>
{
    private int _nextRowY;
    private readonly View _formContent;
    private readonly Label _errorLabel;
    private View? _actions;
    // 按添加顺序登记的流式行（视图 + 占位高度），支撑 SetRowVisible 的整体平移。
    private readonly List<(View View, int Height)> _flowEntries = [];

    protected FormOverlay(string title, int preferredWidth, int preferredHeight)
        : base(title, preferredWidth, preferredHeight)
    {
        _formContent = new View
        {
            X = 0,
            Y = TuiSpacing.OverlayContentY,
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
            CanFocus = true,
            TabStop = TabBehavior.TabGroup,
        };
        // 滚动容器默认会用自身背景铺满整块视口，把浮层外框的左右竖线整段抹掉
        // （设置页曾出现整片无框的区域）。设为透明，只画行内容不动外框。
        _formContent.ViewportSettings |= ViewportSettingsFlags.Transparent;
        _formContent.VerticalScrollBar.Visible = false;
        Add(_formContent);

        // 视口尺寸变化（浮层首次定位、终端拉伸）后内容宽度必须同步收缩，
        // 否则 Dim.Fill 会按旧的内容宽度求解，行内容画到外框之外。
        _formContent.ViewportChanged += (_, _) => UpdateContentSize();

        _errorLabel = new Label
        {
            Text = string.Empty,
            X = TuiSpacing.OverlayContentX,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(TuiSpacing.OverlayContentX),
            Height = 1,
            CanFocus = false,
        };
        _errorLabel.SetScheme(TuiStyles.MakeScheme(TuiPalette.Error, TuiPalette.BgCard));
    }

    protected int NextRowY => _nextRowY;

    internal string ValidationMessage => _errorLabel.Text;

    /// <summary>表单可滚动字段视口容器（internal 供 headless 测试视口滚动断言）。</summary>
    internal View FormContent => _formContent;

    /// <summary>底部操作栏容器（internal 供 headless 测试定位保存/取消焦点断言）。</summary>
    internal View? Actions => _actions;

    protected FormRow AddRow(string labelText, View field, int rowSpacing = TuiSpacing.Sm)
    {
        var row = new FormRow(labelText, field) { Y = _nextRowY };
        _formContent.Add(row);
        _flowEntries.Add((row, rowSpacing));
        _nextRowY += rowSpacing;

        row.HasFocusChanged += (_, _) =>
        {
            if (row.HasFocus)
                EnsureVisible(row);
        };
        field.HasFocusChanged += (_, _) =>
        {
            if (field.HasFocus)
                EnsureVisible(field);
        };

        UpdateContentSize();
        return row;
    }

    protected void AddCustomRow(int height, params View[] views)
    {
        foreach (var view in views)
        {
            view.Y = _nextRowY + view.Y;
            _formContent.Add(view);
            _flowEntries.Add((view, height));

            view.HasFocusChanged += (_, _) =>
            {
                if (view.HasFocus)
                    EnsureVisible(view);
            };
        }

        _nextRowY += height;
        UpdateContentSize();
    }

    /// <summary>
    /// 切换某表单行的可见性：隐藏时其后的所有行整体上移以收起空洞，显示时恢复。
    /// 仅支持经 <see cref="AddRow"/> 添加的行；重复设置同一可见性为无操作。
    /// </summary>
    protected void SetRowVisible(FormRow row, bool visible)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Visible == visible)
            return;

        row.Visible = visible;
        row.TabStop = visible ? TabBehavior.TabGroup : TabBehavior.NoStop;

        var entryIndex = _flowEntries.FindIndex(entry => entry.View == row);
        if (entryIndex < 0)
            return;

        var delta = visible ? _flowEntries[entryIndex].Height : -_flowEntries[entryIndex].Height;
        for (var i = entryIndex + 1; i < _flowEntries.Count; i++)
        {
            _flowEntries[i].View.Y += delta;
        }

        _nextRowY += delta;
        PreferredHeight += delta;
        UpdateContentSize();
        SetNeedsLayout();
        SetNeedsDraw();
    }

    protected (Button Primary, Button Secondary) AddActionBar(
        string primaryText,
        Action primaryAction,
        string secondaryText,
        Action secondaryAction)
    {
        _errorLabel.Y = Pos.AnchorEnd(3);
        Add(_errorLabel);

        var primary = TuiButtons.Create(primaryText);
        primary.Accepting += (_, _) => primaryAction();

        var secondary = TuiButtons.Create(secondaryText);
        secondary.Accepting += (_, _) => secondaryAction();

        _actions = TuiButtons.CreateRightAlignedRow(primary, secondary);
        Add(_actions);
        PreferredHeight = _nextRowY + TuiSpacing.OverlayContentY + 4;
        UpdateContentSize();
        return (primary, secondary);
    }

    protected bool ShowValidationFailure(FormValidationFailure? failure)
    {
        if (failure is null)
        {
            _errorLabel.Text = string.Empty;
            SetNeedsDraw();
            return false;
        }

        _errorLabel.Text = failure.Message;
        if (failure.Target is { } target)
        {
            EnsureVisible(target);
            target.SetFocus();
        }
        SetNeedsDraw();
        return true;
    }

    internal override void FocusInitialView()
    {
        if (InitialFocusView is { } initial)
        {
            EnsureVisible(initial);
        }

        base.FocusInitialView();
    }

    protected override bool OnKeyDown(Key kb)
    {
        // 注意：Ctrl+D 是保留退出键（app:exit），此处不得绑定翻页——Overlay 内同样放行，
        // 由 ChatInputView/ReplShell 的退出路径统一处理。
        if (kb == Key.PageDown)
        {
            if (_formContent.ScrollVertical(Math.Max(1, _formContent.Viewport.Height / 2)) == true)
            {
                SetNeedsDraw();
                return true;
            }
        }
        else if (kb == Key.PageUp)
        {
            if (_formContent.ScrollVertical(-Math.Max(1, _formContent.Viewport.Height / 2)) == true)
            {
                SetNeedsDraw();
                return true;
            }
        }

        return base.OnKeyDown(kb);
    }

    private void UpdateContentSize()
    {
        var contentHeight = Math.Max(1, _nextRowY);
        // 宽度必须取当前视口宽度：内容尺寸一旦大于视口，子行的 Dim.Fill 会按
        // 内容宽度求解，于是整行越过浮层边框画到框外（右侧竖线被行内容覆盖）。
        // 首次绘制前视口还是 0，退回偏好宽度。
        var contentWidth = _formContent.Viewport.Width > 0
            ? _formContent.Viewport.Width
            : PreferredWidth;
        _formContent.SetContentSize(new System.Drawing.Size(Math.Max(1, contentWidth), contentHeight));
    }

    private void EnsureVisible(View? target)
    {
        if (target is null || _formContent.Viewport.Height <= 0)
            return;

        var current = target;
        var relY = 0;
        while (current is not null && current != _formContent)
        {
            relY += current.Frame.Y;
            current = current.SuperView;
        }

        if (current is null)
            return;

        var targetHeight = Math.Max(1, target.Frame.Height);
        var top = relY;
        var bottom = relY + targetHeight;

        if (top < _formContent.Viewport.Y)
        {
            _formContent.ScrollVertical(top - _formContent.Viewport.Y);
            SetNeedsDraw();
        }
        else if (bottom > _formContent.Viewport.Y + _formContent.Viewport.Height)
        {
            _formContent.ScrollVertical(bottom - (_formContent.Viewport.Y + _formContent.Viewport.Height));
            SetNeedsDraw();
        }
    }
}
