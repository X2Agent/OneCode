using OneCode.App.Commands;
using OneCode.Core.Commands;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// 补全列表显示测试（P2-10）：SlashCommandEntry 的 ArgumentHint 应显示在弹窗中；
/// 过滤仅按命令名前缀匹配，描述与参数提示不参与。
/// </summary>
public sealed class ChatCompletionControllerTests
{
    [Fact]
    public void UpdateCompletionList_ShowsArgumentHint()
    {
        var controller = new ChatCompletionController(
            new[]
            {
                new SlashCommandEntry("add-dir", "Add a directory to the project context", CommandSource.Builtin, "<path> [--persist]"),
                new SlashCommandEntry("help", "Show help", CommandSource.Builtin),
            },
            typeaheadEngine: null);

        controller.UpdateCompletionList("/add");

        controller.CurrentDisplayItems.Should().NotBeNull();
        controller.CurrentDisplayItems!.Should().Contain(item => item.Contains("<path> [--persist]"));
    }

    [Fact]
    public void UpdateCompletionList_FiltersByCommandNamePrefixOnly()
    {
        var controller = new ChatCompletionController(
            new[]
            {
                new SlashCommandEntry("keybindings", "View or customize keyboard shortcuts by editing keybindings.json", CommandSource.Builtin, "[list|open|reset]"),
                new SlashCommandEntry("model", "Switch the AI model (API key required)", CommandSource.Builtin),
            },
            typeaheadEngine: null);

        // 命令名前缀命中
        controller.UpdateCompletionList("/key");

        controller.FilteredCommands.Should().Contain(c => c.Name == "keybindings");
        controller.FilteredCommands.Should().NotContain(c => c.Name == "model");
    }

    [Fact]
    public void UpdateCompletionList_KeywordInDescription_DoesNotMatch()
    {
        var controller = new ChatCompletionController(
            new[]
            {
                // "key" 仅出现在描述与参数提示中，不在命令名里
                new SlashCommandEntry("model", "Switch the AI model (API key required)", CommandSource.Builtin, "[name]"),
            },
            typeaheadEngine: null);

        controller.UpdateCompletionList("/key");

        controller.IsCompletionActive.Should().BeFalse();
        controller.FilteredCommands.Should().NotContain(c => c.Name == "model");
    }

    [Fact]
    public void UpdateCompletionList_TypoQuery_FuzzyMatchesIntendedCommand()
    {
        var controller = new ChatCompletionController(
            new[]
            {
                new SlashCommandEntry("keybindings", "View or customize keyboard shortcuts", CommandSource.Builtin),
                new SlashCommandEntry("model", "Switch the AI model", CommandSource.Builtin),
            },
            typeaheadEngine: null);

        // "keyband" 是 "keybindings" 的拼写错误，应通过 JaroWinkler 模糊命中
        controller.UpdateCompletionList("/keyband");

        controller.FilteredCommands.Should().Contain(c => c.Name == "keybindings");
        controller.FilteredCommands.Should().NotContain(c => c.Name == "model");
    }

    [Fact]
    public void UpdateCompletionList_PrefixMatch_RanksBeforeFuzzyMatch()
    {
        var controller = new ChatCompletionController(
            new[]
            {
                // "key" 与查询 "keyband" 相似（JaroWinkler 命中）但非前缀匹配；
                // "keybindings" 相似度更高。两者都走模糊路径，按分数降序排列。
                new SlashCommandEntry("key", "Show the key", CommandSource.Builtin),
                new SlashCommandEntry("keybindings", "View or customize keyboard shortcuts", CommandSource.Builtin),
            },
            typeaheadEngine: null);

        controller.UpdateCompletionList("/keyband");

        var names = controller.FilteredCommands.Select(c => c.Name).ToList();
        names.IndexOf("keybindings").Should().BeLessThan(names.IndexOf("key"), "相似度更高的命令应排在前面");
    }

    [Fact]
    public void UpdateCompletionList_UnrelatedQuery_HidesCompletion()
    {
        var controller = new ChatCompletionController(
            new[]
            {
                new SlashCommandEntry("keybindings", "View or customize keyboard shortcuts", CommandSource.Builtin),
            },
            typeaheadEngine: null);

        controller.UpdateCompletionList("/xyzzy");

        controller.IsCompletionActive.Should().BeFalse();
    }

    [Fact]
    public void UpdateCompletionList_ShortQuery_DoesNotFuzzyMatch()
    {
        var controller = new ChatCompletionController(
            new[]
            {
                // "ey" 与 "keybindings" 的 JaroWinkler ≈ 0.79（超过阈值），
                // 但查询仅 2 字符不触发模糊匹配——短查询模糊命中多为噪音
                new SlashCommandEntry("keybindings", "View or customize keyboard shortcuts", CommandSource.Builtin),
            },
            typeaheadEngine: null);

        controller.UpdateCompletionList("/ey");

        controller.IsCompletionActive.Should().BeFalse();
    }

    [Fact]
    public void PopupTitle_SwitchesBetweenCommandsFilesAndSessionsByMode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"onecode-popup-title-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "README.md"), "# readme");
            var commands = new[]
            {
                new SlashCommandEntry("help", "Show help", CommandSource.Builtin),
            };
            var engine = new TypeaheadCompletionEngine(
                commands,
                tempDir,
                sessionCandidatesProvider: () =>
                [
                    new OneCode.App.Session.SessionCandidate("4f8a90c1e0d244ef8dba35b2588c0424", "会话", "build", 3),
                ]);
            var controller = new ChatCompletionController(commands, engine);

            controller.UpdateCompletionList("/he");
            controller.IsCompletionActive.Should().BeTrue();
            controller.PopupTitle.Should().Be(ChatCompletionController.CommandsPopupTitle);

            controller.TryTypeaheadCompletion("@READ");
            controller.IsCompletionActive.Should().BeTrue();
            controller.PopupTitle.Should().Be(ChatCompletionController.FilesPopupTitle);

            controller.TryTypeaheadCompletion("/session switch 4f");
            controller.IsCompletionActive.Should().BeTrue();
            controller.PopupTitle.Should().Be(ChatCompletionController.SessionsPopupTitle);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryTypeaheadCompletion_EmptyWorkspaceBareAt_ShowsEmptyHintAndAcceptReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"onecode-empty-at-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var commands = new[]
            {
                new SlashCommandEntry("help", "Show help", CommandSource.Builtin),
            };
            var engine = new TypeaheadCompletionEngine(commands, tempDir);
            var controller = new ChatCompletionController(commands, engine);

            // 先触发一次斜杠命令补全，验证后续进入空目录 @ 提示时不会残留旧命令被误 Accept
            controller.UpdateCompletionList("/help");
            controller.IsCompletionActive.Should().BeTrue();

            controller.TryTypeaheadCompletion("@");

            controller.IsCompletionActive.Should().BeTrue();
            controller.PopupTitle.Should().Be(ChatCompletionController.FilesPopupTitle);
            controller.CurrentDisplayItems.Should().Equal(ChatCompletionController.EmptyWorkspaceFilesHint);

            controller.CycleNext();
            controller.SelectedIndex.Should().Be(0);

            controller.Accept().Should().BeNull();
            controller.IsCompletionActive.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryTypeaheadCompletion_AtQueryWithNoMatches_ShowsNoMatchingFilesHint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"onecode-nomatch-at-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "README.md"), "# readme");
            var engine = new TypeaheadCompletionEngine([], tempDir);
            var controller = new ChatCompletionController([], engine);

            controller.TryTypeaheadCompletion("@zzz");

            controller.IsCompletionActive.Should().BeTrue();
            controller.PopupTitle.Should().Be(ChatCompletionController.FilesPopupTitle);
            controller.CurrentDisplayItems.Should().Equal(ChatCompletionController.NoMatchingFilesHint);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
