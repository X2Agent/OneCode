using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Commands;

/// <summary>
/// Injects <see cref="IServiceProvider"/> instead of <see cref="ICommandRegistry"/>
/// to break the circular dependency: CommandRegistry → IEnumerable&lt;ICommand&gt; → HelpCommand → ICommandRegistry.
/// Resolution is deferred to <see cref="ExecuteAsync"/> when the registry is already constructed.
/// </summary>
public sealed class HelpCommand(IServiceProvider services) : Command
{
    private ICommandRegistry? _registry;
    private ICommandRegistry Registry => _registry ??= services.GetRequiredService<ICommandRegistry>();
    public override string Name => "help";
    public override string Description => "Show available commands and keyboard shortcuts";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override IReadOnlyList<string> Aliases => ["?"];

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // Commands
        sb.AppendLine("Available Commands:");
        sb.AppendLine();

        var grouped = Registry.GetGrouped();
        var categoryOrder = new (CommandCategory Cat, string Label)[]
        {
            (CommandCategory.Builtin, "Built-in"),
            (CommandCategory.Session, "Session"),
            (CommandCategory.Diagnostic, "Diagnostics"),
            (CommandCategory.Skill, "Skills & MCP"),
            (CommandCategory.Git, "Git"),
        };

        foreach (var (category, label) in categoryOrder)
        {
            if (!grouped.TryGetValue(category, out var cmds) || cmds.Count == 0)
                continue;

            sb.AppendLine(CultureInfo.InvariantCulture, $"  {label}:");
            foreach (var cmd in cmds.Where(c => !c.IsHidden).OrderBy(c => c.Name))
            {
                var aliases = cmd.Aliases.Count > 0
                    ? $" ({string.Join(", ", cmd.Aliases.Select(a => $"/{a}"))})"
                    : "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"    /{cmd.Name,-18} {cmd.Description}{aliases}");
            }
            sb.AppendLine();
        }

        // Keyboard Shortcuts — must match KeybindingDefaults + hardcoded TUI keys.
        // Do not list unimplemented shortcuts (they erode trust more than omitting them).
        sb.AppendLine("Keyboard Shortcuts:");
        sb.AppendLine();

        var shortcutGroups = new (string Section, (string Key, string Desc)[] Entries)[]
        {
            ("输入与发送",
            [
                ("Enter",            "发送消息 / 确认补全或选择"),
                ("Shift+Enter",      "换行（需 kitty 协议终端）"),
                ("Alt+Enter",        "换行（通用回退）"),
                ("Ctrl+V",           "粘贴（折叠多行、识别图片/文件）"),
                ("Esc",              "关闭补全；响应中中断 agent；空闲无操作"),
            ]),
            ("模式与补全",
            [
                ("Tab",              "空输入接受建议；/ 补全循环；否则切 BUILD/PLAN/TEAM/GOAL"),
                ("/",                "斜杠命令补全"),
                ("Ctrl+← / Ctrl+→",  "循环占位建议"),
                ("Shift+Tab",        "TEAM 模式切换 Magentic ↔ GroupChat"),
                ("Ctrl+Shift+T",     "TEAM 模式循环切换已注册团队"),
            ]),
            ("历史与搜索",
            [
                ("↑ / ↓",            "历史命令（光标在首/末行时）/ 弹窗内导航"),
                ("Ctrl+↑",           "召回上一条用户消息以便编辑重发"),
                ("/find <关键词>",   "搜索会话并跳转匹配 · /find next 下一处"),
            ]),
            ("覆盖层与审查",
            [
                ("/diff",            "审查 Git 变更（可下钻 Diff）"),
                ("↑↓ Enter Esc",     "权限 / Plan 等 InlineSelector 选择与确认"),
            ]),
            ("全局",
            [
                ("Ctrl+C",           "复制选中文本"),
                ("Ctrl+D",           "退出"),
                ("Esc（响应中）",     "中断当前 agent（不退出）；可自定义 chat:killAgents"),
            ]),
        };

        foreach (var (section, entries) in shortcutGroups)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {section}:");
            foreach (var (key, desc) in entries)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {key,-18} {desc}");
            sb.AppendLine();
        }

        return Task.FromResult(CommandResult.Text(sb.ToString().TrimEnd()));
    }
}
