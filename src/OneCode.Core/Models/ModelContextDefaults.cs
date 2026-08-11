namespace OneCode.Core.Models;

/// <summary>
/// 模型上下文长度解析器 — 两层兜底机制。
///
/// Layer 1: <see cref="IModelCatalog"/>（models.dev 磁盘缓存）
/// Layer 2: 保守默认值 128_000（catalog 未命中时兜底）
/// </summary>
public static class ModelContextDefaults
{
    /// <summary>保守默认上下文长度（catalog 未命中时使用）。</summary>
    public const int DefaultContextWindow = 128_000;

    /// <summary>
    /// 按模型 ID 解析上下文长度。
    /// </summary>
    public static int Resolve(string? modelId, IModelCatalog? catalog = null)
    {
        if (string.IsNullOrEmpty(modelId))
            return DefaultContextWindow;

        if (catalog is not null)
        {
            var catalogValue = catalog.GetContextWindow(modelId);
            if (catalogValue > 0)
                return catalogValue;
        }

        return DefaultContextWindow;
    }
}
