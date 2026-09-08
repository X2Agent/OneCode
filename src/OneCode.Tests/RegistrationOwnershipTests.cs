using System.Text.RegularExpressions;

namespace OneCode.Tests;

/// <summary>
/// 架构防护：DI 注册必须随领域实现走（领域目录下的 XxxServiceCollectionExtensions），
/// 按启动批次分桶的 ServiceCollectionExtensions partial 类禁止复活。
/// 顺序不变量由 <see cref="ServiceCollectionSnapshotTests"/> 锁定。
/// </summary>
public sealed class RegistrationOwnershipTests
{
    /// <summary>实例级注册调用：AddSingleton/AddScoped/AddTransient/AddHostedService/AddHttpClient。</summary>
    private static readonly Regex RegistrationCall = new(
        @"\b(services|s)\.Add(Singleton|Scoped|Transient|HostedService|HttpClient|Commands)\b"
        + @"|\.AddTool<"
        + @"|\.AddToolInstance\("
        + @"|\.AddToolStatic\(",
        RegexOptions.Compiled);

    [Fact]
    public void RegistrationCode_LivesOnlyInDomainOwnedServiceCollectionExtensions()
    {
        var appRoot = FindAppRoot();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            if (!RegistrationCall.IsMatch(content))
            {
                continue;
            }

            var relative = file[(appRoot.Length + 1)..];
            var fileName = Path.GetFileName(file);

            if (!fileName.EndsWith("ServiceCollectionExtensions.cs", StringComparison.Ordinal))
            {
                violations.Add($"{relative}: 注册调用出现在非注册类文件中——请移到领域目录的 XxxServiceCollectionExtensions.cs");
            }
            else if (Path.GetDirectoryName(file)!.Equals(appRoot, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{relative}: 注册类不得位于 OneCode.App 根目录——请放到领域目录（如 Services/<Domain>/）");
            }
        }

        violations.Should().BeEmpty(
            "DI 注册必须随领域实现走（注册类与实现同目录）；组合根 OneCodeApp.Create 只保留显式有序的 AddXxx() 调用列表。" +
            "确属跨域基础设施的例外应在此测试的白名单中登记并说明理由。");
    }

    [Fact]
    public void RetiredServiceCollectionExtensionsClassName_DoesNotRevive()
    {
        var revived = Directory.EnumerateFiles(FindAppRoot(), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Where(content => content.Contains("class ServiceCollectionExtensions"))
            .ToList();

        revived.Should().BeEmpty(
            "按启动批次分桶的 ServiceCollectionExtensions partial 类已全部解散为领域自有注册类；" +
            "如需新增注册桶，请按领域命名（如 XxxServiceCollectionExtensions）并放到领域目录。");
    }

    /// <summary>从测试二进制目录向上定位 src/OneCode.App（不依赖进程工作目录）。</summary>
    private static string FindAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "OneCode.App");
            if (File.Exists(Path.Combine(candidate, "OneCode.App.csproj")))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"无法从 {AppContext.BaseDirectory} 向上定位 src/OneCode.App。");
    }
}
