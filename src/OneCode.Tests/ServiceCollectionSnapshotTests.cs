using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneCode.App;
using OneCode.App.Commands;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Query;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.AutoDream;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Coordinator;
using OneCode.App.Services.Setup;
using OneCode.App.Services.Context;
using OneCode.App.Services.Cron;
using OneCode.App.Services.Hooks;
using OneCode.App.Services.Lsp;
using OneCode.App.Services.Loop;
using OneCode.App.Services.Mcp;
using OneCode.App.Services.Memory;
using OneCode.App.Services.Observability;
using OneCode.App.Services.Permissions;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Search;
using OneCode.App.Services.Skills;
using OneCode.App.Services.Tasks;
using OneCode.App.Session;
using OneCode.App.Tui;
using OneCode.App.Tools;
using NSubstitute;

namespace OneCode.Tests;

/// <summary>
/// 注册面快照测试：组合根 DI 注册序列（类型+生命周期+实现+顺序）与基线逐行比对，漂移即失败。
/// 确定性约定：实例/工厂不输出运行时值与方法名（会随机器或无关编辑漂移），仅标记 "(instance)"/"(factory)"。
/// 基线变更：设 <c>ONECODE_UPDATE_SNAPSHOT=1</c> 再生成并人工审查 diff；规范见 src/AGENTS.md「DI 注册归属」。
/// </summary>
public sealed class ServiceCollectionSnapshotTests
{
    private const string GoldenFileName = "ServiceRegistrationSnapshot.approved.txt";

    [Fact]
    public void ProductionRegistrationPipeline_MatchesApprovedSnapshot()
    {
        var services = CreateProductionRegistrations();
        var actual = Describe(services);

        var goldenPath = Path.Combine(FindTestsDirectory(), GoldenFileName);

        if (Environment.GetEnvironmentVariable("ONECODE_UPDATE_SNAPSHOT") == "1")
        {
            File.WriteAllText(goldenPath, actual);
            return;
        }

        File.Exists(goldenPath).Should().BeTrue(
            $"快照基线 {GoldenFileName} 缺失——设置 ONECODE_UPDATE_SNAPSHOT=1 运行本测试生成");

        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");

        actual.Should().Be(expected,
            "服务注册面（顺序/生命周期/实现）发生变化。若这是有意的 DI 变更，请设置 " +
            "ONECODE_UPDATE_SNAPSHOT=1 重新生成基线并人工审查 diff；否则说明注册被意外" +
            "移动或修改——注册代码应随领域归属（见 src/AGENTS.md 注册归属规范）。");
    }

    [Fact]
    public async Task HostedServices_ResolveInRegistrationOrder()
    {
        // 解析全部 IHostedService 实例：GetServices 返回顺序 = AddHostedService 注册顺序 =
        // 宿主启动顺序（StartAsync 按 IEnumerable 顺序执行）。同时验证全部 hosted 服务可构造。
        //
        // 宿主运行时基础设施（ILoggerFactory / IHostApplicationLifetime）由真实 Host 提供，
        // 不属于领域注册面——在此以测试替身补齐，不进入快照基线。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
        PopulateProductionRegistrations(services);

        await using var provider = services.BuildServiceProvider();

        var hostedTypeNames = provider.GetServices<IHostedService>()
            .Select(h => h.GetType().Name)
            .ToList();

        hostedTypeNames.Should().Equal(
            "SkillFilesWatcher",             // RegisterSkillServices：技能目录变更通知（UI 刷新）
            "HostStopSessionCloseService",   // Hook 子系统：宿主停止时兜底关闭前台会话
            "YoloRuleStoreLoader",           // Permission 子系统：Yolo 规则加载
            "CronSchedulerService",          // Cron：定时任务调度
            "ModelCatalogRefreshService",    // 模型目录：定时刷新
            "PlanExecutionRecoveryService",  // Plan：恢复中断的执行
            "LspHostedService",              // LSP：语言服务器自动启动（不阻塞启动）
            "AutoDreamService");             // AutoDream：后台记忆整合（1h 轮询）
    }

    /// <summary>
    /// 启动期冒烟：组合根注册图必须可解析。
    ///
    /// 发版最常见的启动期故障是「某服务缺依赖 / 生命周期不匹配」，这类问题只有真正把 TUI
    /// 跑起来才会暴露，普通单测抓不到。此处用 <c>ValidateOnBuild</c> 静态校验全部调用点
    /// ——**不实例化任何服务、无副作用**，等价于「宿主能否成功构建服务图」的冒烟。
    /// </summary>
    [Fact]
    public void ProductionRegistrations_ValidateOnBuild_ServiceGraphIsResolvable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
        PopulateProductionRegistrations(services);

        var options = new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        };

        ServiceProvider? provider = null;
        var build = () => provider = services.BuildServiceProvider(options);

        build.Should().NotThrow(
            "组合根注册图必须可解析——否则 TUI 启动即崩，而这类故障在无终端环境下无法冒烟");

        provider?.Dispose();
    }

    /// <summary>
    /// 复现组合根 <see cref="OneCodeApp.Create"/> 的完整注册链（除 logging bootstrap 外，
    /// 顺序逐一对应）。组合根的调用序列与本方法必须保持同步——任何一方的顺序变更都是
    /// 行为变更，须重新生成快照基线。
    /// </summary>
    private static ServiceCollection CreateProductionRegistrations()
    {
        var services = new ServiceCollection();
        PopulateProductionRegistrations(services);
        return services;
    }

    private static void PopulateProductionRegistrations(IServiceCollection services)
    {
        var workingDir = Path.Combine(Path.GetTempPath(), "onecode-snapshot-workingdir");
        Directory.CreateDirectory(workingDir);

        services
            .AddSkillServices(workingDir)
            .AddChatClientServices()
            .AddSessionServices(workingDir)
            .AddTaskServices()
            .AddPlanModeServices()
            .AddContextServices()
            .AddSearchServices()
            .AddMcpServices()
            .AddTokenObservabilityServices()
            .AddHookServices()
            .AddPermissionServices()
            .AddCommandSourceServices()
            .AddCronSchedulingServices()
            .AddModelCatalogServices()
            .AddBuildModeServices()
            .AddPlanWorkflowServices()
            .AddChatQueryServices()
            .AddSetupServices()
            .AddAgentRuntimeServices()
            .AddGoalServices()
            .AddLoopServices()
            .AddToolServices()
            .AddMemoryServices()
            .AddCompactServices()
            .AddPromptServices(workingDir)
            .AddLspServices()
            .AddNamedHttpClients()
            .AddTeamServices()
            .AddAutoDreamServices()
            .AddPlatformServices()
            .AddCommands()
            .AddInteractiveServices();
    }

    /// <summary>
    /// 每个描述符一行：<c>序号 | 生命周期 | 服务类型 -> 实现</c>。序号即注册顺序。
    /// </summary>
    private static string Describe(IServiceCollection services)
    {
        var sb = new StringBuilder();
        var ordinal = 0;
        foreach (var descriptor in services)
        {
            ordinal++;
            var implementation = descriptor.ImplementationType is not null
                ? FormatType(descriptor.ImplementationType)
                : descriptor.ImplementationFactory is not null
                    ? "(factory)"
                    : "(instance)";
            sb.Append(CultureInfo.InvariantCulture,
                $"{ordinal,4} {descriptor.Lifetime,-9} {FormatType(descriptor.ServiceType)} -> {implementation}\n");
        }
        return sb.ToString();
    }

    /// <summary>泛型类型展开为"定义[参数]"形式，避免 FullName 中的程序集版本号泄漏进基线。</summary>
    private static string FormatType(Type type) =>
        type.IsConstructedGenericType
            ? $"{type.GetGenericTypeDefinition().FullName}[{string.Join(',', type.GenericTypeArguments.Select(FormatType))}]"
            : type.FullName ?? type.Name;

    /// <summary>从测试二进制目录向上定位 src/OneCode.Tests（不依赖进程工作目录）。</summary>
    private static string FindTestsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "OneCode.Tests");
            if (File.Exists(Path.Combine(candidate, "OneCode.Tests.csproj")))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            $"无法从 {AppContext.BaseDirectory} 向上定位 src/OneCode.Tests——快照基线文件路径无法确定。");
    }
}
