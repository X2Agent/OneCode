using OneCode.Core.Models;

namespace OneCode.Automation.ModelCatalog;

/// <summary>
/// 后台服务：启动时同步加载磁盘缓存，定期检查并刷新 models.dev 快照。
/// </summary>
public sealed class ModelCatalogRefreshService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IModelCatalogCache _cache;
    private readonly IModelCatalog _catalog;
    private readonly ILogger<ModelCatalogRefreshService> _logger;

    public ModelCatalogRefreshService(
        IModelCatalogCache cache,
        IModelCatalog catalog,
        ILogger<ModelCatalogRefreshService>? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCatalogRefreshService>.Instance;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_catalog.Count == 0)
            {
                _logger.LogDebug("Model catalog is empty, retrying disk cache load in StartAsync");
                _cache.TryLoadFromCache();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload model catalog from disk cache in StartAsync");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_cache.IsStale())
            {
                _logger.LogInformation("Model catalog cache is stale or missing, refreshing from models.dev");
                await _cache.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial model catalog refresh failed, using disk cache or empty catalog (will retry later)");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
                if (_cache.IsStale())
                {
                    await _cache.RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in model catalog refresh cycle");
            }
        }
    }
}
