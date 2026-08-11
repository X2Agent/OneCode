using OneCode.App;

namespace OneCode.Cli;

/// <summary>
/// OneCode CLI 入口——两层启动架构：
/// <list type="number">
///   <item>Fast-path 检测：--version 在 DI 容器初始化前直接处理。</item>
///   <item>OneCodeApp 执行：启动交互式 TUI。所有配置通过 TUI slash 命令和 SettingsOverlay 完成。</item>
/// </list>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = CliModeDetector.Detect(args);

        if (mode != CliMode.FullCli)
        {
            return await FastPathDispatcher.DispatchAsync(args, mode);
        }

        await using var app = OneCodeApp.Create(args);
        return await app.RunAsync(CancellationToken.None);
    }
}
