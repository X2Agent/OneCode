using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services.Mcp;

/// <summary>
/// Factory for console-backed <see cref="McpElicitationHandler"/> (stdin prompts + OS browser).
/// </summary>
public static class ConsoleMcpElicitationHandler
{
    public static McpElicitationHandler Create(ILogger<McpElicitationHandler> logger)
        => new(logger, PromptAsync, OpenBrowserAsync);

    private static Task<string?> PromptAsync(string prompt, CancellationToken ct)
    {
        // Fast buffered console write; blocking ReadLine offloaded to the thread pool.
        Console.Write(prompt);
        return Task.Run(Console.ReadLine, ct);
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
