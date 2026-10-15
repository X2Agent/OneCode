using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using OneCode.App.Services.Memory;
using OneCode.App.Session;
using OneCode.Core.Config;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Memory;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 后台服务——任务完成驱动的增量记忆整合。
/// </summary>
/// <remarks>
/// <para>
/// 当一次 <c>BUILD</c> 任务正常完成且未回滚（或用户执行 <c>/memory autodream trigger</c>）时，
/// 先在 C# 层进行零 Token 候选快检（当前内存会话是否存在有效对话轮次，或存在 working-memory 笔记）；
/// 存在候选时在后台启动单轮无工具整合 Agent，基于当前对话与已挂载的 <c>MEMORY.md</c>
/// 将持久事实、约定、排错教训与用户纠正幂等合并写入 <c>MEMORY.md</c>。
/// </para>
/// <para>不写入 AGENTS.md——那是人工维护的规范，自动改写会污染它。</para>
/// </remarks>
public sealed class AutoDreamService : BackgroundService, IAutoDreamService
{
    /// <summary>Agent 单次输出的最大 token 数。</summary>
    private const int MaxOutputTokens = 4096;

    /// <summary>单轮无工具整理的最大迭代次数。</summary>
    private const int MaxIterations = 1;

    private readonly ILogger<AutoDreamService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IChatClient _chatClient;
    private readonly IModelManager _modelManager;
    private readonly IConfigManager _configManager;
    private readonly IWorkingDirectoryAccessor _wdAccessor;
    private readonly IMemoryService? _memoryService;
    private readonly ISessionManager? _sessionManager;

    /// <summary>外部触发信号通道：NotifyTaskCompleted() / Trigger() 写入，ExecuteAsync 读取。</summary>
    private readonly Channel<bool> _triggerChannel =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    /// <summary>是否正在运行整合（进程内重入保护）。</summary>
    private volatile bool _isRunning;

    private readonly string _globalConfigDir;

    private readonly AutoDreamWorkingMemoryScanner _workingMemoryScanner;
    private readonly AutoDreamMemoryApplier _memoryApplier;
    private readonly AutoDreamConsolidationPromptBuilder _promptBuilder;
    private readonly AutoDreamStateStore _stateStore;

    public AutoDreamService(
        ILogger<AutoDreamService> logger,
        ILoggerFactory loggerFactory,
        AutoDreamAgentDependencies agent,
        AutoDreamStorageDependencies storage,
        string? globalConfigDirOverride = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _chatClient = agent.ChatClient;
        _modelManager = agent.ModelManager;
        _memoryService = agent.MemoryService;
        _configManager = storage.ConfigManager;
        _wdAccessor = storage.WorkingDirectory;
        _sessionManager = storage.SessionManager;
        _globalConfigDir = globalConfigDirOverride ?? PathsHelper.GetUserConfigDir();
        _workingMemoryScanner = new AutoDreamWorkingMemoryScanner(loggerFactory.CreateLogger<AutoDreamWorkingMemoryScanner>());
        _stateStore = new AutoDreamStateStore(logger, GetProjectStateDir);
        _memoryApplier = new AutoDreamMemoryApplier(storage.EntryStore, logger, GetCurrentProjectRoot);
        _promptBuilder = new AutoDreamConsolidationPromptBuilder(
            agent.PromptManager,
            _workingMemoryScanner,
            GetCurrentProjectRoot,
            () => _sessionManager?.ForegroundConversation);
    }

    // BackgroundService 主循环

    /// <summary>
    /// 主循环：挂起等待 <see cref="NotifyTaskCompleted"/> 或 <see cref="Trigger"/> 信号，无周期空转轮询。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("AutoDream background service started (event-driven on BUILD task completion)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var force = await _triggerChannel.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);

                // 排空触发通道中可能残留的信号；若任一信号为 force，则保留 force 语义
                while (_triggerChannel.Reader.TryRead(out var nextForce))
                {
                    force |= nextForce;
                }

                await TryConsolidateAsync(force, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AutoDream consolidation cycle failed");
            }
        }

        _logger.LogDebug("AutoDream background service stopped");
    }

    // 外部触发

    /// <inheritdoc />
    public void NotifyTaskCompleted() => _triggerChannel.Writer.TryWrite(false);

    /// <inheritdoc />
    public void Trigger(bool force = true) => _triggerChannel.Writer.TryWrite(force);

    // 核心整合逻辑

    /// <summary>
    /// 执行一次增量快检 + 整合尝试。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    public Task<bool> TryConsolidateAsync(CancellationToken ct = default) =>
        TryConsolidateAsync(force: false, ct);

    /// <summary>
    /// 执行一次增量快检 + 整合尝试。
    /// </summary>
    /// <param name="force">为 <see langword="true"/> 时（如手动 <c>/memory autodream trigger</c>）跳过增量快检。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<bool> TryConsolidateAsync(bool force, CancellationToken ct = default)
    {
        if (_isRunning)
        {
            _logger.LogDebug("AutoDream already running, skip");
            return false;
        }

        // 启用检查（默认开，ONECODE_AUTODREAM=false 或 ONECODE_REMOTE=true 关闭）
        if (!IsEnabled())
        {
            _logger.LogDebug("AutoDream disabled (ONECODE_AUTODREAM=false or ONECODE_REMOTE=true)");
            return false;
        }

        var projectRoot = GetCurrentProjectRoot();
        var hasConversationTurns = _promptBuilder.HasCurrentConversationTurns();
        var hasWorkingMemoryArtefacts = !string.IsNullOrWhiteSpace(projectRoot)
            && _workingMemoryScanner.Collect(projectRoot, maxArtefacts: 1, ct).Count > 0;

        if (!force && !hasConversationTurns && !hasWorkingMemoryArtefacts)
        {
            _logger.LogDebug(
                "AutoDream candidate gate: no active current-conversation turns or working-memory artefacts");
            return false;
        }

        // 获取跨进程锁（FileStream 独占，原子获取）
        var lockStream = _stateStore.TryAcquireConsolidationLock();
        if (lockStream is null)
        {
            _logger.LogDebug("AutoDream consolidation lock not acquired (another process is running)");
            return false;
        }

        _isRunning = true;
        try
        {
            _logger.LogInformation(
                "Starting AutoDream consolidation: conversationTurns={HasConversationTurns}, workingMemory={HasWorkingMemoryArtefacts}, force={Force}",
                hasConversationTurns, hasWorkingMemoryArtefacts, force);

            var prompt = await _promptBuilder.BuildConsolidationPromptAsync(ct)
                .ConfigureAwait(false);
            var (succeeded, inputTokens, outputTokens, entriesWritten) = await RunConsolidationAgentAsync(prompt, ct)
                .ConfigureAwait(false);

            if (!succeeded)
            {
                _logger.LogWarning(
                    "AutoDream output parsing or application failed ({Input}+{Output} tokens)",
                    inputTokens, outputTokens);
                return false;
            }

            _logger.LogInformation(
                "AutoDream completed: {Input}+{Output} tokens, {Entries} memory entries written",
                inputTokens, outputTokens, entriesWritten);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("AutoDream was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoDream failed");
            return false;
        }
        finally
        {
            _isRunning = false;
            lockStream.Dispose();
        }
    }

    // Agent 执行

    /// <summary>
    /// Consolidation-agent 的 <see cref="HarnessAgentOptions"/>。提为 internal 出厂方法，
    /// 使守卫测试无需运行 Agent 即可断言 provider 挂载决策。
    /// </summary>
    internal static HarnessAgentOptions BuildConsolidationAgentOptions(
        string modelId,
        IReadOnlyList<AITool> tools,
        int maxOutputTokens,
        int maxToolCalls,
        IMemoryService? memoryService = null,
        ILoggerFactory? loggerFactory = null)
    {
        var agentOptions = new HarnessAgentOptions
        {
            Name = "autodream",
            HarnessInstructions = OneCodeHarnessDefaults.SuppressFrameworkDefaults,
            ChatOptions = new ChatOptions
            {
                ModelId = modelId,
                MaxOutputTokens = maxOutputTokens,
                Tools = tools.Count > 0 ? tools.ToList() : null,
                ToolMode = tools.Count > 0 ? ChatToolMode.Auto : null,
            },
            MaximumIterationsPerRequest = maxToolCalls,
            DisableTodoProvider = true,
            DisableFileMemory = true,
            AIContextProviders = memoryService is not null
                ? [new MemoryContextProvider(memoryService, loggerFactory?.CreateLogger<MemoryContextProvider>())]
                : [],
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(agentOptions);
        return agentOptions;
    }

    private async Task<(bool Succeeded, long InputTokens, long OutputTokens, int EntriesWritten)> RunConsolidationAgentAsync(
        string prompt, CancellationToken ct)
    {
        // 模型：fastModel（轻量任务用）→ 主 model（未配置 fastModel 时回退）
        var modelId = _modelManager.GetFastModel().Id;

        var agentOptions = BuildConsolidationAgentOptions(
            modelId,
            tools: [],
            MaxOutputTokens,
            MaxIterations,
            _memoryService,
            _loggerFactory);

        var agent = new HarnessAgent(_chatClient, agentOptions, _loggerFactory);
        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        var response = await agent.RunAsync(messages, session, new AgentRunOptions(), ct)
            .ConfigureAwait(false);

        var inputTokens = (long)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (long)(response.Usage?.OutputTokenCount ?? 0);

        var outputText = response.Messages
            .OfType<ChatMessage>()
            .LastOrDefault(m => m.Role == ChatRole.Assistant)
            ?.Text ?? string.Empty;

        var (succeeded, entriesWritten) = await _memoryApplier
            .TryApplyConsolidationChangesAsync(outputText, ct)
            .ConfigureAwait(false);
        return (succeeded, inputTokens, outputTokens, entriesWritten);
    }

    // 门控配置（settings.json 优先，环境变量覆盖，默认开）

    /// <summary>
    /// 是否启用 AutoDream。
    /// 优先级：环境变量 ONECODE_AUTODREAM > settings.json autodream.enabled > 默认 true。
    /// ONECODE_REMOTE=true 时无论配置如何都关闭。
    /// </summary>
    internal bool IsEnabled()
    {
        if (GetEnvBool("ONECODE_REMOTE", defaultValue: false))
            return false;

        if (Environment.GetEnvironmentVariable("ONECODE_AUTODREAM") is { } envVal)
            return !string.Equals(envVal, "false", StringComparison.OrdinalIgnoreCase) && !string.Equals(envVal, "0", StringComparison.Ordinal);

        return _configManager.GetSetting("autodream.enabled", true);
    }

    private static bool GetEnvBool(string name, bool defaultValue) =>
        Environment.GetEnvironmentVariable(name) is { } v
            ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            : defaultValue;

    // 候选检测与路径工具

    internal IReadOnlyList<string> CollectCurrentConversationTurns() =>
        _promptBuilder.CollectCurrentConversationTurns();

    internal bool HasCurrentConversationTurns() =>
        _promptBuilder.HasCurrentConversationTurns();

    private string GetConfigDir() => _globalConfigDir;

    /// <summary>
    /// AutoDream 运行时状态文件目录 <c>{cwd}/.onecode/memory/</c>——
    /// 存放跨进程锁 <c>autodream.lock</c>，与 <c>MEMORY.md</c> 同目录。
    /// workingDirectory 为空时回退到全局目录（防御性降级）。
    /// </summary>
    internal string GetProjectStateDir()
    {
        var cwd = GetCurrentProjectRoot();
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            return MemdirPaths.ProjectMemoryDir(cwd);
        }
        return GetConfigDir();
    }

    /// <summary>当前 workingDirectory（来自 IWorkingDirectoryAccessor），可能为 null。</summary>
    internal string? GetCurrentProjectRoot() => _wdAccessor.WorkingDirectory;

    // 仅单元测试使用：生产代码当前无调用方（测试接缝）。
    internal string GetStateFilePath(string fileName) =>
        Path.Combine(GetProjectStateDir(), fileName);
}
