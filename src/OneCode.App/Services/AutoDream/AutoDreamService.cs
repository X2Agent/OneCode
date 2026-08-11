using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Middleware;
using OneCode.Infrastructure.Middleware.Invariants;
using System.Text.RegularExpressions;
using System.Threading.Channels;

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
/// <item>距上次整合 ≥ 24 小时</item>
/// <item>新会话数 ≥ 5 个</item>
/// </list>
/// <para>不写入 AGENTS.md——那是人工维护的规范，自动改写会污染它。</para>
/// </remarks>
public sealed partial class AutoDreamService : BackgroundService
{
    // 内部常量（非用户配置，实现细节）

    /// <summary>轮询间隔：1 小时。轮询是唯一的自动触发路径。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    /// <summary>会话扫描节流：10 分钟内不重复扫描（IO 操作，避免频繁磁盘访问）。</summary>
    private static readonly TimeSpan SessionScanInterval = TimeSpan.FromMinutes(10);

    /// <summary>JSON 反序列化选项：camelCase 属性名大小写不敏感，容忍 Agent 输出风格差异。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Agent 单次输出的最大 token 数。</summary>
    private const int MaxOutputTokens = 4096;

    /// <summary>AutoDream Agent 最大工具调用次数（防止无限循环）。</summary>
    private const int MaxToolCalls = 30;

    /// <summary>整合锁文件最大存活时间：超过则视为僵尸锁，可安全抢占。</summary>
    private static readonly TimeSpan StaleLockTimeout = TimeSpan.FromHours(2);

    /// <summary>AutoDream 允许使用的工具白名单（仅只读工具，用于扫描会话目录）。</summary>
    private static readonly string[] AllowedTools = ["Read", "Glob", "Grep"];

    // 状态文件名

    private const string ConsolidationLockFile = "autodream.lock";
    private const string LastConsolidatedAtFile = "last_consolidated_at";
    private const string LastSessionScanAtFile = "last_session_scan_at";

    // 依赖

    private readonly ILogger<AutoDreamService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IChatClient _chatClient;
    private readonly IToolCatalog _toolCatalog;
    private readonly IMemoryEntryStore _entryStore;
    private readonly IModelManager _modelManager;
    private readonly IPromptManager _promptManager;
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

    /// <summary>全局配置目录（~/.onecode/），可被测试 override。</summary>
    private readonly string _globalConfigDir;

    public AutoDreamService(
        ILogger<AutoDreamService> logger,
        ILoggerFactory loggerFactory,
        AutoDreamAgentDependencies agent,
        AutoDreamStorageDependencies storage,
        string? globalConfigDirOverride = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _entryStore = storage.EntryStore;
        _chatClient = agent.ChatClient;
        _toolCatalog = agent.ToolCatalog;
        _modelManager = agent.ModelManager;
        _promptManager = agent.PromptManager;
        _configManager = storage.ConfigManager;
        _wdAccessor = storage.WorkingDirectory;
        _globalConfigDir = globalConfigDirOverride ?? PathsHelper.GetUserConfigDir();
    }

    // BackgroundService 主循环

    /// <summary>
    /// 主循环：每小时轮询 + 响应外部 Trigger() 信号。
    /// 两者用 <see cref="Task.WhenAny"/> 同时等待，先到先执行。
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
        var lockStream = TryAcquireConsolidationLock();
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

            var prompt = await BuildConsolidationPromptAsync(lastConsolidatedAt, newSessionCount, ct)
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
    /// 使用轻量 ChatClientAgent 执行记忆整合。
    /// 模型：fastModel → 主 model（无需单独配置）。
    /// 工具：仅 Read/Glob/Grep（只读，扫描会话目录）。
    /// 输出：增量变更 JSON 数组，由 <see cref="ApplyConsolidationChangesAsync"/> 解析后合并写入 MEMORY.md。
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

        var agentOptions = new ChatClientAgentOptions
        {
            Name = "autodream",
            ChatOptions = new ChatOptions
            {
                ModelId = modelId,
                MaxOutputTokens = MaxOutputTokens,
                Tools = tools.Count > 0 ? tools : null,
                ToolMode = tools.Count > 0 ? ChatToolMode.Auto : null,
            },
        };

        var agent = new ChatClientAgent(_chatClient, agentOptions, _loggerFactory);

        // 轻量中间件管道：SafetyInvariant（Layer 0 安全不变量）→ ToolResultBudget
        // SafetyInvariant 必须在最外层：即使 AutoDream 只使用 Read/Glob/Grep（只读工具），
        // Read 仍需受 SensitiveReadPathSequences 保护（id_rsa/.env/.aws/credentials 等），
        // 防止 LLM 读取敏感文件并将内容写入 MEMORY.md 造成凭证泄露。
        var workingDir = GetCurrentProjectRoot() ?? Environment.CurrentDirectory;
        var invariants = new ISafetyInvariant[]
        {
            new FileSystemInvariant(workingDir),
            new BashCommandInvariant(),
            new ResourceInvariant(),
        };
        var builder = agent.AsBuilder();
        builder = builder.Use(SafetyInvariantMiddleware.Create(
            invariants, _loggerFactory.CreateLogger("SafetyInvariantMiddleware")));
        // MaxToolCalls safety net — prevents infinite tool loops if the LLM doesn't converge
        var toolCallCount = 0;
        builder = builder.Use((_, ctx, next, ct) =>
        {
            if (Interlocked.Increment(ref toolCallCount) > MaxToolCalls)
            {
                ctx.Terminate = true;
                _logger.LogWarning("AutoDream: MaxToolCalls limit ({Limit}) reached, terminating", MaxToolCalls);
                return new ValueTask<object?>(
                    (object?)ToolResult.Error($"AutoDream: Maximum tool call limit ({MaxToolCalls}) reached."));
            }
            return next(ctx, ct);
        });
        builder = builder.Use(new ToolExecutionBudgetMiddleware(
            logger: _loggerFactory.CreateLogger<ToolExecutionBudgetMiddleware>()).CreateDelegate());
        var pipelinedAgent = builder.Build();

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

        var entriesWritten = await ApplyConsolidationChangesAsync(outputText, ct).ConfigureAwait(false);
        return (inputTokens, outputTokens, entriesWritten);
    }

    /// <summary>
    /// 解析 Agent 输出的增量变更 JSON 数组，合并写入对应的 MEMORY.md 文件。
    /// 容错：容忍 Agent 在 JSON 前后添加 markdown 围栏或额外说明文本。
    /// 安全：Agent 输出为不可信内容，必须经过 <see cref="SanitizeKey"/> / <see cref="SanitizeValue"/>
    /// 清洗，防止 MEMORY.md 结构注入（如 <c>## </c> 开头的行会被 <see cref="MemoryEntryStore.ParseEntries"/>
    /// 误识别为新的 entry header，导致条目边界错乱、内容串入相邻条目）。
    /// </summary>
    private async Task<int> ApplyConsolidationChangesAsync(string outputText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputText))
            return 0;

        var json = ExtractJsonArray(outputText);
        if (json is null)
        {
            _logger.LogWarning("AutoDream: failed to extract JSON array from agent output");
            return 0;
        }

        List<ConsolidationChange>? changes;
        try
        {
            changes = JsonSerializer.Deserialize<List<ConsolidationChange>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "AutoDream: failed to parse consolidation changes JSON");
            return 0;
        }

        if (changes is null || changes.Count == 0)
            return 0;

        // 配额限制：防止 Agent 输出过多变更导致 MEMORY.md 膨胀或写入风暴。
        // 50 条覆盖典型整合场景（事实/约定/教训/纠正），超出部分丢弃并记录。
        if (changes.Count > MaxChangesPerConsolidation)
        {
            _logger.LogWarning(
                "AutoDream: agent output {Count} changes exceeds limit {Max}, truncating",
                changes.Count, MaxChangesPerConsolidation);
            changes = changes.Take(MaxChangesPerConsolidation).ToList();
        }

        var now = DateTimeOffset.UtcNow;
        var projectRoot = GetCurrentProjectRoot();

        // Group by scope to batch-write each MEMORY.md
        var userEntries = new List<MemoryEntry>();
        var projectEntries = new List<MemoryEntry>();
        var userDeleteKeys = new List<string>();
        var projectDeleteKeys = new List<string>();
        var written = 0;
        var skipped = 0;

        foreach (var change in changes)
        {
            // 清洗 Key：strip 换行/前导#防止 entry header 注入，限制长度
            var key = SanitizeKey(change.Key);
            if (string.IsNullOrEmpty(key))
            {
                skipped++;
                continue;
            }

            // 显式 scope 校验：仅接受 "user"/"project"，其他值跳过。
            if (!string.Equals(change.Scope, "user", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(change.Scope, "project", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("AutoDream: change with key '{Key}' has invalid scope '{Scope}', skipping",
                    key, change.Scope);
                skipped++;
                continue;
            }

            var scope = string.Equals(change.Scope, "user", StringComparison.OrdinalIgnoreCase)
                ? MemoryScope.User
                : MemoryScope.Project;

            if (string.Equals(change.Action, "delete", StringComparison.OrdinalIgnoreCase))
            {
                if (scope == MemoryScope.User)
                    userDeleteKeys.Add(key);
                else
                    projectDeleteKeys.Add(key);
                written++;
                continue;
            }

            // 非 delete action 仅接受 upsert（默认）。其他 action 值跳过。
            if (!string.IsNullOrEmpty(change.Action)
                && !string.Equals(change.Action, "upsert", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("AutoDream: change with key '{Key}' has unknown action '{Action}', skipping",
                    key, change.Action);
                skipped++;
                continue;
            }

            // 清洗 Value：strip 行首 ## 防止 entry header 注入，限制长度
            var value = SanitizeValue(change.Value);
            if (string.IsNullOrEmpty(value))
            {
                skipped++;
                continue;
            }

            var category = MemoryEntry.DeriveCategory(key);
            TimeSpan? ttl = change.TtlHours is > 0
                ? TimeSpan.FromHours(change.TtlHours.Value)
                : null;

            var entry = new MemoryEntry
            {
                Key = key,
                Value = value,
                Source = "autodream",
                Category = category,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = ttl.HasValue ? now.Add(ttl.Value) : null,
            };

            if (scope == MemoryScope.User)
                userEntries.Add(entry);
            else
                projectEntries.Add(entry);
            written++;
        }

        if (skipped > 0)
            _logger.LogWarning("AutoDream: skipped {Count} invalid changes", skipped);

        if (userEntries.Count > 0)
        {
            await _entryStore.UpsertAsync(MemoryScope.User, userEntries, ct).ConfigureAwait(false);
        }
        foreach (var key in userDeleteKeys)
        {
            await _entryStore.RemoveAsync(MemoryScope.User, key, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            if (projectEntries.Count > 0)
            {
                await _entryStore.UpsertAsync(MemoryScope.Project, projectEntries, ct).ConfigureAwait(false);
            }
            foreach (var key in projectDeleteKeys)
            {
                await _entryStore.RemoveAsync(MemoryScope.Project, key, ct).ConfigureAwait(false);
            }

            // Prune expired entries after consolidation
            await _entryStore.PruneAsync(MemoryScope.Project, ct).ConfigureAwait(false);
        }

        // Prune user-scope expired entries
        await _entryStore.PruneAsync(MemoryScope.User, ct).ConfigureAwait(false);

        return written;
    }

    // 不可信输出清洗

    /// <summary>单次整合允许的最大变更条数（防止 Agent 输出风暴）。</summary>
    private const int MaxChangesPerConsolidation = 50;

    /// <summary>Key 最大长度（{category}:{short-id} 格式，100 字符足够描述性 ID）。</summary>
    private const int MaxKeyLength = 100;

    /// <summary>Value 最大长度（10K 字符覆盖多段事实/约定，超出截断）。</summary>
    private const int MaxValueLength = 10_000;

    /// <summary>
    /// 清洗 Agent 输出的 Key，防止 MEMORY.md 结构注入。
    /// - 换行 → 空格：Key 必须单行（<see cref="MemoryEntryStore"/> 序列化为 <c>## {Key}</c> 单行 header）
    /// - 前导 <c>#</c> → 移除：防止 <c>## fact:foo</c> 被解析器当作已存在的 header 而错位
    /// - 长度限制：防止超长 Key 撑爆 MEMORY.md 单行
    /// </summary>
    private static string SanitizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        // 统一换行为空格，确保单行
        var single = key.ReplaceLineEndings(" ").Trim();

        // 移除所有前导 '#'（防止 ## 注入）
        single = single.TrimStart('#').TrimStart();

        // 长度截断
        return single.Length > MaxKeyLength ? single[..MaxKeyLength] : single;
    }

    /// <summary>
    /// 清洗 Agent 输出的 Value，防止 MEMORY.md 结构注入。
    /// - 移除行首 <c>## </c>：这些行会被 <see cref="MemoryEntryStore.EntryHeaderRegex"/>
    ///   误识别为新的 entry header，导致当前 Value 被截断、后续行被解析为独立（无元数据）条目。
    ///   处理方式：将行首 <c>## </c> 替换为 <c># # </c>（保留可读性，破坏 header 模式）。
    /// - 长度限制：防止超长 Value 撑爆 MEMORY.md
    /// </summary>
    private static string SanitizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.Length > MaxValueLength)
            trimmed = trimmed[..MaxValueLength];

        // 防止行首 "## " 注入：逐行检查，将 "## " 替换为 "# # "
        // (RegexOptions.Multiline 使 ^ 匹配每行行首)
        return EntryHeaderInjectionRegex().Replace(trimmed, "# # ");
    }

    [GeneratedRegex(@"^##\s+", RegexOptions.Multiline)]
    private static partial Regex EntryHeaderInjectionRegex();

    private static string? ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        if (start < 0) return null;
        var end = text.LastIndexOf(']');
        if (end <= start) return null;
        return text.Substring(start, end - start + 1);
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

    internal DateTimeOffset GetLastConsolidatedAt() => ReadStateFile(LastConsolidatedAtFile);

    internal void SetLastConsolidatedAt(DateTimeOffset time) =>
        WriteStateFile(LastConsolidatedAtFile, time.ToString("O", CultureInfo.InvariantCulture));

    internal DateTimeOffset GetLastSessionScanAt() => ReadStateFile(LastSessionScanAtFile);

    internal void SetLastSessionScanAt(DateTimeOffset time) =>
        WriteStateFile(LastSessionScanAtFile, time.ToString("O", CultureInfo.InvariantCulture));

    private DateTimeOffset ReadStateFile(string fileName)
    {
        var filePath = GetStateFilePath(fileName);
        if (!File.Exists(filePath)) return DateTimeOffset.MinValue;
        try
        {
            var text = File.ReadAllText(filePath).Trim();
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read state from {FilePath}", filePath);
            return DateTimeOffset.MinValue;
        }
    }

    private void WriteStateFile(string fileName, string content)
    {
        var filePath = GetStateFilePath(fileName);
        try
        {
            EnsureConfigDir();
            File.WriteAllText(filePath, content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write state file {FilePath}", filePath);
        }
    }

    internal int CountNewSessionsSince(DateTimeOffset since)
    {
        var sessionsDir = Path.Combine(GetConfigDir(), "sessions");
        if (!Directory.Exists(sessionsDir)) return 0;

        var projectRoot = GetCurrentProjectRoot();
        if (projectRoot is null)
        {
            _logger.LogDebug("AutoDream: workingDirectory not available, skipping session count");
            return 0;
        }

        try
        {
            var files = Directory.GetFiles(sessionsDir, "*.jsonl");
            var count = 0;
            foreach (var file in files)
            {
                if (File.GetLastWriteTimeUtc(file) <= since.UtcDateTime)
                    continue;

                if (IsSessionForProject(file, projectRoot))
                    count++;
            }
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to count new sessions in {SessionsDir}", sessionsDir);
            return 0;
        }
    }

    /// <summary>
    /// 检查会话文件是否属于当前项目：读取 JSONL 首行的 <c>working_directory</c> 字段并比较。
    /// </summary>
    internal bool IsSessionForProject(string sessionFile, string projectRoot)
    {
        try
        {
            var firstLine = ReadFirstLine(sessionFile);
            if (string.IsNullOrEmpty(firstLine))
                return false;

            using var doc = JsonDocument.Parse(firstLine);
            if (!doc.RootElement.TryGetProperty("working_directory", out var wdElem))
                return false;

            var wd = wdElem.GetString();
            if (string.IsNullOrWhiteSpace(wd))
                return false;

            return string.Equals(
                PathsHelper.NormalizePath(wd),
                PathsHelper.NormalizePath(projectRoot),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read working_directory from {File}", sessionFile);
            return false;
        }
    }

    private static string? ReadFirstLine(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadLine();
        }
        catch
        {
            return null;
        }
    }

    // 跨进程文件锁（原子获取）

    /// <summary>
    /// 原子地获取跨进程整合锁。
    /// 使用 FileStream + FileShare.None，多进程同时调用时仅一个成功。
    /// 僵尸锁（超 2 小时）可安全抢占。返回的 FileStream 持有锁，Dispose 即释放。
    /// </summary>
    private FileStream? TryAcquireConsolidationLock()
    {
        var lockPath = GetStateFilePath(ConsolidationLockFile);
        EnsureConfigDir();

        try
        {
            var stream = new FileStream(lockPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.Flush();
            }
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }
        catch (IOException)
        {
            // 文件被其他进程独占——检查是否为僵尸锁
            try
            {
                var content = File.ReadAllText(lockPath).Trim();
                if (DateTimeOffset.TryParse(content, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var lockTime))
                {
                    if (DateTimeOffset.UtcNow - lockTime > StaleLockTimeout)
                    {
                        _logger.LogWarning("AutoDream stale lock (age {Age:F1}h), forcing takeover",
                            (DateTimeOffset.UtcNow - lockTime).TotalHours);
                        File.Delete(lockPath);
                        return TryAcquireConsolidationLock();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect stale lock at {LockPath}", lockPath);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire consolidation lock at {LockPath}", lockPath);
            return null;
        }
    }

    // Prompt 构建

    private async Task<string> BuildConsolidationPromptAsync(DateTimeOffset since, int sessionCount, CancellationToken ct)
    {
        var projectRoot = GetCurrentProjectRoot() ?? "(unknown)";
        var variables = new Dictionary<string, string>
        {
            ["since"] = since.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["session_count"] = sessionCount.ToString(CultureInfo.InvariantCulture),
            ["project_root"] = projectRoot,
        };
        return await _promptManager.RenderPromptAsync("system/autodream-consolidation", variables, ct)
            .ConfigureAwait(false);
    }

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

    internal string GetStateFilePath(string fileName) =>
        Path.Combine(GetProjectStateDir(), fileName);

    private void EnsureConfigDir()
    {
        var dir = GetProjectStateDir();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}

/// <summary>Agent 输出的单条增量变更（待合并写入 MEMORY.md）。</summary>
internal sealed record ConsolidationChange(
    string Action,
    string Scope,
    string Key,
    string? Value,
    int? TtlHours);
