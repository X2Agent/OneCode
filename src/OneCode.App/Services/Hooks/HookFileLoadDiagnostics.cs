namespace OneCode.App.Services.Hooks;

/// <summary>单个 hooks.json 文件的加载状态。</summary>
public enum HookFileLoadStatus
{
    /// <summary>文件不存在（未配置 hooks，属正常状态）。</summary>
    NotFound,

    /// <summary>成功加载（可能带非致命解析警告，见 <see cref="HookFileLoadReport.Errors"/>）。</summary>
    Loaded,

    /// <summary>加载失败（JSON 损坏 / IO 错误 / 根节点不是对象）。</summary>
    Failed,
}

/// <summary>单个 hooks.json 文件的加载结果快照，供 /hooks 概览展示。</summary>
/// <param name="ConfigDir">配置目录（hooks.json 的上层目录）。</param>
/// <param name="Status">加载状态。</param>
/// <param name="HookCount">成功注册的 hook 数量。</param>
/// <param name="Errors">非致命诊断消息（已迁移/不再支持的事件名等）。</param>
public sealed record HookFileLoadReport(
    string ConfigDir,
    HookFileLoadStatus Status,
    int HookCount,
    IReadOnlyList<string> Errors);

/// <summary>Loader 层结果：解析出的 hook 配置 + 诊断消息。</summary>
/// <param name="Path">hooks.json 绝对路径。</param>
/// <param name="Status">加载状态。</param>
/// <param name="Hooks">拦截点 → matcher 组列表；加载失败或为空时为 null。</param>
/// <param name="Errors">非致命诊断消息。</param>
public sealed record HookFileLoadResult(
    string Path,
    HookFileLoadStatus Status,
    Dictionary<HookInterceptionPoint, List<HookMatcherGroup>>? Hooks,
    IReadOnlyList<string> Errors);

/// <summary>
/// 进程级 hook 配置加载诊断（最近一次 Bootstrap 的各文件状态）。
/// /hooks 概览据此展示每个 hooks.json 的加载状态与解析错误，排障不再只靠翻日志。
/// </summary>
public sealed class HookLoadDiagnostics
{
    private readonly object _lock = new();
    private IReadOnlyList<HookFileLoadReport> _reports = [];

    /// <summary>记录一次 Bootstrap 的各文件加载结果（整体替换）。</summary>
    public void Record(IReadOnlyList<HookFileLoadReport> reports)
    {
        lock (_lock)
            _reports = reports;
    }

    /// <summary>最近一次 Bootstrap 的各文件加载结果（快照，只读）。</summary>
    public IReadOnlyList<HookFileLoadReport> Reports
    {
        get
        {
            lock (_lock)
                return _reports;
        }
    }
}
