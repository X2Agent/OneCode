using OneCode.Core.Keybindings;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="KeybindingViewBuilder"/>：生效绑定去重（后匹配生效）与来源分类。
/// 输入按 KeybindingLoader 的合并语义构造：默认在前、用户在后。
/// </summary>
public sealed class KeybindingViewBuilderTests
{
    [Fact]
    public void Build_UserOverridesDefault_LastEntryWinsAndMarkedCustom()
    {
        var merged = MergeDefaults(UserBlock(("ctrl+g", "command:foo")));

        var views = KeybindingViewBuilder.Build(merged);

        var ctrlG = views.Single(v => v.Context == "Chat" && v.KeyDisplay == "Ctrl+G");
        ctrlG.Action.Should().Be("command:foo");
        ctrlG.Source.Should().Be(KeybindingSource.Custom);
    }

    [Fact]
    public void Build_UserUnbindsDefault_MarkedUnbound()
    {
        var merged = MergeDefaults(UserBlock(("ctrl+g", null)));

        var views = KeybindingViewBuilder.Build(merged);

        var ctrlG = views.Single(v => v.Context == "Chat" && v.KeyDisplay == "Ctrl+G");
        ctrlG.Action.Should().BeNull();
        ctrlG.Source.Should().Be(KeybindingSource.Unbound);
    }

    [Fact]
    public void Build_UserAddsNewKey_MarkedCustom()
    {
        var merged = MergeDefaults(UserBlock(("ctrl+f9", "app:exit")));

        var views = KeybindingViewBuilder.Build(merged);

        var added = views.Single(v => v.Context == "Chat" && v.KeyDisplay == "Ctrl+F9");
        added.Action.Should().Be("app:exit");
        added.Source.Should().Be(KeybindingSource.Custom);
    }

    [Fact]
    public void Build_PureDefaults_AllMarkedDefault()
    {
        var views = KeybindingViewBuilder.Build([.. KeybindingDefaults.GetDefaultParsedBindings()]);

        views.Should().NotBeEmpty();
        views.Should().OnlyContain(v => v.Source == KeybindingSource.Default);
    }

    [Fact]
    public void Build_ChordKey_TitleCaseWithSpaceSeparator()
    {
        var merged = MergeDefaults(UserBlock(("ctrl+x ctrl+k", "chat:killAgents")));

        var views = KeybindingViewBuilder.Build(merged);

        views.Should().Contain(v => v.KeyDisplay == "Ctrl+X Ctrl+K" && v.Action == "chat:killAgents");
    }

    /// <summary>按 loader 合并语义拼接：默认绑定在前，用户绑定追加在后。</summary>
    private static List<KeybindingEntry> MergeDefaults(KeybindingBlock userBlock)
    {
        var defaults = KeybindingDefaults.GetDefaultParsedBindings();
        defaults.AddRange(KeybindingParser.ParseBindings([userBlock]));
        return defaults;
    }

    private static KeybindingBlock UserBlock(params (string Key, string? Action)[] chatBindings) =>
        new("Chat", chatBindings.ToDictionary(t => t.Key, t => t.Action));
}
