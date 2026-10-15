namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 任务级后台记忆整合服务契约。
/// </summary>
public interface IAutoDreamService
{
    /// <summary>
    /// 通知一次 BUILD 模式任务已正常完成，触发后台增量记忆整合检查。
    /// </summary>
    void NotifyTaskCompleted();

    /// <summary>
    /// 手动触发一次记忆整合（由 <c>/memory autodream trigger</c> 调用）。
    /// </summary>
    /// <param name="force">为 <see langword="true"/> 时跳过候选非空快检，强制运行整合 Agent。</param>
    void Trigger(bool force = true);
}
