using System.Reflection;
using OneCode.Core.Product;

namespace OneCode.Cli;

public static class FastPathDispatcher
{
    public static Task<int> DispatchAsync(string[] args, CliMode mode)
    {
        return mode switch
        {
            CliMode.FastPathVersion => Task.FromResult(HandleVersion()),
            _ => Task.FromResult(-1)
        };
    }

    private static int HandleVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? ProductInfo.Default.Version;
        Console.WriteLine($"{version} ({ProductInfo.Default.Name})");
        return 0;
    }
}
