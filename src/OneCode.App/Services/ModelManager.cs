using OneCode.Core.Models;
using OneCode.Infrastructure.Config;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App.Services;

/// <summary>
/// 统一的模型管理器——合并原 ModelResolver 与 ModelRegistry 职责。
///
/// 职责：
/// 1. 启动时从 <see cref="ConfigManager"/> 读取模型配置 + <see cref="ModelCatalog"/> 元数据，构建内部模型表
/// 2. 运行时提供统一的模型选择（4 级优先级链）和元数据查询
///
/// 主模型优先级：调用方会话参数 &gt; ConfigManager 有效快照；文件、环境变量和配置会话层由 ConfigManager 内部解析
///
/// ID 规范：内部 <see cref="ModelInfo.Id"/> 直接使用用户配置的 model 值（如 "gpt-5.4"），
/// 不拼接 provider 前缀。provider 是 API 协议标识（anthropic/openai/ollama），
/// 与模型厂商无关——拼前缀会产生 "openai/Qwen3.5" 这种语义错误的 ID。
/// </summary>
public sealed class ModelManager : IModelManager
{
    private readonly IConfigManager _configManager;
    private readonly IModelCatalog _modelCatalog;
    private readonly Dictionary<string, ModelInfo> _models;
    private readonly Dictionary<string, string> _aliases;
    private readonly string? _defaultModelId;
    private readonly Lock _modelsLock = new();

    public ModelManager(IConfigManager configManager, IModelCatalog modelCatalog)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _modelCatalog = modelCatalog ?? throw new ArgumentNullException(nameof(modelCatalog));
        _models = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 从 ConfigManager 读取配置（单一真相源）
        var configModel = configManager.Current.Effective.Model;
        var configProvider = configManager.Current.Effective.Provider;
        var configFastModel = configManager.GetSetting<string>(CoreConstants.ConfigKeys.FastModel);

        var defaultModelName = configModel;

        if (string.IsNullOrEmpty(defaultModelName))
        {
            _defaultModelId = null;
            return;
        }

        var isAnthropic = string.IsNullOrEmpty(configProvider)
            || configProvider.Equals(CoreConstants.ModelProviders.Anthropic, StringComparison.OrdinalIgnoreCase);
        var providerId = isAnthropic ? CoreConstants.ModelProviders.Anthropic : CoreConstants.ModelProviders.OpenAI;

        var fastModelName = configFastModel;

        // 注册主模型（ContextWindow 不再快照，由 Resolve 实时委托 catalog）
        AddModel(providerId, defaultModelName);

        // 注册 fast 模型（若配置且未重复）
        if (!string.IsNullOrEmpty(fastModelName))
        {
            if (!_models.ContainsKey(fastModelName))
                AddModel(providerId, fastModelName);
            _aliases["fast"] = fastModelName;
        }

        _defaultModelId = defaultModelName;
        _aliases["default"] = defaultModelName;
    }

    private void AddModel(string providerId, string modelName)
    {
        // ContextWindow 不再在此快照——由 Resolve 实时委托 IModelCatalog 读取，
        // 确保 catalog 热刷新后 ModelManager 返回的 ContextWindow 始终最新。
        // MaxOutputTokens 在 models.dev 数据中不直接提供，使用保守默认值；
        // 实际限制由 API 在运行时强制，此处仅用于 UI 提示和预算估算。
        _models[modelName] = new ModelInfo(
            Id: modelName,
            ProviderId: providerId,
            ModelId: modelName,
            MaxOutputTokens: 8192,
            ThinkingBudget: null,
            ContextWindow: 0);
    }

    /// <summary>
    /// 获取主模型。会话参数优先于 <see cref="IConfigManager"/> 已解析的有效模型。
    /// </summary>
    /// <param name="sessionOverride">会话级覆盖（如 AppState.MainLoopModel），可为 null</param>
    public ModelInfo GetMainModel(string? sessionOverride = null)
    {
        var modelRef = sessionOverride
            ?? _configManager.Current.Effective.Model
            ?? _defaultModelId;

        if (Resolve(modelRef) is { } model)
            return model;

        // Config can change while the TUI is running. A model configured
        // through /config is not present in the startup snapshot, so register
        // it before falling back to the startup default.
        if (!string.IsNullOrEmpty(modelRef))
        {
            EnsureModelRegistered(modelRef);
            if (Resolve(modelRef) is { } runtimeModel)
                return runtimeModel;
        }

        return GetDefault();
    }

    /// <summary>
    /// 获取 fastmodel（用于结构化 JSON 拆解、记忆提取、Hook 执行等边缘任务）。
    /// 未配置 fast 时回退到 <see cref="GetMainModel"/>。
    /// <para>
    /// 实时从 <see cref="IConfigManager"/> 读取 <c>fastModel</c> 配置——与 <see cref="GetMainModel"/> 
    /// 对称，settings 面板修改后立即对后续调用生效，无需重启。
    /// </para>
    /// </summary>
    public ModelInfo GetFastModel()
    {
        var fastModelName = _configManager.GetSetting<string>(CoreConstants.ConfigKeys.FastModel);

        if (!string.IsNullOrEmpty(fastModelName))
        {
            if (Resolve(fastModelName) is { } fast)
                return fast;

            EnsureModelRegistered(fastModelName);
            if (Resolve(fastModelName) is { } registered)
                return registered;
        }

        return GetMainModel();
    }

    /// <summary>
    /// 解析模型引用。内部 ID 为 bare model 名（如 "gpt-5.4"）。
    /// ContextWindow 实时委托 <see cref="IModelCatalog"/> 查询，
    /// 确保 catalog 热刷新后此处返回的值始终最新。
    /// </summary>
    public ModelInfo? Resolve(string? modelRef)
    {
        if (string.IsNullOrEmpty(modelRef)) return null;

        lock (_modelsLock)
        {
            if (_models.TryGetValue(modelRef, out var model))
                return MergeWithCatalog(model);

            if (_aliases.TryGetValue(modelRef, out var resolved)
                && _models.TryGetValue(resolved, out var aliased))
                return MergeWithCatalog(aliased);

            return null;
        }
    }

    /// <summary>
    /// 从 <see cref="IModelCatalog"/> 实时填充 ContextWindow。
    /// catalog 热刷新后，模型将自动获得最新值。
    /// </summary>
    private ModelInfo MergeWithCatalog(ModelInfo model)
    {
        if (model.ContextWindow > 0) return model;

        var liveWindow = _modelCatalog.GetContextWindow(model.Id);
        if (liveWindow > 0)
            return model with { ContextWindow = liveWindow };
        return model;
    }

    /// <summary>
    /// 获取默认模型。未配置时抛出异常。
    /// </summary>
    public ModelInfo GetDefault()
    {
        if (_defaultModelId is not null && Resolve(_defaultModelId) is { } model)
            return model;

        throw new InvalidOperationException(
            "未配置默认模型。请在 settings.json 中设置 \"model\"，或通过环境变量 ONECODE_MODEL 配置。");
    }

    /// <summary>
    /// 返回所有已注册模型的只读列表。供 /model 命令展示。
    /// </summary>
    public IReadOnlyList<ModelInfo> GetAll()
    {
        lock (_modelsLock)
        {
            return _models.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// 若 <paramref name="modelName"/> 未在 <c>_models</c> 中注册，用主模型 provider 动态注册。
    /// 供 <see cref="GetFastModel"/> 处理“运行时配置变更引入的新模型”。
    /// 动态注册改 <c>_models</c>/<c>_aliases</c>，需加锁保护并发读。
    /// </summary>
    private void EnsureModelRegistered(string modelName)
    {
        lock (_modelsLock)
        {
            if (_models.ContainsKey(modelName))
                return;

            var configProvider = _configManager.Current.Effective.Provider;
            var isAnthropic = string.IsNullOrEmpty(configProvider)
                || configProvider.Equals(CoreConstants.ModelProviders.Anthropic, StringComparison.OrdinalIgnoreCase);
            var providerId = isAnthropic
                ? CoreConstants.ModelProviders.Anthropic
                : CoreConstants.ModelProviders.OpenAI;

            AddModel(providerId, modelName);
            _aliases["fast"] = modelName;
        }
    }
}
