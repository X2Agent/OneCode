using System.Collections.ObjectModel;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Tui;

/// <summary>
/// 设置弹层。显示统一解析后的有效值，但只提交用户实际修改的字段，避免把继承值复制到目标作用域。
/// </summary>
public sealed class SettingsOverlay : FormOverlay<SettingsResult?>
{
    private static readonly Scheme CheckScheme = new()
    {
        Normal = new Attribute(TuiPalette.FgPrimary, TuiPalette.BgCard),
        Focus = new Attribute(TuiPalette.Accent, TuiPalette.BgCard),
        HotNormal = new Attribute(TuiPalette.Accent, TuiPalette.BgCard),
        HotFocus = new Attribute(TuiPalette.Accent, TuiPalette.BgCard),
        Disabled = new Attribute(TuiPalette.FgMuted, TuiPalette.BgCard),
    };

    private readonly ConfigSnapshot _initialSnapshot;
    private readonly DropDownList _scopeField;
    private readonly ObservableCollection<string> _scopeItems;
    private readonly DropDownList _providerField;
    private readonly ObservableCollection<string> _providerItems;
    private readonly TextField _baseUrlField;
    private readonly TextField _apiKeyField;
    private readonly string _initialApiKey;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly TextField _modelField;
    private readonly TextField _fastModelField;
    private readonly CheckBox _thinkingCheck;
    private readonly CheckBox _showThinkingCheck;
    private readonly CheckBox _notificationsCheck;
    private readonly TextField _maxTurnsField;
    private readonly DropDownList _effortField;
    private readonly ObservableCollection<string> _effortItems;

    public override OverlayLayoutMode LayoutMode => OverlayLayoutMode.Fill;

    protected override View? InitialFocusView => _scopeField;

    internal View InitialFocusControl => _scopeField;
    internal TextField BaseUrlField => _baseUrlField;
    internal TextField ModelField => _modelField;
    internal TextField MaxTurnsField => _maxTurnsField;
    internal TextField ApiKeyField => _apiKeyField;
    internal Button SaveButton => _saveButton;
    internal Button CancelButton => _cancelButton;

    public SettingsOverlay(ConfigSnapshot snapshot, bool projectScopeAvailable)
        : base("  设置  (Ctrl+S 保存 · Esc 取消)", preferredWidth: 78, preferredHeight: 28)
    {
        _initialSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        var settings = snapshot.Effective;

        _scopeItems = new ObservableCollection<string> { "用户配置" };
        if (projectScopeAvailable)
            _scopeItems.Add("项目配置");
        _scopeField = CreateDropDown(_scopeItems, 0, 20);
        AddRow("编辑范围：", _scopeField);

        _providerItems = new ObservableCollection<string> { "anthropic", "openai", "ollama" };
        _providerField = CreateDropDown(
            _providerItems,
            SelectIndex(_providerItems, settings.Provider ?? "anthropic", 0),
            20);
        AddRow("服务商：", _providerField);

        _baseUrlField = CreateTextField(settings.BaseUrl ?? string.Empty, Dim.Fill(TuiSpacing.Md));
        AddRow("服务商地址：", _baseUrlField);

        _initialApiKey = settings.ApiKey ?? string.Empty;
        _apiKeyField = CreateTextField(_initialApiKey, Dim.Fill(TuiSpacing.Md));
        _apiKeyField.Secret = true;
        AddRow("API Key：", _apiKeyField);

        _modelField = CreateTextField(settings.Model ?? string.Empty, Dim.Fill(TuiSpacing.Md));
        AddRow("模型：", _modelField);

        _fastModelField = CreateTextField(
            settings.Get<string>(OneCode.Core.Constants.ConfigKeys.FastModel) ?? string.Empty,
            Dim.Fill(TuiSpacing.Md));
        AddRow("快速模型：", _fastModelField);

        _thinkingCheck = CreateCheckBox("扩展思考", settings.Get("thinkingEnabled", false));
        _showThinkingCheck = CreateCheckBox("显示思考", settings.Get("showThinking", false));
        _showThinkingCheck.X = Pos.Right(_thinkingCheck) + TuiSpacing.Md;
        _notificationsCheck = CreateCheckBox("任务完成通知", settings.NotificationsEnabled);
        _notificationsCheck.Y = 1;
        AddCustomRow(3, _thinkingCheck, _showThinkingCheck, _notificationsCheck);

        _maxTurnsField = CreateTextField(settings.MaxTurns.ToString(CultureInfo.InvariantCulture), 10);
        AddRow("最大轮数：", _maxTurnsField);

        _effortItems = new ObservableCollection<string> { "low", "medium", "high", "max" };
        _effortField = CreateDropDown(
            _effortItems,
            SelectIndex(_effortItems, settings.Get("effortValue", "medium") ?? "medium", 1),
            15);
        AddRow("努力程度：", _effortField);

        (_saveButton, _cancelButton) = AddActionBar(
            $"{TuiGlyphs.Complete} _保存  Ctrl+S",
            TrySave,
            "_取消  Esc",
            () => RequestClose(OverlayCloseReason.Cancelled));
    }

    protected override SettingsResult? GetDismissedResult(OverlayCloseReason reason) => null;

    internal void TrySave()
    {
        if (ShowValidationFailure(FormValidators.IntegerRange(
                _maxTurnsField.Text,
                "最大轮数",
                minimum: 1,
                maximum: 999,
                _maxTurnsField,
                out var parsedMaxTurns)))
        {
            return;
        }

        if (ShowValidationFailure(FormValidators.HttpUrl(_baseUrlField.Text, "服务商地址", _baseUrlField)))
            return;

        if (ShowValidationFailure(FormValidators.Required(_modelField.Text, "模型", _modelField)))
            return;

        ShowValidationFailure(null);
        Complete(new SettingsResult(
            TargetScope: _scopeField.Value is string scope && scope == "项目配置"
                ? ConfigScope.Project
                : ConfigScope.User,
            Provider: GetSelectedValue(_providerField, _providerItems, 0),
            BaseUrl: _baseUrlField.Text ?? string.Empty,
            ApiKey: _apiKeyField.Text ?? string.Empty,
            ApiKeyChanged: !string.Equals(_apiKeyField.Text, _initialApiKey, StringComparison.Ordinal),
            Model: _modelField.Text ?? string.Empty,
            FastModel: _fastModelField.Text ?? string.Empty,
            ThinkingEnabled: _thinkingCheck.Value == CheckState.Checked,
            ShowThinking: _showThinkingCheck.Value == CheckState.Checked,
            NotificationsEnabled: _notificationsCheck.Value == CheckState.Checked,
            MaxTurns: parsedMaxTurns,
            Effort: GetSelectedValue(_effortField, _effortItems, 1),
            InitialSnapshot: _initialSnapshot));
    }

    private static DropDownList CreateDropDown(ObservableCollection<string> items, int selectedIndex, int width)
    {
        var field = new DropDownList
        {
            Width = width,
            Height = 1,
            Source = new ListWrapper<string>(items),
        };
        field.Value = items[Math.Clamp(selectedIndex, 0, items.Count - 1)];
        return field;
    }

    private static TextField CreateTextField(string value, Dim width) => new()
    {
        Text = value,
        Width = width,
    };

    private static CheckBox CreateCheckBox(string text, bool value)
    {
        var checkBox = new CheckBox
        {
            Text = text,
            Value = value ? CheckState.Checked : CheckState.UnChecked,
            X = TuiSpacing.OverlayContentX,
        };
        checkBox.SetScheme(CheckScheme);
        return checkBox;
    }

    private static int SelectIndex(ObservableCollection<string> items, string value, int fallbackIndex)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index], value, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return fallbackIndex;
    }

    private static string GetSelectedValue(
        DropDownList field,
        ObservableCollection<string> items,
        int fallbackIndex) =>
        field.Value is string value && !string.IsNullOrEmpty(value)
            ? value
            : items[fallbackIndex];

    protected override bool OnKeyDown(Key kb)
    {
        if (kb == Key.S.WithCtrl)
        {
            TrySave();
            return true;
        }

        return base.OnKeyDown(kb);
    }

}

public sealed record SettingsResult(
    ConfigScope TargetScope,
    string Provider,
    string BaseUrl,
    string ApiKey,
    bool ApiKeyChanged,
    string Model,
    string FastModel,
    bool ThinkingEnabled,
    bool ShowThinking,
    bool NotificationsEnabled,
    int MaxTurns,
    string Effort,
    ConfigSnapshot InitialSnapshot);
