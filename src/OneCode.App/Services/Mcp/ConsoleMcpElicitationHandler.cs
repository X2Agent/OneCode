using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services.Mcp;

/// <summary>
/// Factory for console-backed <see cref="McpElicitationHandler"/> (stdin prompts + OS browser).
/// </summary>
public static class ConsoleMcpElicitationHandler
{
    public static McpElicitationHandler Create(ILogger<McpElicitationHandler> logger)
        => new(logger, PromptAsync, OpenBrowserAsync);

    internal static Task<string?> PromptAsync(string prompt, CancellationToken ct)
    {
        // Fast buffered console write; blocking input offloaded to the thread pool.
        Console.Write(prompt);
        // 不把 ct 传给 Task.Run：已取消时任务会立即进入 Canceled 而非返回结果，
        // 调用方约定"取消 = 空响应"。取消语义由轮询循环自身保证。
        return Task.Run(() => ReadLineCancellable(ct));

        static string? ReadLineCancellable(CancellationToken ct)
        {
            // Console.ReadLine 无法被取消中断。交互控制台下用 KeyAvailable 轮询让
            // 取消及时生效；输入重定向（管道/CI）不支持 KeyAvailable，回退阻塞
            // ReadLine——取消后线程池线程随读到的一行结束，属已知局限。
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (Console.KeyAvailable)
                        return Console.ReadLine();
                }
                catch (InvalidOperationException)
                {
                    return Console.ReadLine(); // stdin 已重定向
                }

                Thread.Sleep(50);
            }

            return null;
        }
    }

    private static Task OpenBrowserAsync(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open browser: {ex.Message}");
            Console.WriteLine($"Please open: {url}");
        }

        return Task.CompletedTask;
    }
}
