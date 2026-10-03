using OneCode.Core.Config;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using OneCode.Core.Models;
using OneCode.Infrastructure;
using System.Threading.Channels;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Memory;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 后台服务——睡眠式记忆整合。
/// </summary>
/// <remarks>
/// <para>
/// 模拟大脑睡眠时整理记忆的过程：当用户积累了足够多的新会话后，
/// 自动启动轻量 Agent 回顾这些会话，提取关键信息以增量变更 JSON 形式合并写入
/// <c>MEMORY.md</c>，供后续会话使用。
/// </para>
///
/// <para><b>默认开启</b>，无需任何配置。两个自然门控防止频繁触发：</para>
/// <list type="bullet">
/// <item>距上次整合 ≥ 6 小时</item>
/// <item>新会话数 ≥ 3 个</item>
/// </list>
/// <para>不写入 AGENTS.md——那是人工维护的规范，自动改写会污染它。</para>
/// </remarks>
public sealed class AutoDreamService : BackgroundService
{
    // 内部常量（非用户配置，实现细节）

    /// <summary>轮询间隔：1 小时。轮询是唯一的自动触发路径。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    /// <summary>会话扫描节流：10 分钟内不重复扫描（IO 操作，避免频繁磁盘访问）。</summary>
    private static readonly TimeSpan SessionScanInterval = TimeSpan.FromMinutes(10);

    /// <summary>Agent 单次输出的最大 token 数。</summary>
    private const int MaxOutputTokens = 4096;

    /// <summary>AutoDream Agent 最大工具调用次数（防止无限循环）。</summary>
    private const int MaxToolCalls = 30;

    /// <summary>AutoDream 允许使用的工具白名单（仅只读工具，用于扫描会话目录）。</summary>
    private static readonly string[] AllowedTools = ["Read", "Glob", "Grep"];

    // 依赖

    private readonly ILogger<AutoDreamService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IChatClient _chatClient;
    private readonly IToolCatalog _toolCatalog;
    private readonly IModelManager _modelManager;
    private readonly IConfigManager _configManager;
    private readonly IWorkingDirectoryAccessor _wdAccessor;

    /// <summary>外部触发信号通道：Trigger() 写入，ExecuteAsync 读取。</summary>
    private readonly Channel<bool> _triggerChannel =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <summary>是否正在运行整合（进程内重入保护）。</summary>
    private volatile bool _isRunning;

    private readonly string _globalConfigDir;

    private readonly AutoDreamSessionScanner _sessionScanner;
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
        _toolCatalog = agent.ToolCatalog;
        _modelManager = agent.ModelManager;
        _configManager = storage.ConfigManager;
        _wdAccessor = storage.WorkingDirectory;
        _globalConfigDir = globalConfigDirOverride ?? PathsHelper.GetUserConfigDir();
        _sessionScanner = new AutoDreamSessionScanner(logger, _globalConfigDir);
        _stateStore = new AutoDreamStateStore(logger, GetProjectStateDir);
        _memoryApplier = new AutoDreamMemoryApplier(storage.EntryStore, logger, GetCurrentProjectRoot);
        _promptBuilder = new AutoDreamConsolidationPromptBuilder(
            agent.PromptManager,
            new AutoDreamWorkingMemoryScanner(loggerFactory.CreateLogger<AutoDreamWorkingMemoryScanner>()),
            GetCurrentProjectRoot);
    }

    // BackgroundService 主循环

    /// <summary>
    /// 主循环：每小时轮询 + 响应外部 Trigger() 信号。
    /// 两者用 <c>Task.WhenAny</c> 同时等待，先到先执行。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("AutoDream background service started (poll: {Poll}, minHours: {MinHours}h, minSessions: {MinSessions})",
            PollInterval, GetMinHours(), GetMinSessions());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delayTask = Task.Delay(PollInterval, stoppingToken);
                var triggerTask = _triggerChannel.Reader.ReadAsync(stoppingToken).AsTask();

                await Task.WhenAny(delayTask, triggerTask).ConfigureAwait(false);

                // 排空触发通道中可能残留的信号
                while (_triggerChannel.Reader.TryRead(out _)) { }

                await TryConsolidateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AutoDream loop error, will retry after poll interval");
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogDebug("AutoDream background service stopped");
    }

    // 外部触发

    /// <summary>
    /// 外部触发：通知 AutoDream "有新事件，请尽快检查门控"。
    /// 非阻塞、幂等。典型调用方：/memory autodream trigger 手动命令。
    /// </summary>
    public void Trigger() => _triggerChannel.Writer.TryWrite(true);

    // 核心整合逻辑

    /// <summary>
    /// 执行一次完整的门控检查 + 整合尝试。
    /// 由 ExecuteAsync 循环调用。所有门控检查在此完成。
    /// </summary>
    public async Task<bool> TryConsolidateAsync(CancellationToken ct = default)
    {
        if (_isRunning)
        {
            _logger.LogDebug("AutoDream already running, skip");
            return false;
        }

        // 门控 1：启用检查（默认开，ONECODE_AUTODREAM=false 或 ONECODE_REMOTE=true 关闭）
        if (!IsEnabled())
        {
            _logger.LogDebug("AutoDream disabled (ONECODE_AUTODREAM=false or ONECODE_REMOTE=true)");
            return false;
        }

        var minHours = GetMinHours();
        var minSessions = GetMinSessions();

        // 门控 2：时间门控（距上次整合 ≥ minHours）
        var lastConsolidatedAt = GetLastConsolidatedAt();
        var hoursSinceLast = (DateTimeOffset.UtcNow - lastConsolidatedAt).TotalHours;
        if (hoursSinceLast < minHours)
        {
            _logger.LogDebug("AutoDream time gate: {Hours:F1}h since last, need {MinHours}h",
                hoursSinceLast, minHours);
            return false;
        }

        // 门控 3：扫描节流（10 分钟内不重复扫描，持久化跨重启）
        var lastScanAt = GetLastSessionScanAt();
        if (DateTimeOffset.UtcNow - lastScanAt < SessionScanInterval)
        {
            _logger.LogDebug("AutoDream scan throttle: scanned {Ago:F1}min ago",
                (DateTimeOffset.UtcNow - lastScanAt).TotalMinutes);
            return false;
        }
        SetLastSessionScanAt(DateTimeOffset.UtcNow);

        // 门控 4：会话门控（新会话数 ≥ minSessions）
        var newSessionCount = CountNewSessionsSince(lastConsolidatedAt);
        if (newSessionCount < minSessions)
        {
            _logger.LogDebug("AutoDream session gate: {Count} new sessions, need {Min}",
                newSessionCount, minSessions);
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
                "Starting AutoDream consolidation: {Sessions} new sessions, {Hours:F1}h since last",
                newSessionCount, hoursSinceLast);

            var prompt = await _promptBuilder.BuildConsolidationPromptAsync(lastConsolidatedAt, newSessionCount, ct)
                .ConfigureAwait(false);
            var (inputTokens, outputTokens, entriesWritten) = await RunConsolidationAgentAsync(prompt, ct)
                .ConfigureAwait(false);

            // 仅成功后才更新时间戳（失败不更新，下次仍能通过时间门控重试）
            SetLastConsolidatedAt(DateTimeOffset.UtcNow);

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
            _logger.LogError(ex, "AutoDream failed; lastConsolidatedAt not updated, will retry next cycle");
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
        int maxToolCalls)
    {
        var agentOptions = new HarnessAgentOptions
        {
            Name = "autodream",
            // AutoDream deliberately does NOT receive the product harness fragment
            // (system/harness.prompt): it runs a single consolidated prompt (system/autodream-consolidation)
            // on a light model with a fixed tool allowlist, so the interactive security guidance
            // (prompt-injection defense for arbitrary tool output, sensitive-file policy) does not apply
            // to its restricted surface. The empty string is required rather than null: null would let
            // MAF inject its generic DefaultInstructions, widening the instruction surface beyond the
            // dedicated consolidation prompt — the §4.6 migration must not change prompt text as a side
            // effect. OneCodeHarnessDefaults.SuppressFrameworkDefaults keeps the pre-migration
            // behaviour (framework defaults suppressed) explicit.
            HarnessInstructions = OneCodeHarnessDefaults.SuppressFrameworkDefaults,
            ChatOptions = new ChatOptions
            {
                ModelId = modelId,
                MaxOutputTokens = maxOutputTokens,
                Tools = tools.Count > 0 ? tools.ToList() : null,
                ToolMode = tools.Count > 0 ? ChatToolMode.Auto : null,
            },
            MaximumIterationsPerRequest = maxToolCalls,
            // AutoDream 绕过 PipelineProfile 自建 options——按 profile 决定的 Todo/FileMemory
            // 能力决策到不了这条路径，而 Harness 对两者默认挂载。该路径的设计是"轻量模型 +
            // 固定整合提示词 + 只读工具白名单 + 单次无连续会话的 run"，每会话待办清单和工作
            // 记忆写面都与它无关（provider 注入的工具也不受 ChatOptions 白名单约束）。
            // 公共 ApplyProductOptOuts 按契约必须保持 profile 无关（它一关，profile 决策就不可达），
            // 所以在这里显式做出本路径的决策。
            DisableTodoProvider = true,
            DisableFileMemory = true,
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(agentOptions);
        return agentOptions;
    }

    /// <summary>
    /// 使用 MAF HarnessAgent 执行记忆整合。
    /// 模型：fastModel → 主 model（无需单独配置）。
    /// 工具：仅 Read/Glob/Grep（只读，扫描会话目录）。
    /// 输出：增量变更 JSON 数组，由 <see cref="AutoDreamMemoryApplier.ApplyConsolidationChangesAsync"/> 解析后合并写入 MEMORY.md。
    ///
    /// <para><b>为何不设 ResponseFormat（结构化输出）</b>：本 Agent 带 Read/Glob/Grep 工具循环与
    /// 会话，schema 约束会作用于循环内每条助手消息——模型无法在工具调用之间自由叙述，与现有行为
    /// 有实质差异（GoalDecomposer / ClarificationQuestionGenerator 的直连调用没有这个问题，已采用
    /// "结构化请求 + 文本降级"策略）。且输出侧已有 <see cref="AutoDreamOutputSanitizer"/>
    /// 消毒、50 条配额与"记 Warning 返回 0"的完整降级链，schema 增益有限。故维持 prompt-only
    ///（整合提示词内嵌 JSON 数组契约）+ ExtractJsonArray 容错提取。</para>
    /// </summary>
    private async Task<(long InputTokens, long OutputTokens, int EntriesWritten)> RunConsolidationAgentAsync(
        string prompt, CancellationToken ct)
    {
        // 模型：fastModel（轻量任务用）→ 主 model（未配置 fastModel 时回退）
        var modelId = _modelManager.GetFastModel().Id;

        var allTools = _toolCatalog.Tools;
        var allowedSet = new HashSet<string>(AllowedTools, StringComparer.OrdinalIgnoreCase);
        var tools = allTools
            .Where(t => allowedSet.Contains(t.Name))
            .Cast<AITool>()
            .ToList();

        var agentOptions = BuildConsolidationAgentOptions(modelId, tools, MaxOutputTokens, MaxToolCalls);

        var agent = new HarnessAgent(_chatClient, agentOptions, _loggerFactory);

        // Product policy middleware (safety invariants + tool-result budget), mounted through the
        // shared helper so the install order cannot drift from the main agent path.
        var workingDir = GetCurrentProjectRoot() ?? Environment.CurrentDirectory;
        var pipelinedAgent = OneCodeToolMiddleware
            .Apply(agent.AsBuilder(), workingDir, _loggerFactory)
            .Build();

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        var session = await pipelinedAgent.CreateSessionAsync(ct).ConfigureAwait(false);
        var response = await pipelinedAgent.RunAsync(messages, session, new AgentRunOptions(), ct)
            .ConfigureAwait(false);

        var inputTokens = (long)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (long)(response.Usage?.OutputTokenCount ?? 0);

        var outputText = response.Messages
            .OfType<ChatMessage>()
            .LastOrDefault(m => m.Role == ChatRole.Assistant)
            ?.Text ?? string.Empty;

        var entriesWritten = await _memoryApplier.ApplyConsolidationChangesAsync(outputText, ct).ConfigureAwait(false);
        return (inputTokens, outputTokens, entriesWritten);
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

        return _configManager.GetSetting("autodream.enabled", true);
    }

    /// <summary>距上次整合的最小间隔小时数。默认 6。</summary>
    internal int GetMinHours() => _configManager.GetSetting("autodream.minHours", 6);

    /// <summary>触发整合所需的最小新会话数。默认 3。</summary>
    internal int GetMinSessions() => _configManager.GetSetting("autodream.minSessions", 3);

    private static bool GetEnvBool(string name, bool defaultValue) =>
        Environment.GetEnvironmentVariable(name) is { } v
            ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            : defaultValue;

    // 状态持久化

    internal DateTimeOffset GetLastConsolidatedAt() => _stateStore.GetLastConsolidatedAt();

    internal void SetLastConsolidatedAt(DateTimeOffset time) => _stateStore.SetLastConsolidatedAt(time);

    internal DateTimeOffset GetLastSessionScanAt() => _stateStore.GetLastSessionScanAt();

    internal void SetLastSessionScanAt(DateTimeOffset time) => _stateStore.SetLastSessionScanAt(time);

    internal int CountNewSessionsSince(DateTimeOffset since) =>
        _sessionScanner.CountNewSessionsSince(since, GetCurrentProjectRoot());

    internal bool IsSessionForProject(string sessionFile, string projectRoot) =>
        _sessionScanner.IsSessionForProject(sessionFile, projectRoot);

    // 路径工具

    private string GetConfigDir() => _globalConfigDir;

    /// <summary>
    /// AutoDream 运行时状态文件目录 <c>{cwd}/.onecode/memory/</c>——
    /// 存放 lock、last_consolidated_at、last_session_scan_at，与 MEMORY.md 同目录。
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
