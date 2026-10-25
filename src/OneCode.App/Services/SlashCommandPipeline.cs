namespace OneCode.App.Services;

/// <summary>
/// <see cref="SlashCommandPipeline.TryResolvePromptCommandAsync"/> 的判别结果，
/// 指示宿主分发层如何处理该命令。
/// </summary>
public abstract record CommandDispatchResult
{
    /// <summary>命令产出一段 prompt，经 LLM 查询流水线流式呈现。</summary>
    public sealed record Prompt(string Content, string[]? AllowedTools) : CommandDispatchResult;

    /// <summary>命令请求恢复持久化工作流——直接路由到工作流恢复流。</summary>
    public sealed record ResumeWorkflow(string SessionId, WorkflowResumeKind Kind) : CommandDispatchResult;

    /// <summary>命令请求运行有界确定性循环——直接路由到循环流。</summary>
    public sealed record Loop(string Task, string? CheckCommand, int MaxIterations) : CommandDispatchResult;
}

/// <summary>
/// 斜杠命令解析与执行。持有缓存非 prompt 结果的 <see cref="CommandExecutionState"/>，
/// 以及供宿主生命周期查询的退出标记。
/// </summary>
/// <remarks>
/// <see cref="CommandState"/> 与 <c>TuiHostConfigurator</c> 共享（用于会话 UI 刷新接线）。
/// 实例持有会话内可变状态：TUI 单用户场景用单例，多电路宿主（WebUI）每电路新建实例。
/// </remarks>
public sealed class SlashCommandPipeline(
    ICommandRegistry commandRegistry)
{
    private readonly CommandExecutionState _cmdState = new();
    private volatile bool _exitRequested;

    /// <summary>ExitResult 命令（如 /exit）执行后为 true。</summary>
    public bool IsExitRequested => _exitRequested;

    /// <summary>斜杠命令管线共享的可变状态。</summary>
    public CommandExecutionState CommandState => _cmdState;

    /// <summary>
    /// 执行斜杠命令并判断是否产出 PromptResult / ResumeWorkflowResult / LoopResult。
    /// 返回 <see cref="CommandDispatchResult"/> 指示宿主分发层如何继续；
    /// 都不是时缓存结果，供 <see cref="ExecuteCommandAsync"/> 取用。
    /// </summary>
    public async Task<CommandDispatchResult?> TryResolvePromptCommandAsync(
        InteractiveSession session, string text, CancellationToken ct)
    {
        var cmd = commandRegistry.Find(text);
        if (cmd is null) return null;

        var result = await ExecuteFoundCommandAsync(cmd, text, ct).ConfigureAwait(false);

        if (result is CommandResult.PromptResult pr)
            return new CommandDispatchResult.Prompt(pr.Content, pr.AllowedTools);

        if (result is CommandResult.ResumeWorkflowResult rw)
            return new CommandDispatchResult.ResumeWorkflow(rw.SessionId, rw.Kind);

        if (result is CommandResult.LoopResult loop)
            return new CommandDispatchResult.Loop(loop.Task, loop.CheckCommand, loop.MaxIterations);

        _cmdState.CacheResult(result);
        return null;
    }

    /// <summary>
    /// 执行斜杠命令。若 <see cref="TryResolvePromptCommandAsync"/> 已执行并缓存了结果
    /// （非 PromptResult），直接返回缓存而不重复执行；否则执行命令并映射结果。
    /// </summary>
    public async Task<string?> ExecuteCommandAsync(
        InteractiveSession session, string text, CancellationToken ct)
    {
        if (_cmdState.ConsumeCachedResult() is { } cached)
            return MapResultToDisplay(cached);

        var cmd = commandRegistry.Find(text);
        if (cmd is null)
        {
            var name = text.TrimStart('/').Split(' ')[0];
            return commandRegistry.Suggest(text) ?? $"Unknown command '/{name}'. Type /help to see available commands.";
        }

        var result = await ExecuteFoundCommandAsync(cmd, text, ct).ConfigureAwait(false);
        return MapResultToDisplay(result);
    }

    private async Task<CommandResult> ExecuteFoundCommandAsync(
        ICommand cmd, string text, CancellationToken ct)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = parts.Length > 1 ? parts[1..] : [];

        var result = await cmd.ExecuteAsync(args, ct).ConfigureAwait(false);

        if (result is CommandResult.ExitResult)
            _exitRequested = true;

        // 影响会话的命令：session（list/switch/new/close）、new、close。
        // 刷新宿主 UI，使转录/计数反映前台会话（/close 后无前台会话时整体清空）。
        if (result is not CommandResult.ErrorResult && cmd.Name is "session" or "new" or "close")
            await _cmdState.RefreshSessionUiAsync(ct).ConfigureAwait(false);

        return result;
    }

    private static string? MapResultToDisplay(CommandResult result) => result switch
    {
        CommandResult.TextResult t => t.Value,
        CommandResult.ErrorResult e => e.Message,
        CommandResult.ExitResult => "Goodbye!",
        CommandResult.SilentResult => null,
        _ => null,
    };
}
