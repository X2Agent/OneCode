namespace OneCode.Core.Commands;

/// <summary>
/// 动态命令来源接口（C-04 修复）。
///
/// 各来源（Plugin / Skill / MCP / Workflow）实现此接口，
/// CommandRegistry 通过 RefreshDynamicCommandsAsync 统一刷新。
/// </summary>
public interface IDynamicCommandSource
{
    CommandSource Source { get; }

    Task<IReadOnlyList<ICommand>> LoadCommandsAsync(CancellationToken ct);
}
