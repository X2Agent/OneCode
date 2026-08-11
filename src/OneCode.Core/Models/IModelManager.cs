namespace OneCode.Core.Models;

/// <summary>
/// 模型管理器接口——供 Core 层组件（如 YoloClassifier）注入使用。
/// App 层的 <c>ModelManager</c> 实现此接口，避免 Core 层反向依赖 App/Infrastructure。
/// </summary>
public interface IModelManager
{
    /// <summary>获取 fastmodel（边缘任务用）。未配置时回退到主模型。</summary>
    ModelInfo GetFastModel();

    /// <summary>
    /// 获取主模型：<paramref name="sessionOverride"/> 优先于 ConfigManager 有效配置快照；
    /// 文件、环境变量和配置会话层的优先级由 ConfigManager 统一解析
    /// </summary>
    ModelInfo GetMainModel(string? sessionOverride = null);

    /// <summary>
    /// 解析模型引用。内部 ID 为 bare model 名（如 "gpt-5.4"），也支持别名 "fast"/"default"。
    /// ContextWindow 实时委托 ModelCatalog 查询，确保 catalog 热刷新后此处返回的值始终最新。
    /// </summary>
    ModelInfo? Resolve(string? modelRef);

    /// <summary>获取默认模型。未配置时抛出 InvalidOperationException。</summary>
    ModelInfo GetDefault();

    /// <summary>返回所有已注册模型的只读列表。</summary>
    IReadOnlyList<ModelInfo> GetAll();
}
