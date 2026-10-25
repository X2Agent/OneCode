using OneCode.Core.Config;
using OneCode.App.Query;
using OneCode.Automation.Cron;
using OneCode.App.Services.Mcp;
using OneCode.Infrastructure;

namespace OneCode.App.Services.Cron;

/// <summary>
/// App-layer implementation of <see cref="ICronJobExecutor"/>. Resolves the configured model,
/// builds (and caches) the system + harness prompts, and submits the prompt to
/// <see cref="IConversationRunner"/> in an isolated unattended session (<c>unattended: true</c>).
/// </summary>
/// <remarks>
/// 依赖 <see cref="IConversationRunner"/> 接口而非具体实现：cron 只需要
/// "跑一段 prompt"的能力，不需要会话持久化/hook/token 统计等交互式职责。该抽象也是
/// 保持构造图为 DAG 的关键一环（见 <see cref="IConversationRunner"/> 备注）。
///
/// 每次触发在独立会话 ID 与 <c>SuppressToolApproval = true</c> 下运行，既不污染用户的前台会话历史，
/// 也不会在无 UI 消费方的情况下因交互式审批弹窗而永久死锁。通过 <see cref="_gate"/> 串行化后台触发。
/// </remarks>
public sealed class CronJobExecutor(
    ILogger<CronJobExecutor> logger,
    IConversationRunner runner,
    PromptConfigBuilder promptConfigBuilder,
    McpStartupPreconnector preconnector,
    IConfigManager configManager,
    INotifierService? notifier = null) : ICronJobExecutor, IDisposable
{
    private static readonly TimeSpan DefaultJobTimeout = TimeSpan.FromMinutes(15);

    private readonly ILogger<CronJobExecutor> _logger = logger;
    private readonly IConversationRunner _runner = runner;
    private readonly PromptConfigBuilder _promptConfigBuilder = promptConfigBuilder;
    private readonly McpStartupPreconnector _preconnector = preconnector;
    private readonly IConfigManager _configManager = configManager;
    private readonly INotifierService? _notifier = notifier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedSystemPrompt;
    private string? _cachedHarnessPrompt;

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

            await _preconnector.EnsureConnectedAsync(ct).ConfigureAwait(false);
            _cachedSystemPrompt ??= await _promptConfigBuilder.BuildSystemPromptAsync(
                ct).ConfigureAwait(false);
            _cachedHarnessPrompt ??= await _promptConfigBuilder.LoadHarnessAsync(
                ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultJobTimeout);

            string? runError = null;
            await foreach (var evt in _runner.StreamQueryAsync(
                prompt,
                _cachedSystemPrompt,
                modelId,
                ct: timeoutCts.Token,
                workingMode: WorkingMode.Build,
                harnessPrompt: _cachedHarnessPrompt,
                unattended: true).ConfigureAwait(false))
            {
                if (evt is ErrorEvent err)
                    runError = err.Message;
            }

            var preview = prompt.Length > 50 ? prompt[..50] + "..." : prompt;
            if (!string.IsNullOrEmpty(runError))
            {
                await NotifyIfEnabledAsync("OneCode Cron 任务失败", runError, ct).ConfigureAwait(false);
                throw new InvalidOperationException(runError);
            }

            var elapsed = DateTimeOffset.UtcNow - startedAt;
            await NotifyIfEnabledAsync("OneCode Cron 任务完成", preview, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Cron job executed in {Seconds:F1}s: {Prompt}",
                elapsed.TotalSeconds, preview);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task NotifyIfEnabledAsync(string title, string message, CancellationToken ct)
    {
        if (_notifier is not { IsSupported: true }) return;
        if (_configManager.Current.Effective.NotificationsEnabled == false) return;
        await _notifier.SendNotificationAsync(title, message, ct).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();
}
