using OneCode.Core.Config;
using OneCode.App.Query;
using OneCode.App.Session;
using OneCode.Automation.Cron;
using OneCode.App.Services.Mcp;
using OneCode.Core.Models;

namespace OneCode.App.Services.Cron;

/// <summary>
/// App-layer implementation of <see cref="ICronJobExecutor"/>. Wraps the previously inline
/// trigger logic of <c>CronSchedulerService.TriggerJobAsync</c>: resolve model id → build
/// (cached) system prompt → ensure foreground conversation → submit prompt under a read-only
/// tool permission policy → drain event stream to completion.
/// </summary>
/// <remarks>
/// 依赖 <see cref="IConversationRunner"/> 而非 <c>ChatService</c> 具体类：cron 只需要
/// "跑一段 prompt"的能力，不需要会话持久化/hook/token 统计等交互式职责。该抽象也是
/// 断开运行时循环依赖的关键一环（见 <see cref="IConversationRunner"/> 备注）。
///
/// Serialised by <see cref="_gate"/> so a cron-triggered run never overlaps the TUI main
/// loop's run on the same <see cref="SessionManager.ForegroundConversation"/>.
/// </remarks>
public sealed class CronJobExecutor : ICronJobExecutor
{
    private readonly ILogger<CronJobExecutor> _logger;
    private readonly IConversationRunner _runner;
    private readonly ISessionManager _sessionManager;
    private readonly PromptConfigBuilder _promptConfigBuilder;
    private readonly McpStartupPreconnector _preconnector;
    private readonly IConfigManager _configManager;
    private readonly IModelManager _modelManager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedSystemPrompt;

    public CronJobExecutor(
        ILogger<CronJobExecutor> logger,
        IConversationRunner runner,
        ISessionManager sessionManager,
        PromptConfigBuilder promptConfigBuilder,
        McpStartupPreconnector preconnector,
        IConfigManager configManager,
        IModelManager modelManager)
    {
        _logger = logger;
        _runner = runner;
        _sessionManager = sessionManager;
        _promptConfigBuilder = promptConfigBuilder;
        _preconnector = preconnector;
        _configManager = configManager;
        _modelManager = modelManager;
    }

    /// <inheritdoc />
    public async Task ExecuteJobAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            _logger.LogWarning("CronJobExecutor received empty prompt; skipping");
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var modelId = _configManager.Current.Effective.Model;
            if (string.IsNullOrEmpty(modelId))
            {
                _logger.LogWarning("CronJobExecutor: no model configured; skipping job");
                return;
            }

            // Build (and cache) the system prompt once, including the skills provider
            // rebuild. MCP 预连接已移出 PromptConfigBuilder（Plan B）——cron 是非交互
            // 路径，这里显式等待预连接完成后再构建，保证工具目录与 skills 覆盖 MCP。
            // The first build is uncancellable (prompt pipeline doesn't honour the
            // token), but subsequent await points below DO honour `ct` so a host
            // shutdown still surfaces promptly.
            await _preconnector.EnsureConnectedAsync(ct).ConfigureAwait(false);
            _cachedSystemPrompt ??= await _promptConfigBuilder.BuildSystemPromptAsync(
                ct).ConfigureAwait(false);

            // Ensure a foreground conversation exists before submitting.
            await _sessionManager.EnsureActiveSessionAsync(
                new ConversationOptions(Environment.CurrentDirectory, modelId),
                ct).ConfigureAwait(false);

            // Cron 任务无人值守，应使用 Goal 模式（自主分解+迭代验证）。
            // Plan 模式提交后进入持久化 AwaitingApproval，必须由用户审批，
            // 因此不适合作为无人值守 Cron 的执行模式。
            await foreach (var _ in _runner.StreamQueryAsync(
                prompt, _cachedSystemPrompt, modelId, ct: ct,
                workingMode: WorkingMode.Goal).ConfigureAwait(false))
            {
                // Cron runs are headless; events are not surfaced to a TUI.
            }

            var elapsed = DateTimeOffset.UtcNow - startedAt;
            var preview = prompt.Length > 50 ? prompt[..50] + "..." : prompt;
            _logger.LogInformation(
                "Cron job executed in {Seconds:F1}s: {Prompt}",
                elapsed.TotalSeconds, preview);
        }
        finally
        {
            _gate.Release();
        }
    }
}
