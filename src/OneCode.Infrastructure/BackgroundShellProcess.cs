namespace OneCode.Infrastructure;

/// <summary>
/// 分离式后台 shell 进程（Windows `cmd /c` / Unix `sh -c`）：启动长命令、重定向输出，
/// 取消时杀掉整个进程树（cmd/sh 启动的子进程一并终止）。供后台任务类工具使用——
/// 与 <see cref="ProcessRunner"/>（等待退出的命令执行）互补，本类专注分离生命周期与树级终止。
/// </summary>
public static class BackgroundShellProcess
{
    /// <summary>
    /// 运行至退出。取消令牌触发时杀整树并抛 <see cref="OperationCanceledException"/>（由调用方映射任务取消语义）。
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken ct,
        ILogger? logger = null)
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = isWindows ? "cmd" : "sh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(isWindows ? "/c" : "-c");
        psi.ArgumentList.Add(command);

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start background shell process for '{command}'");

        // Task 'stop' 取消 token 时杀掉整个进程树；进程已退出的竞态下 Kill 抛出的异常吞掉即可。
        using var killReg = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception killEx)
            {
                logger?.LogDebug(killEx, "Failed to kill background process tree");
            }
        });

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
