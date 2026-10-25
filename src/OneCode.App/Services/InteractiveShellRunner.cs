namespace OneCode.App.Services;

/// <summary>
/// 组合根驱动交互前端的唯一入口。前端在自身注册段绑定具体实现（TUI 绑
/// <c>InteractiveModeExecutor.ExecuteAsync</c>）；用委托而非接口，既保证 App 不反向引用前端项目，
/// 也不为「单实现 + 单调用点」造一层抽象壳。
/// </summary>
public delegate Task<int> InteractiveShellRunner(CancellationToken ct);
