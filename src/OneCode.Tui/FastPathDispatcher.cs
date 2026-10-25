using System.Reflection;
using OneCode.Core.Product;

namespace OneCode.Tui;

public static class FastPathDispatcher
{
    public static Task<int> DispatchAsync(string[] args, LaunchMode mode)
    {
        return mode switch
        {
            LaunchMode.FastPathVersion => Task.FromResult(HandleVersion()),
            _ => Task.FromResult(-1)
        };
    }

    private static int HandleVersion()
    {
        // 与 /version 命令同一口径：InformationalVersion 携带预发布后缀（如 1.0.0-beta.1），
        // AssemblyVersion 只有 x.y.z——发布冒烟测试按完整 semver 断言。
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? ProductInfo.Default.Version;
        Console.WriteLine($"{version} ({ProductInfo.Default.Name})");
        return 0;
    }
}
