# ADR 0005: Hook 模块架构设计

**状态**: Accepted
**日期**: 2026-07-18
**关联**: [hooks.md](../hooks.md)、[settings.md §Hooks 配置](../settings.md#hooks-配置)、[ADR 0001](./0001-permission-vs-toolapproval-vs-filter.md)

## 语境

OneCode 需要一个生命周期钩子（Hook）子系统，让用户在 Agent 运行的关键节点注入自定义逻辑，支持 CI/CD 集成、安全策略、自动化通知、审计日志等场景。

早期版本的 Hook 系统存在以下问题：

1. **事件过多且语义模糊**：曾包含 `PreAgentRun` / `PostAgentRun` / `SubagentStop` / `PreToolUse` 等 20+ 种事件，部分事件无外部脚本消费价值，维护成本高
2. **执行器类型膨胀**：曾包含 `Command` / `Prompt` / `Agent` / `Async` 等多种执行器，其中 `PromptHookExecutor`（向 LLM 注入提示）与 `AgentHookExecutor`（启动子 Agent）语义与工具调用重叠，违反单一职责
3. **异步 Hook 复杂度高**：`IAsyncHookRegistry` / `AsyncHookRegistryCleanupService` 维护跨进程的异步 hook 队列，增加系统复杂度但实际使用场景极少
4. **条件求值器抽象过度**：`IHookConditionEvaluator` / `SimpleHookConditionEvaluator` 试图支持运行时条件表达式，但与 matcher 机制功能重叠
5. **配置散落多处**：Hook 定义曾嵌在 `settings.json` 的 `hooks` 子属性中，与策略开关混在一起，难以独立维护与版本管理
6. **接口位于 App 层**：`IHookExecutionService` 曾位于 App 层，导致 Infrastructure 层的 `AgentPipelineBuilder` 需要反向依赖 App 层
7. **广播器与分发器冗余**：`IHookEventBroadcaster` / `HookEventDispatcher` / `SessionHookStore` 多层抽象，职责重叠

本 ADR 记录重构后的架构决策与实现细节。

## 决策

### 1. 事件收敛到 10 种真实有外部消费价值的事件

**决策**：`HookEvent` 枚举仅保留 10 种事件，每种事件都有明确的外部脚本消费场景。

```csharp
public enum HookEvent
{
    PreToolUse,        // 工具执行前（可阻断）
    PostToolUse,       // 工具执行后
    Notification,      // 通知发送时
    UserPromptSubmit,  // 用户提交 prompt 后
    SessionStart,      // 会话启动
    Stop,              // AI 响应结束前（可阻断）
    StopFailure,       // API 错误导致 turn 结束
    PreCompact,        // 对话压缩前
    PostCompact,       // 对话压缩后
    SessionEnd,        // 会话结束
}
```

**理由**：
- 移除 `PreAgentRun` / `PostAgentRun`：与 `SessionStart` / `Stop` 语义重叠，外部脚本无法区分
- 移除 `SubagentStop`：子 Agent 停止事件无外部消费价值，内部通过 `OrchestrationEventSink` 流式处理
- 移除 `PreToolApproval`：审批逻辑由 `PermissionMiddleware` 统一处理（见 [ADR 0001](./0001-permission-vs-toolapproval-vs-filter.md)）
- 保留 `StopFailure`：API 错误（rate_limit / auth_failed / billing 等）需要外部告警

**事件元数据**：`HookEventMetadataRegistry` 为每种事件声明 matcher 字段名与可选值，用于 UI 展示和文档生成：

```csharp
[HookEvent.PreToolUse] = new(
    "工具执行前",
    "在工具调用执行前触发，可通过 exit code 2 阻止工具执行",
    new MatcherMetadata("tool_name", ["Bash", "Write", "Read", "Grep", "Glob", "WebFetch", "WebSearch", "Task"]));
```

### 2. 执行器收敛到 3 种真实使用的类型

**决策**：`HookType` 枚举仅保留 3 种执行器，移除 `Prompt` / `Agent` / `Async` 类型。

```csharp
public enum HookType
{
    Command,       // CliWrap 外部进程，stdin 传 JSON payload，exit code 2 可阻断
    Notification,  // Provider 策略分发到飞书/企业微信等外部消息系统
    Http,          // 通用 HTTP 调用（GET/POST/PUT/DELETE），自定义 URL/Method/Headers/Body
}
```

**理由**：
- **移除 `PromptHookExecutor`**：向 LLM 注入提示应由 `AIContextProvider` 统一处理，不应通过 Hook 机制旁路。Hook 是"外部副作用"语义，不应污染 LLM 上下文
- **移除 `AgentHookExecutor`**：启动子 Agent 应由 `ForkedAgentRunner` / `GoalAgentRunner` 显式调度，Hook 触发子 Agent 会导致执行图不可预测
- **移除 `AsyncHookExecutor` 与 `IAsyncHookRegistry`**：异步 hook 队列的实际使用场景极少，且跨进程队列增加系统复杂度。所有 hook 统一为同步串行执行（带超时），结果聚合后一次性返回

**Http 与 Notification 的边界**：

| 维度 | Http | Notification |
|------|------|--------------|
| 目标 | 通用 HTTP 调用（自定义 URL/Method/Headers/Body） | 消息推送业务场景（飞书/企微等固定渠道格式） |
| 灵活性 | 高（完全自定义） | 低（渠道特定格式） |
| 签名 | 自行通过 headers 实现 | Provider 内置 HMAC 签名 |
| 响应解析 | 仅判断 HTTP 状态码 | 解析渠道特定响应字段（code/errcode 等） |

两者并存的理由：Http 面向"通用 HTTP 调用"（CI/CD 触发、审计回调），Notification 面向"消息推送业务"（飞书/企微通知）。强行合并会导致 Notification 丧失渠道特定的签名/响应解析能力，或 Http 被迫承载不必要的渠道抽象。

### 3. 执行器策略模式 + Provider 策略模式

**决策**：通过 `IHookExecutor` 接口按 `HookType` 分发，通过 `INotificationProvider` 接口按 `Provider` 名称分发。

```csharp
public interface IHookExecutor
{
    HookType Type { get; }
    Task<HookResult?> ExecuteAsync(HookPayload payload, HookConfig config, CancellationToken ct);
}

public interface INotificationProvider
{
    string Name { get; }
    Task<NotificationSendResult> SendAsync(NotificationMessage message, string webhookUrl, string? secret, CancellationToken ct);
}
```

**DI 注册模式**：

```csharp
// 执行器：按 HookType Keyed Services 分发
services.AddKeyedSingleton<IHookExecutor, CommandHookExecutor>(HookType.Command);
services.AddKeyedSingleton<IHookExecutor, NotificationHookExecutor>(HookType.Notification);
services.AddKeyedSingleton<IHookExecutor, HttpHookExecutor>(HookType.Http);

// 通知渠道：按 Provider Name 分发（IEnumerable<INotificationProvider> 注入）
services.AddSingleton<INotificationProvider, FeishuNotificationProvider>();
services.AddSingleton<INotificationProvider, WeChatWorkNotificationProvider>();
```

**分发逻辑**：

```csharp
// HookExecutionService 构造函数：按键控服务显式注入三类执行器
public HookExecutionService(
    HookRegistry hookRegistry,
    [FromKeyedServices(HookType.Command)] IHookExecutor commandExecutor,
    [FromKeyedServices(HookType.Notification)] IHookExecutor notificationExecutor,
    [FromKeyedServices(HookType.Http)] IHookExecutor httpExecutor,
    ...)

// NotificationHookExecutor 构造函数内：
_providers = providers?.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
```

**理由**：
- 新增执行器类型只需实现 `IHookExecutor` + Keyed DI 注册，`HookExecutionService` 通过 `HookType` 键控注入自动分发
- 新增通知渠道只需实现 `INotificationProvider` + DI 注册，`NotificationHookExecutor` 通过 `Name` 字典查找
- Keyed Services（.NET 8+）让"一种 HookType 一个执行器"的多实现注册无需 `IEnumerable<IHookExecutor>` + GroupBy 去重

### 4. Registry 二维索引 + Glob 匹配

**决策**：`HookRegistry` 按 `(HookEvent, MatcherPattern)` 二维索引，`GlobHookMatcher` 实现 Glob 风格通配符匹配。

```csharp
public sealed class HookRegistry
{
    private readonly Dictionary<HookEvent, List<MatcherGroup>> _matcherIndex = new();

    public void Register(HookRegistration hook)
    {
        // 按 Event 查找或创建 MatcherGroup
        // 按 MatcherPattern（大小写不敏感）查找或创建分组
        // 将 hook 追加到分组的 Hooks 列表
    }

    public IReadOnlyList<HookRegistration> GetMatchesForEvent(HookEvent @event, string? matcherValue)
    {
        // O(1) 事件查找 → 遍历该事件下的 MatcherGroup
        // 对每个 group，用 GlobHookMatcher.Matches(pattern, matcherValue) 过滤
    }
}
```

**Glob 匹配规则**：

| Pattern | 语义 |
|---------|------|
| `""` 或 `"*"` | 匹配所有（wildcard） |
| `"Bash"` | 精确匹配（大小写不敏感） |
| `"Bash*"` | 前缀通配（fast-path：单 `*` 拆分为 prefix + suffix） |
| `"*Tool"` | 后缀通配 |
| `"Write\|Read"` | 管道分隔多值（匹配任意一个，自动 trim 空白） |
| `"Ba*sh"` | 多 `*` 通配（回退到 Regex） |

**理由**：
- 二维索引避免 `GetAll()` 的 O(n) 全量扫描，事件查找 O(1)，matcher 过滤 O(k)（k 为该事件下的分组数）
- Glob 风格比正则表达式更易写易读，符合用户对 shell 通配符的直觉
- 单 `*` fast-path 优化覆盖 90% 的实际用法（如 `Bash*` / `*Tool`），避免 Regex 编译开销
- 管道分隔多值支持 `Write|Read` 这类常见需求，无需写多个 hook

### 5. 串行执行 + 结果聚合

**决策**：同一事件下匹配的 hook 按 `priority` 升序串行执行，结果通过 `HookResultAggregator` 聚合为单个 `AggregatedHookResult`。

```csharp
// HookExecutionService.FireAsync 流程：
// 1. 策略前置检查（工作区不可信 → 返回空结果）
// 2. _hookRegistry.GetMatchesForEvent(payload.Event, actualMatcherValue)
// 3. hooks.Sort((a, b) => a.Priority.CompareTo(b.Priority))
// 4. 串行执行：foreach (hook in hooks) { results.Add(await ExecuteSingleHookAsync(hook, payload, ct)); }
// 5. HookResultAggregator.Aggregate(results)
// 6. 清理 once hook（Unregister）
```

**聚合策略**：

| 字段类型 | 聚合策略 | 理由 |
|---------|---------|------|
| 布尔字段（`PreventContinuation`） | OR | 任一 hook 请求阻止即应生效 |
| 列表字段（`BlockingErrors` / `AdditionalContexts`） | 累加 | 所有阻断错误和额外上下文都应保留 |
| 字符串字段（`Message` / `SystemMessage`） | last-write-wins | 后执行的 hook 可覆盖前者的消息 |
| `UpdatedInput` | last-write-wins | 后执行的 hook 可修改工具输入 |

**理由**：
- 串行执行保证 hook 之间的顺序依赖（如审计 hook 需在通知 hook 之前执行）
- 串行避免并发执行导致的资源竞争（如多个 hook 同时写同一文件）
- 聚合策略保证多个 hook 的结果可合并为单一决策，调用方无需感知 hook 数量
- 异常隔离：单个 hook 执行器异常被吞掉记 Warning，不影响其他 hook 执行

### 6. 配置独立文件 + 新旧格式兼容

**决策**：Hook 定义从 `settings.json` 中剥离，存放在独立的 `hooks.json` 文件中；策略开关保留在 `settings.json`。

**文件布局**：

| 文件 | 作用域 | 基础优先级 | 内容 |
|------|--------|-----------|------|
| `~/.onecode/hooks.json` | 用户级（全局） | 100 | Hook 定义（事件 → matcher 分组 → hook 配置） |
| `<cwd>/.onecode/hooks.json` | 项目级 | 200 | 同上 |

**理由**：
- Hook 定义和执行策略统一由 Hook 子系统管理，不进入通用 `settings.json` 配置模型
- `hooks.json` 可被 Git 跟踪（项目级）或单独备份（用户级），不与敏感的 `settings.json`（可能含 API key）混在一起
- 工作区是否允许执行 Hook 由 `HookPolicyService` 基于统一配置快照中的 `trustedDirectories` 判断

**格式说明**：`HookSettingsLoader.ParseEventHooks` 仅解析 matcher-group 格式（数组元素即 `HookMatcherGroup`，元素内含 `matcher` 与 `hooks`）；无 `hooks` 字段的元素会被跳过。

```csharp
var group = JsonSerializer.Deserialize<HookMatcherGroup>(item.GetRawText(), JsonOptions);
if (group is not null && group.Hooks.Count > 0)
    groups.Add(group);
```

**理由**：旧格式平铺无法表达"同一 matcher 下多个 hook"的分组语义，新格式通过 `HookMatcherGroup` 显式分组。早期版本的平铺兼容分支已移除，迁移到分组格式即可。

### 7. 优先级范围约定

**决策**：`Priority` 字段按范围划分来源，`HookConfigBootstrapper.BootstrapFromDirectory` 根据配置目录推导基础优先级。

| 优先级范围 | 来源 | 说明 |
|-----------|------|------|
| `0-99` | Managed（系统内置） | 系统级 hook |
| `100-199` | User（用户级） | `~/.onecode/hooks.json` 加载，`basePriority = 100` |
| `200-299` | Project（项目级） | `<cwd>/.onecode/hooks.json` 加载，`basePriority = 200` |
| `300+` | Plugin（插件） | 插件运行时注册 |

**理由**：
- 数值范围约定让 `/hooks list` 可按 source 分组展示，用户一目了然
- 用户可在 `hooks.json` 中通过 `priority` 字段显式覆盖默认值，实现跨源排序

### 8. 接口下沉到 Core 层

**决策**：`IHookExecutionService` 接口位于 `OneCode.Core/Hooks/`，实现 `HookExecutionService` 位于 `OneCode.App/Services/Hooks/`。

```csharp
// OneCode.Core/Hooks/IHookExecutionService.cs
public interface IHookExecutionService
{
    Task<AggregatedHookResult> FireAsync(
        HookPayload payload,
        string? actualMatcherValue = null,
        CancellationToken ct = default);
}
```

**理由**：
- Infrastructure 层的 `AgentPipelineBuilder` 需要通过此接口触发 `PreToolUse` / `PostToolUse` hook（通过 `HookMiddleware`）
- 若接口位于 App 层，Infrastructure 层需反向依赖 App 层，违反分层架构
- 接口下沉到 Core 层后，Infrastructure 层仅依赖 Core 层契约，实现由 App 层注入

**`HookMiddleware` 集成**（`OneCode.Infrastructure/Middleware/HookMiddleware.cs`）：

```csharp
// PreToolUse hook（fail-closed：异常时拒绝工具执行）
if (options.HookExecutionService is not null && ctx.Function is not null)
{
    var prePayload = new HookPayload { Event = HookEvent.PreToolUse, ToolName = ctx.Function.Name, ... };
    AggregatedHookResult? hookResult;
    try
    {
        hookResult = await options.HookExecutionService.FireAsync(
            prePayload, actualMatcherValue: ctx.Function.Name, ct: ct);
    }
    catch (OperationCanceledException) { throw; }  // OCE 透传
    catch (Exception ex)
    {
        // Pre-hook 异常 fail-closed（见 §10）：限制为单次工具调用失败，保留批次完整性
        return ToolResult.Error($"Tool '{ctx.Function.Name}' blocked: pre-hook execution failed: {ex.Message}");
    }

    if (hookResult?.BlockingErrors is { Count: > 0 })
    {
        return ToolResult.Error($"Tool '{ctx.Function.Name}' blocked by hook: {hookResult.BlockingErrors[0].Error}");
    }
}

var result = await next(ctx, ct);

// PostToolUse hook（fail-soft：异常仅记 Warning，保留工具结果）
if (options.HookExecutionService is not null && ctx.Function is not null)
{
    var postPayload = new HookPayload { Event = HookEvent.PostToolUse, ToolName = ctx.Function.Name, ... };
    try { await options.HookExecutionService.FireAsync(postPayload, actualMatcherValue: ctx.Function.Name, ct: ct); }
    catch (OperationCanceledException) { throw; }  // OCE 透传
    catch (Exception ex) { logger.LogWarning(ex, "PostToolUse hook threw; tool result preserved"); }
}

return result;
```

### 9. 安全策略：工作区信任

**决策**：`HookExecutionService.FireAsync` 在执行前做工作区信任前置检查。

```csharp
// HookExecutionService.FireAsync 入口：
if (!_policyService.IsCurrentWorkspaceTrusted())
    return new AggregatedHookResult();  // 工作区不可信
```

**工作区信任检查**：

```csharp
public bool IsCurrentWorkspaceTrusted()
{
    var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
    var trusted = _configManager.Current.Effective.TrustedDirectories;

    foreach (var trustedDir in trusted)
    {
        // 精确匹配 或 子目录继承
        if (normalizedCwd.Equals(normalizedTrusted, PathComparison)
            || normalizedCwd.StartsWith(normalizedTrusted + Path.DirectorySeparatorChar, PathComparison))
            return true;
    }
    return false;
}
```

**理由**：
- **工作区信任**是安全基础：恶意仓库可通过 `<cwd>/.onecode/hooks.json` 注入任意命令（如 `rm -rf ~`），必须限制仅在受信任目录中触发
- 策略前置检查（在 matcher 过滤之前）避免不必要的 Registry 查询开销
- 早期设计的 `disableAll` / `allowManagedOnly` 等策略开关未实现，当前工作区信任是唯一的策略门控

### 10. 异常处理策略：Pre fail-closed / Post fail-soft

**决策**：`HookMiddleware` 对 Pre-hook 和 Post-hook 采用不同的异常处理策略。

| 场景 | 策略 | 实现 |
|------|------|------|
| Pre-hook（PreToolUse）异常 | **fail-closed** | 异常转为 `ToolResult.Error` 使该次工具调用失败（不调用 `ctx.Terminate`，保留批次完整性） |
| Post-hook（PostToolUse）异常 | **fail-soft** | 仅记 Warning 日志，保留原工具结果返回 |
| `OperationCanceledException` | 透传 | 显式 `catch (OperationCanceledException) { throw; }` 保留取消信号 |
| 单个 hook 执行器异常 | 隔离 | `ExecuteSingleHookAsync` try-catch 吞掉异常记 Warning，其他 hook 继续执行 |

**理由**：
- **Pre-hook fail-closed**：Pre-hook 的核心语义是"安全检查"（如阻断危险命令）。若 Pre-hook 异常被吞掉，可能导致危险操作被执行。fail-closed 确保异常时拒绝工具执行，符合安全原则
- **Post-hook fail-soft**：Post-hook 是"通知/审计"语义，失败不应丢弃已成功的工具结果。若 Post-hook 异常冒泡，会导致用户看到工具失败但实际工具已执行，造成状态混乱
- **OCE 透传**：取消信号必须传播到调用方，不能被异常处理吞掉。`catch (OperationCanceledException) { throw; }` 显式排除 OCE
- **执行器隔离**：单个 hook 异常不应影响其他 hook。`ExecuteSingleHookAsync` 返回 null（被聚合器跳过），其他 hook 继续执行

### 11. Webhook 通知基类复用

**决策**：飞书 / 企业微信 / 钉钉等 Webhook 类通知渠道共享 `WebhookNotificationProviderBase` 基类，子类只需重写 4 个抽象成员。

```csharp
public abstract class WebhookNotificationProviderBase(HttpClient httpClient, ILogger? logger = null) : INotificationProvider
{
    // 共享流程：
    // 1. BuildPayload(message) → 渠道特定 payload 对象
    // 2. 若有 secret → BuildSignedUrl(webhookUrl, secret) 附加 timestamp + sign 查询参数
    // 3. HttpClient.PostAsJsonAsync(url, payload)
    // 4. ParseResponse(body) → 渠道特定响应字段判断成功/失败

    protected abstract string CodeFieldName { get; }      // "code" / "errcode"
    protected abstract string MsgFieldName { get; }        // "msg" / "errmsg"
    protected abstract string ProviderDisplayName { get; } // "Feishu" / "WeChatWork"
    protected abstract object BuildPayload(NotificationMessage message);
    protected abstract string ComputeSign(string timestamp, string secret);

    protected static string ComputeHmacSha256Base64(byte[] key, byte[] message)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(message));
    }
}
```

**子类实现示例**（飞书）：

```csharp
public sealed class FeishuNotificationProvider(HttpClient httpClient, ILogger<FeishuNotificationProvider>? logger = null)
    : WebhookNotificationProviderBase(httpClient, logger)
{
    public override string Name => "feishu";
    protected override string CodeFieldName => "code";
    protected override string MsgFieldName => "msg";
    protected override string ProviderDisplayName => "Feishu";

    protected override object BuildPayload(NotificationMessage message) => new
    {
        msg_type = "text",
        content = new { text = message.Text },
    };

    // 飞书签名：HMAC-SHA256(key = timestamp + "\n" + secret, message = "")
    protected override string ComputeSign(string timestamp, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(timestamp + "\n" + secret);
        return ComputeHmacSha256Base64(keyBytes, Array.Empty<byte>());
    }
}
```

**理由**：
- 飞书 / 企业微信 / 钉钉等渠道的 Webhook 流程高度一致（POST JSON + 签名 URL + 响应解析），仅在 payload 字段名、签名算法、响应字段名上有差异
- 基类统一实现 HTTP POST、签名 URL 构造、异常处理、状态码处理、响应解析，子类只需声明差异点
- `ComputeHmacSha256Base64` 辅助方法避免 HMAC 样板代码重复
- 新增渠道（如钉钉）只需实现 5 个抽象成员，无需重复 HTTP/签名/解析逻辑

### 12. 模板插值统一语义

**决策**：`NotificationHookExecutor` 和 `HttpHookExecutor` 共享同一套 `{{Field}}` 模板插值语义，支持的字段完全一致。

**支持的字段**（来自 `HookPayload`）：

| 字段 | 说明 |
|------|------|
| `{{Event}}` | 事件名称 |
| `{{SessionId}}` | 会话 ID |
| `{{Cwd}}` | 当前工作目录 |
| `{{ToolName}}` | 工具名称 |
| `{{UserMessage}}` | 用户消息 |
| `{{AgentId}}` | Agent ID |
| `{{AgentType}}` | Agent 类型 |
| `{{Timestamp}}` | 触发时间戳（`yyyy-MM-dd HH:mm:ss`） |

**插值规则**：
- 未知字段保持原样（如 `{{Unknown}}` 不被替换）
- 字段值为 null 时替换为空字符串
- 大小写敏感（必须与字段名完全一致）

**实现**（共享 `HookTemplateRenderer.Render`，两个执行器调用同一实现）：

```csharp
internal static string RenderTemplate(string template, HookPayload payload)
{
    return TemplatePattern().Replace(template, match =>
    {
        var field = match.Groups[1].Value;
        return field switch
        {
            "Event" => payload.Event.ToString(),
            "SessionId" => payload.SessionId ?? string.Empty,
            "Cwd" => payload.Cwd ?? string.Empty,
            "ToolName" => payload.ToolName ?? string.Empty,
            "UserMessage" => payload.UserMessage ?? string.Empty,
            "AgentId" => payload.AgentId ?? string.Empty,
            "AgentType" => payload.AgentType ?? string.Empty,
            "Timestamp" => payload.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => match.Value,  // 未知字段保持原样
        };
    });
}
```

**理由**：
- 两个执行器共享插值语义，用户学习一次即可在 Notification 和 Http 之间无缝切换
- 未知字段保持原样而非报错，避免配置笔误导致 hook 执行失败
- `{{Field}}` 语法比 `${field}` / `$field` 更显式，避免与 shell 变量混淆
- 使用 `[GeneratedRegex]` 源生成器编译正则，避免运行时编译开销

> **设计决策**（2026-08-14 修订）：`RenderTemplate` 已提取为共享的 `HookTemplateRenderer`（同程序集 `internal` 静态类，`HttpHookExecutor`/`NotificationHookExecutor` 共用）。原"两个执行器各留一份副本、提取会增加跨模块依赖"的表述不成立——两个执行器本就在同一程序集内，不存在跨模块依赖；字段集若未来分化（如 Http 需要支持 `{{Headers}}` 复合字段），再按执行器拆分。

### 13. 使用 JSON Source Generator

**决策**：`HookSerializerContext` 为 Hook 子系统的 JSON 序列化提供 Source Generator 支持。

```csharp
[JsonSerializable(typeof(HookPayload))]
[JsonSerializable(typeof(HookResult))]
[JsonSerializable(typeof(HookConfig))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true)]
internal partial class HookSerializerContext : JsonSerializerContext;
```

**使用点**：
- `CommandHookExecutor`：序列化 `HookPayload`（stdin 传给外部进程）、反序列化 `HookResult`（解析 stdout）
- `HookSettingsLoader`：反序列化 `hooks.json`（通过 `JsonSerializerOptions` 而非 Source Generator，因需 `PropertyNameCaseInsensitive` 动态宽松解析）

**理由**：
- 消除运行时反射
- 提升高频序列化性能（`CommandHookExecutor` 每次 hook 触发都需序列化 payload）
- 集中声明序列化选项（CamelCase / 忽略 null / 枚举转字符串），避免多处重复配置

### 14. DI 注册

`ServiceCollectionExtensions.Business.cs` 的 `RegisterHookSubsystem` 方法统一注册：

```csharp
private static void RegisterHookSubsystem(IServiceCollection services)
{
    // 基础设施
    services.AddSingleton<GlobHookMatcher>();
    services.AddSingleton<HookSettingsLoader>();
    services.AddSingleton<HookRegistry>();
    services.AddSingleton<HookPolicyService>();

    // 执行器（按 HookType Keyed Services 分发）
    services.AddKeyedSingleton<IHookExecutor, CommandHookExecutor>(HookType.Command);
    services.AddKeyedSingleton<IHookExecutor, NotificationHookExecutor>(HookType.Notification);
    services.AddKeyedSingleton<IHookExecutor, HttpHookExecutor>(HookType.Http);

    // 通知渠道 Provider（新增渠道只需在此追加一行）
    services.AddSingleton<INotificationProvider, FeishuNotificationProvider>();
    services.AddSingleton<INotificationProvider, WeChatWorkNotificationProvider>();

    // 通知 Provider 的 HttpClient（带合理超时）
    services.AddHttpClient<FeishuNotificationProvider>();
    services.AddHttpClient<WeChatWorkNotificationProvider>();

    // 执行服务（接口 + 实现都注册，支持 Infrastructure 层通过接口注入）
    services.AddSingleton<HookExecutionService>();
    services.AddSingleton<IHookExecutionService>(sp => sp.GetRequiredService<HookExecutionService>());
    services.AddSingleton<HookConfigBootstrapper>();
}
```

**`HttpClient` 注册模式**：
- 通知 Provider 通过 `AddHttpClient<T>` 注册，享受 `IHttpClientFactory` 的连接池、超时、重试策略
- `HttpHookExecutor` 通过 `IHttpClientFactory.CreateClient("HookHttp")` 获取命名 HttpClient，与通知 Provider 的 HttpClient 隔离

## 影响

- **事件模型简化**：从 20+ 种事件收敛为 10 种，每种都有明确的外部消费场景，降低维护成本
- **执行器模型简化**：从 6+ 种执行器收敛为 3 种（Command / Notification / Http），移除与工具调用语义重叠的 Prompt / Agent 类型
- **异步 Hook 移除**：移除 `IAsyncHookRegistry` / `AsyncHookRegistryCleanupService` / `SessionHookStore`，所有 hook 统一为同步串行执行，简化系统模型
- **配置分离**：Hook 定义独立到 `hooks.json`，便于审计与版本管理
- **分层架构清晰**：`IHookExecutionService` 接口下沉到 Core 层，Infrastructure 层通过接口注入，避免反向依赖 App 层
- **安全边界明确**：工作区信任 + Pre-hook fail-closed 双层防护，防止恶意仓库通过 hook 执行任意命令
- **扩展点明确**：新增执行器类型只需实现 `IHookExecutor` + Keyed DI 注册；新增通知渠道只需实现 `INotificationProvider` + DI 注册；新增生命周期事件只需扩展枚举 + 业务模块触发

## 扩展指南

### 新增执行器类型

1. 扩展 `HookType` 枚举（`OneCode.Core/Hooks/HookTypes.cs`）
2. 更新 `HookTypeParser.Parse` 支持新类型字符串
3. 实现 `IHookExecutor`（`OneCode.App/Services/Hooks/`）
4. 在 `ServiceCollectionExtensions.Business.cs` 的 `RegisterHookSubsystem` 追加 `services.AddKeyedSingleton<IHookExecutor, YourExecutor>(HookType.YourType)`
5. 若需要 HttpClient，通过 `services.AddHttpClient<YourExecutor>()` 注册

### 新增通知渠道

1. 继承 `WebhookNotificationProviderBase`（推荐）或实现 `INotificationProvider`
2. 重写 `Name` / `CodeFieldName` / `MsgFieldName` / `ProviderDisplayName` / `BuildPayload` / `ComputeSign`
3. 在 `ServiceCollectionExtensions.Business.cs` 追加 `services.AddSingleton<INotificationProvider, YourProvider>()` 和 `services.AddHttpClient<YourProvider>()`
4. 在 `hooks.json` 中通过 `"provider": "your_provider_name"` 使用

### 新增生命周期事件

1. 扩展 `HookEvent` 枚举（`OneCode.Core/Hooks/HookEvent.cs`）
2. 在 `HookEventMetadataRegistry.All` 添加事件元数据（Summary / Description / MatcherMetadata）
3. 在业务模块注入 `IHookExecutionService` 并调用 `FireAsync`：
   ```csharp
   await _hooks.FireAsync(new HookPayload
   {
       Event = HookEvent.YourEvent,
       SessionId = sessionId,
       Cwd = workingDirectory,
   }, actualMatcherValue: "some_matcher_value", ct: ct);
   ```
4. 更新 `/hooks events` 命令输出（自动从 `HookEventMetadataRegistry` 生成，无需改代码）

### 新增模板插值字段

1. 在 `HookPayload` 添加字段（`OneCode.Core/Hooks/HookPayload.cs`）
2. 在 `HttpHookExecutor.RenderTemplate` 和 `NotificationHookExecutor.RenderTemplate` 的 switch 表达式添加对应分支
3. 在 [hooks.md §模板插值字段](../hooks.md#8-模板插值字段) 文档中补充字段说明
