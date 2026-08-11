using OneCode.Core.Permissions.Yolo;

namespace OneCode.Automation.Yolo;

/// <summary>
/// 启动时加载 <c>~/.onecode/yolo_rules.json</c> 到 <see cref="YoloRuleStore"/>。
///
/// 设计选择（HostedService 而非 DI 工厂内同步加载）：
/// - 同步阻塞 DI 工厂会拖慢启动且违反 async 规范
/// - 文件不存在/解析失败不应阻断启动（已由 <see cref="IYoloRuleFileStore"/> 兜底）
/// - 加载晚于首次工具调用不会导致功能错误：YoloRuleStore 构造时已装入置默认规则
/// </summary>
public sealed class YoloRuleStoreLoader : IHostedService
{
    private readonly YoloRuleStore _ruleStore;
    private readonly IYoloRuleFileStore _fileStore;
    private readonly ILogger<YoloRuleStoreLoader>? _logger;

    public YoloRuleStoreLoader(
        YoloRuleStore ruleStore,
        IYoloRuleFileStore fileStore,
        ILogger<YoloRuleStoreLoader>? logger = null)
    {
        _ruleStore = ruleStore;
        _fileStore = fileStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rules = await _fileStore.LoadOrDefaultsAsync(cancellationToken).ConfigureAwait(false);
            _ruleStore.ReplaceRules(rules);
            _logger?.LogDebug(
                "YOLO rules loaded: {Count} rules from {Path}",
                _ruleStore.Rules.Count, _fileStore.RulesPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load YOLO rules on startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
