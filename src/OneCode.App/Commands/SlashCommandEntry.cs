namespace OneCode.App.Commands;

/// <summary>
/// 补全列表中的命令条目 —— 内置命令 / 技能 / MCP 共用此结构。
/// 携带 <see cref="Source"/> 以支持分组显示：输入 / 时补全列表按来源分组，用分隔行区分。
/// </summary>
public sealed record SlashCommandEntry(
    string Name,
    string Description,
    CommandSource Source = CommandSource.Builtin,
    string? ArgumentHint = null);
