namespace OneCode.App.Tui;

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
        Label.SetScheme(TuiTheme.MakeFieldScheme(TuiPalette.FgSecondary, TuiPalette.BgCard));

        Field = field;
        Field.X = FieldOffset;
        Field.Y = 0;
        Field.SetScheme(TuiTheme.MakeFieldScheme(TuiPalette.FgPrimary, TuiPalette.BgTerminalHeader));

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
    private int _nextRowY = TuiSpacing.OverlayContentY;
    private readonly Label _errorLabel;

    protected FormOverlay(string title, int preferredWidth, int preferredHeight)
        : base(title, preferredWidth, preferredHeight)
    {
        _errorLabel = new Label
        {
            Text = string.Empty,
            X = TuiSpacing.OverlayContentX,
            Width = Dim.Fill(TuiSpacing.OverlayContentX),
            Height = 1,
            CanFocus = false,
        };
        _errorLabel.SetScheme(TuiTheme.MakeScheme(TuiPalette.Error, TuiPalette.BgCard));
    }

    protected int NextRowY => _nextRowY;

    internal string ValidationMessage => _errorLabel.Text;

    protected FormRow AddRow(string labelText, View field, int rowSpacing = TuiSpacing.Sm)
    {
        var row = new FormRow(labelText, field) { Y = _nextRowY };
        Add(row);
        _nextRowY += rowSpacing;
        return row;
    }

    protected void AddCustomRow(int height, params View[] views)
    {
        foreach (var view in views)
        {
            view.Y = _nextRowY + view.Y;
            Add(view);
        }

        _nextRowY += height;
    }

    protected (Button Primary, Button Secondary) AddActionBar(
        string primaryText,
        Action primaryAction,
        string secondaryText,
        Action secondaryAction)
    {
        _errorLabel.Y = _nextRowY;
        Add(_errorLabel);
        _nextRowY++;

        var secondary = new Button
        {
            Text = secondaryText,
            X = Pos.AnchorEnd(),
            Y = _nextRowY,
        };
        secondary.SetScheme(TuiTheme.MakeButtonScheme(TuiPalette.FgPrimary, TuiPalette.BgCard, TuiPalette.BgActive));
        secondary.Accepting += (_, _) => secondaryAction();

        var primary = new Button
        {
            Text = primaryText,
            X = 0,
            Y = _nextRowY,
        };
        primary.SetScheme(TuiTheme.MakeButtonScheme(TuiPalette.FgPrimary, TuiPalette.BgCard, TuiPalette.BgActive));
        primary.Accepting += (_, _) => primaryAction();

        var actions = new View
        {
            X = Pos.AnchorEnd(),
            Y = _nextRowY,
            Width = Dim.Auto(),
            Height = 1,
            CanFocus = true,
            TabStop = TabBehavior.TabGroup,
        };
        secondary.X = Pos.Right(primary) + TuiSpacing.Sm;
        primary.Y = 0;
        secondary.Y = 0;
        actions.Add(primary, secondary);
        Add(actions);
        PreferredHeight = _nextRowY + 3;
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
        failure.Target?.SetFocus();
        SetNeedsDraw();
        return true;
    }
}
