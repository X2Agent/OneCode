using NSubstitute;
using OneCode.App.Services;
using OneCode.Core.Commands;
using OneCode.Core.Config;
using OneCode.Core.Domain;
using OneCode.Core.Models;
using OneCode.Core.Tools;
using OneCode.Tests.TestSupport;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Tests;

/// <summary>
/// TUI 运行时模型名解析的回归防护。
///
/// 背景：状态栏曾缓存启动时的模型名（<c>InteractiveSession.Model</c> 快照），
/// <c>/model</c> 切换后状态栏一直显示旧模型。修复后显示层一律经
/// <see cref="TuiContextFactory.ResolveRuntimeModelId"/> 实时取值，
/// 且优先级必须与查询派工一致（会话覆盖 &gt; 配置有效值）。
/// </summary>
public sealed class TuiContextFactoryTests
{
    private static TuiCatalogDependencies CreateCatalog(
        IReadOnlyDictionary<string, object?> configuredValues,
        string? sessionOverride)
    {
        var configManager = TestConfigManager.Create(new AppSettings(new Dictionary<string, object?>(configuredValues)));
        // ModelManager 经 GetSetting<string>("fastModel") 注册 fast 别名（而非 Current 快照），
        // 测试替身必须与生产 ConfigManager 读同一份值。
        if (configuredValues.TryGetValue(CoreConstants.ConfigKeys.FastModel, out var fast)
            && fast is string fastModel)
        {
            configManager.GetSetting<string>(CoreConstants.ConfigKeys.FastModel).Returns(fastModel);
        }

        var appState = Substitute.For<IAppStateAccessor>();
        appState.Current.Returns(new AppState { MainLoopModel = sessionOverride });

        return new TuiCatalogDependencies(
            new ModelManager(configManager, Substitute.For<IModelCatalog>()),
            Substitute.For<IModelCatalog>(),
            appState,
            configManager,
            Substitute.For<ICommandRegistry>(),
            Substitute.For<IToolCatalog>());
    }

    [Fact]
    public void ResolveRuntimeModelId_ModelSwitchedAtRuntime_ReturnsSessionOverrideNotConfiguredModel()
    {
        var catalog = CreateCatalog(
            new Dictionary<string, object?> { [CoreConstants.ConfigKeys.Model] = "configured-model" },
            sessionOverride: "nvidia/nemotron-3.5-lightning:free");

        TuiContextFactory.ResolveRuntimeModelId(catalog)
            .Should().Be("nvidia/nemotron-3.5-lightning:free",
                "/model 写入的是 MainLoopModel 覆盖项，状态栏必须跟随它而不是启动时的配置值");
    }

    [Fact]
    public void ResolveRuntimeModelId_NoSessionOverride_FallsBackToConfiguredModel()
    {
        var catalog = CreateCatalog(
            new Dictionary<string, object?> { [CoreConstants.ConfigKeys.Model] = "configured-model" },
            sessionOverride: null);

        TuiContextFactory.ResolveRuntimeModelId(catalog).Should().Be("configured-model");
    }

    [Fact]
    public void ResolveRuntimeModelId_AliasOverride_ReturnsResolvedModelId()
    {
        var catalog = CreateCatalog(
            new Dictionary<string, object?>
            {
                [CoreConstants.ConfigKeys.Model] = "configured-model",
                [CoreConstants.ConfigKeys.FastModel] = "fast-model",
            },
            sessionOverride: "fast");

        TuiContextFactory.ResolveRuntimeModelId(catalog)
            .Should().Be("fast-model", "展示名须与查询实际使用的模型一致，别名要归一化");
    }

    [Fact]
    public void ResolveRuntimeModelId_NothingConfigured_ReturnsEmptyWithoutThrowing()
    {
        var catalog = CreateCatalog(new Dictionary<string, object?>(), sessionOverride: null);

        TuiContextFactory.ResolveRuntimeModelId(catalog)
            .Should().BeEmpty("未配置模型时显示层返回空串，缺配置由查询路径给出提示");
    }
}
