namespace OneCode.Core.Models;

/// <summary>
/// 模型目录磁盘缓存服务契约。
/// Infrastructure 层实现此接口，负责从 models.dev API 拉取数据、持久化到磁盘缓存文件、
/// 并通过 <see cref="ModelCatalogStore.Replace"/> 热替换内存中的 catalog。
/// </summary>
public interface IModelCatalogCache
{
    /// <summary>
    /// 尝试从磁盘缓存文件加载 catalog 并更新 <see cref="ModelCatalogStore"/>。
    /// 缓存文件不存在或损坏时返回 false。
    /// </summary>
    bool TryLoadFromCache();

    /// <summary>
    /// 缓存文件是否已过期（超过刷新阈值或不存在）。
    /// </summary>
    bool IsStale();

    /// <summary>
    /// 从 models.dev API 拉取最新数据，写入磁盘缓存文件，并更新内存 catalog。
    /// 网络失败或写入失败时返回 false（内存 catalog 保持不变）。
    /// </summary>
    Task<bool> RefreshAsync(CancellationToken ct = default);
}
