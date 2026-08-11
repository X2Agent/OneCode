using System.Drawing;
using OneCode.App.Services;
using OneCode.App.Tui;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

public sealed class SettingsOverlayTests
{
    [Fact]
    public void Title_UsesUnifiedChineseTerminology()
    {
        var overlay = CreateOverlay();
        overlay.Title.Should().Contain("设置");
        overlay.Title.ToUpperInvariant().Should().NotContain("CONFIG");
    }

    [Fact]
    public void InitialFocus_IsScopeField()
    {
        var overlay = CreateOverlay();
        var host = new OverlayHost(() => { });
        host.Push(overlay);
        overlay.InitialFocusControl.Should().BeSameAs(overlay.MostFocused);
    }

    [Fact]
    public async Task HostEsc_CompletesSettingsTaskWithNull()
    {
        var host = new OverlayHost(() => { });
        var overlay = CreateOverlay();
        var task = overlay.ShowAsync(host.Push, () => host.Pop(), TestContext.Current.CancellationToken);
        host.HandleEsc();
        (await task).Should().BeNull();
        host.Depth.Should().Be(0);
    }

    [Theory]
    [InlineData("not-a-number", "最大轮数必须是整数")]
    [InlineData("1000", "最大轮数范围")]
    public void TrySave_InvalidMaxTurns_ShowsErrorAndFocusesField(string value, string expectedMessage)
    {
        var overlay = CreateOverlay();
        var host = new OverlayHost(() => { });
        host.Push(overlay);
        overlay.MaxTurnsField.Text = value;
        overlay.TrySave();
        overlay.ValidationMessage.Should().Contain(expectedMessage);
        overlay.MaxTurnsField.Should().BeSameAs(overlay.MostFocused);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("not-a-url")]
    public void TrySave_InvalidHttpUrl_ShowsErrorAndFocusesField(string value)
    {
        var overlay = CreateOverlay();
        var host = new OverlayHost(() => { });
        host.Push(overlay);
        overlay.BaseUrlField.Text = value;
        overlay.TrySave();
        overlay.ValidationMessage.Should().Contain("服务商地址格式无效");
        overlay.BaseUrlField.Should().BeSameAs(overlay.MostFocused);
    }

    [Fact]
    public void TrySave_EmptyModel_ShowsErrorAndFocusesField()
    {
        var overlay = CreateOverlay();
        var host = new OverlayHost(() => { });
        host.Push(overlay);
        overlay.ModelField.Text = string.Empty;
        overlay.TrySave();
        overlay.ValidationMessage.Should().Contain("模型不能为空");
        overlay.ModelField.Should().BeSameAs(overlay.MostFocused);
    }

    [Fact]
    public void ApiKeyField_ShowsCurrentValueAsMaskedText()
    {
        var overlay = CreateOverlay();
        overlay.ApiKeyField.Text.Should().Be("secret");
        overlay.ApiKeyField.Secret.Should().BeTrue();
    }

    [Fact]
    public void SaveAction_IsVisibleAndDocumentsShortcut()
    {
        var overlay = CreateOverlay();
        overlay.LayoutMode.Should().Be(OverlayLayoutMode.Fill);
        overlay.Title.Should().Contain("Ctrl+S 保存");
        overlay.SaveButton.Text.Should().Contain("保存");
        overlay.SaveButton.Text.Should().Contain("Ctrl+S");
    }

    [Fact]
    public void SupplementalSourceAndActivationText_IsNotShown()
    {
        var overlay = CreateOverlay();
        var visibleText = string.Join('\n', overlay.SubViews.Select(view => view.Text));

        visibleText.Should().NotContain("有效来源");
        visibleText.Should().NotContain("生效：");
        visibleText.Should().NotContain("重启后生效");
    }

    [Fact]
    public void ActionBar_KeepsSaveButtonLeftOfCancelWithoutOverlap()
    {
        var overlay = CreateOverlay();
        overlay.Layout(new Size(120, 30));

        overlay.SaveButton.Frame.Right.Should().BeLessThan(overlay.CancelButton.Frame.Left);
        overlay.CancelButton.Frame.Right.Should().BeLessThanOrEqualTo(overlay.Viewport.Width);
    }

    [Fact]
    public async Task TrySave_UnchangedApiKey_DoesNotWriteSecret()
    {
        var host = new OverlayHost(() => { });
        var overlay = CreateOverlay();
        var task = overlay.ShowAsync(host.Push, () => host.Pop(), TestContext.Current.CancellationToken);
        overlay.TrySave();
        var result = await task;
        result.Should().NotBeNull();
        result!.TargetScope.Should().Be(ConfigScope.User);
        result.ApiKey.Should().Be("secret");
        result.ApiKeyChanged.Should().BeFalse();
        result.Model.Should().Be("gpt-5.6");
        result.MaxTurns.Should().Be(20);
        TuiHostConfigurator.BuildSettingsPatch(result).Should().NotContainKey("apiKey");
    }

    [Fact]
    public async Task TrySave_ChangedApiKey_ReplacesCurrentScopeSecret()
    {
        var host = new OverlayHost(() => { });
        var overlay = CreateOverlay();
        var task = overlay.ShowAsync(host.Push, () => host.Pop(), TestContext.Current.CancellationToken);
        overlay.ApiKeyField.Text = "new-secret";
        overlay.TrySave();
        var result = await task;
        var changes = TuiHostConfigurator.BuildSettingsPatch(result!);
        changes["apiKey"].Should().BeEquivalentTo(new ConfigMutation.Set("new-secret"));
    }

    [Fact]
    public async Task TrySave_ClearedApiKey_RemovesCurrentScopeSecret()
    {
        var host = new OverlayHost(() => { });
        var overlay = CreateOverlay();
        var task = overlay.ShowAsync(host.Push, () => host.Pop(), TestContext.Current.CancellationToken);
        overlay.ApiKeyField.Text = string.Empty;
        overlay.TrySave();
        var result = await task;
        var changes = TuiHostConfigurator.BuildSettingsPatch(result!);
        changes["apiKey"].Should().BeOfType<ConfigMutation.Remove>();
    }

    private static SettingsOverlay CreateOverlay()
    {
        var settings = new AppSettings(new Dictionary<string, object?>
        {
            ["provider"] = "openai",
            ["baseUrl"] = "https://api.example.com",
            ["apiKey"] = "secret",
            ["model"] = "gpt-5.6",
            ["fastModel"] = "gpt-5.6-mini",
            ["thinkingEnabled"] = true,
            ["showThinking"] = true,
            ["notificationsEnabled"] = false,
            ["maxTurns"] = 20,
            ["effortValue"] = "high",
        });
        return new SettingsOverlay(ConfigSnapshot.FromEffective(settings), projectScopeAvailable: true);
    }
}
