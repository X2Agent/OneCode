using OneCode.Core.Config;
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

        var providerId = ResolveProviderId(configProvider);

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
        // ContextWindow 不再在此快照——由 ResolveMetadata 实时解析（本地 Ollama 取
        // ollamaContextWindow，其余 provider 委托 IModelCatalog），确保热刷新后始终最新。
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
                return ResolveMetadata(model);

            if (_aliases.TryGetValue(modelRef, out var resolved)
                && _models.TryGetValue(resolved, out var aliased))
                return ResolveMetadata(aliased);

            return null;
        }
    }

    /// <summary>
    /// 实时解析 ContextWindow，永不返回 0。
    ///
    /// <para>本地 Ollama 是例外分支：其窗口由我们下发的 <c>num_ctx</c>（<c>ollamaContextWindow</c>）决定，
    /// 也就是服务端真正强制的窗口；models.dev 不覆盖本地模型，catalog 值与之无关，故无条件优先。
    /// 宁可低估（压缩提前触发）也不可高估（服务端静默丢弃最旧消息）。</para>
    ///
    /// <para>其余 provider 走 <see cref="IModelCatalog"/>（catalog 热刷新后自动获得最新值），
    /// 未命中时回落到 <see cref="ModelContextDefaults.DefaultContextWindow"/>——与
    /// <c>TokenBudget.GetMaxContextTokens</c> 的既有回落口径一致。返回 0 会让压缩装配把 0
    /// 当作有效窗口传入严格校验并直接失败。</para>
    /// </summary>
    private ModelInfo ResolveMetadata(ModelInfo model)
    {
        if (IsLocalOllama)
            return model with { ContextWindow = ResolveLocalContextWindow() };

        if (model.ContextWindow > 0) return model;

        var liveWindow = _modelCatalog.GetContextWindow(model.Id);
        if (liveWindow > 0)
            return model with { ContextWindow = liveWindow };
        return model with { ContextWindow = ModelContextDefaults.DefaultContextWindow };
    }

    /// <summary>当前 provider 是否为本地 Ollama 部署（精确匹配，与 <c>num_ctx</c> 的注入条件一致）。</summary>
    private bool IsLocalOllama => string.Equals(
        _configManager.Current.Effective.Provider,
        CoreConstants.ModelProviders.Ollama,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>本地窗口真相源：下发的 <c>num_ctx</c>；未配置（≤ 0）时回落到保守默认值。</summary>
    private int ResolveLocalContextWindow()
    {
        var configured = _configManager.Current.Effective.OllamaContextWindow;
        return configured > 0 ? configured : ModelContextDefaults.DefaultContextWindow;
    }

    /// <summary>
    /// provider 配置 → API 协议标识（<see cref="ModelInfo.ProviderId"/>）。
    /// 空值与 anthropic 均视为 Anthropic（历史语义）；ollama 必须保留为 "ollama"，
    /// 否则工具结果序列化会走 OpenAI 的 JSON 分支，与本地模型返回 Markdown 的设计相反。
    /// </summary>
    private static string ResolveProviderId(string? configProvider)
    {
        if (string.IsNullOrEmpty(configProvider)
            || configProvider.Equals(CoreConstants.ModelProviders.Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            return CoreConstants.ModelProviders.Anthropic;
        }

        return configProvider.Equals(CoreConstants.ModelProviders.Ollama, StringComparison.OrdinalIgnoreCase)
            ? CoreConstants.ModelProviders.Ollama
            : CoreConstants.ModelProviders.OpenAI;
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

            AddModel(ResolveProviderId(_configManager.Current.Effective.Provider), modelName);
            _aliases["fast"] = modelName;
        }
    }
}
