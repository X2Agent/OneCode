using OneCode.App;
using OneCode.App.Daemon;

namespace OneCode.Tui;

/// <summary>
/// OneCode TUI 入口——三层启动架构：
/// <list type="number">
///   <item>--cwd/-C 预处理：在快路径与 DI 之前切换进程工作目录并从 args 剥离。</item>
///   <item>Fast-path 检测：--version 在 DI 容器初始化前直接处理。</item>
///   <item>OneCodeApp 执行：启动交互式 TUI。所有配置通过 TUI slash 命令和 SettingsOverlay 完成。</item>
/// </list>
/// </summary>
public static class Program
{
    /// <summary>用法错误退出码（如 --cwd 缺参/目录不存在），与 git -C 惯例一致。</summary>
    private const int UsageErrorCode = 2;

    public static async Task<int> Main(string[] args)
    {
        var cwd = LaunchWorkingDirectory.Parse(args);
        if (cwd.Error is not null)
        {
            Console.Error.WriteLine(cwd.Error);
            return UsageErrorCode;
        }
        if (cwd.Path is not null && !LaunchWorkingDirectory.TryApply(cwd.Path, out var applyError))
        {
            Console.Error.WriteLine(applyError);
            return UsageErrorCode;
        }
        args = cwd.Remaining;

        var mode = LaunchModeDetector.Detect(args);

        // daemon 是全量 DI 宿主（最重路径），不走绕过 DI 的 FastPathDispatcher；
        // 动词是 /daemon start 与 OS 调度器共用的内部启动协议，非用户功能入口。
        if (mode == LaunchMode.Daemon)
        {
            return await DaemonHost.RunAsync().ConfigureAwait(false);
        }

        if (mode != LaunchMode.Interactive)
        {
            return await FastPathDispatcher.DispatchAsync(args, mode);
        }

        await using var app = OneCodeApp.Create(args, services => services.AddTuiServices());
        return await app.RunAsync(CancellationToken.None);
    }
}
