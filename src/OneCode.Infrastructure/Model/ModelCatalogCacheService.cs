namespace OneCode.Infrastructure.Model;

using Microsoft.Extensions.Logging;
using OneCode.Core.Models;
using OneCode.Infrastructure.Config;

/// <summary>
/// 模型目录磁盘缓存服务 — 管理 ~/.onecode/cache/models-dev-snapshot.json。
/// </summary>
public sealed class ModelCatalogCacheService : IModelCatalogCache
{
    private const string CacheFileName = "models-dev-snapshot.json";

    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromDays(7);

    private readonly ModelsDevClient _client;
    private readonly ModelCatalogStore _catalogStore;
    private readonly ILogger<ModelCatalogCacheService> _logger;
    private readonly string _cachePath;

    public ModelCatalogCacheService(
        ModelsDevClient client,
        ModelCatalogStore catalogStore,
        ILogger<ModelCatalogCacheService>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCatalogCacheService>.Instance;

        var home = PathsHelper.UserHome;
        var cacheDir = Path.Combine(home, Constants.App.ConfigDirName, Constants.Subdirs.Cache);
        Directory.CreateDirectory(cacheDir);
        _cachePath = Path.Combine(cacheDir, CacheFileName);
    }

    /// <inheritdoc />
    public bool TryLoadFromCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return false;
            using var fs = File.OpenRead(_cachePath);
            var catalog = ModelCatalog.LoadFromStream(fs);
            _catalogStore.Replace(catalog);
            _logger.LogDebug("Loaded model catalog from cache file: {Path}", _cachePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load model catalog from cache file: {Path}", _cachePath);
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsStale()
    {
        if (!File.Exists(_cachePath)) return true;
        var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_cachePath);
        return age > RefreshThreshold;
    }

    /// <inheritdoc />
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var stream = await _client.FetchAsync(ct).ConfigureAwait(false);
        if (stream is null) return false;

        try
        {
            var tempPath = _cachePath + ".tmp";
            using (stream)
            using (var fs = File.Create(tempPath))
            {
                await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            File.Move(tempPath, _cachePath, overwrite: true);

            using var reloadStream = File.OpenRead(_cachePath);
            var catalog = ModelCatalog.LoadFromStream(reloadStream);
            _catalogStore.Replace(catalog);

            _logger.LogInformation("Model catalog refreshed from models.dev ({Count} models)", catalog.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist/refresh model catalog cache");
            return false;
        }
    }
}
