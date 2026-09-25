# Hook 模块架构设计

**状态**: Accepted
**日期**: 2026-07-18
**关联**: [hooks.md](../hooks.md)、[settings.md §Hooks 配置](../settings.md#hooks-配置)、[Permission vs ToolApproval vs Filter](./0001-permission-vs-toolapproval-vs-filter.md)

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

### 1. 拦截点收敛为 6 个开放点

**决策**：`HookInterceptionPoint` 只保留 6 个与 AGENT-HOOKS-0.1 对齐的开放拦截点，另有两个内部点不对用户 DSL 开放。

```csharp
public enum HookInterceptionPoint
{
    Input,          // 外部输入进入 agent run 前（无 matcher）
    PreModelCall,   // 模型请求前（matcher: model_id）
    PostModelCall,  // 模型完整响应后（matcher: model_id）
    PreToolCall,    // 工具执行前（可阻断；matcher: tool_name）
    PostToolCall,   // 工具执行后（matcher: tool_name）
    Output,         // 最终响应交付调用方前（无 matcher）

    AgentStartup,   // 内部：agent run 开始，不开放
    AgentShutdown,  // 内部：agent run 结束，不开放
}
```

**理由**：
- 6 个开放点覆盖 agent 的四类策略边界（输入 / 模型调用 / 工具调用 / 输出），与 AGENT-HOOKS-0.1 协议一致且**不再扩展**
- 会话生命周期、Stop 纠偏、压缩、Goal 编排、权限通知与异常告警不由 Hook 承载：这些属宿主职责，暴露给外部脚本只会造成职责重叠
- `AgentStartup` / `AgentShutdown` 表达 agent run 边界，与产品会话边界不等价，故仅内部使用
- `PreToolCall` 保留阻断语义（fail-closed）：安全策略需要在工具执行前拒绝

**拦截点元数据**：`HookPointMetadataRegistry` 为每个拦截点声明 matcher 字段名与可选值，用于 UI 展示和文档生成：

```csharp
[HookInterceptionPoint.PreToolCall] = new(
    "工具真正执行前",
    "在工具调用执行前触发。deny 使本次工具调用以错误载荷返回、批次循环继续（不中断整轮）",
    new MatcherMetadata("tool_name", ["Bash", "Edit", "Write", "Read", "Grep", "Glob", "Task", "todos_*", "mcp.*"]));
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
- **移除 `AgentHookExecutor`**：启动子 Agent 应由 `ForkedAgentRunner` / `GoalSubGoalLoop` 显式调度，Hook 触发子 Agent 会导致执行图不可预测
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

**DI 注册与分发**：

```csharp
// 执行器：普通注册；构造函数注入 IEnumerable<IHookExecutor> 后按 Type 自建分发字典
services.AddSingleton<IHookExecutor, CommandHookExecutor>();
services.AddSingleton<IHookExecutor, NotificationHookExecutor>();
services.AddSingleton<IHookExecutor, HttpHookExecutor>();

// 通知渠道：按 Provider Name 分发（IEnumerable<INotificationProvider> 注入）
services.AddSingleton<INotificationProvider, FeishuNotificationProvider>();
services.AddSingleton<INotificationProvider, WeChatWorkNotificationProvider>();
```

```csharp
public HookExecutionService(
    HookRegistry hookRegistry,
    IEnumerable<IHookExecutor> executors,
    HookPolicyService policyService,
    ILogger<HookExecutionService> logger)
{
    // 同类型重复注册时首个生效
    _executors = executors.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.First());
}

// NotificationHookExecutor 构造函数内：
_providers = providers?.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
```

**理由**：
- 新增执行器类型只需实现 `IHookExecutor` + 一行注册，`HookExecutionService` 按 `Type` 自建字典分发，无需修改本类
- 新增通知渠道只需实现 `INotificationProvider` + DI 注册，`NotificationHookExecutor` 通过 `Name` 字典查找
- 执行器分发保持"一个 `HookType` 一个执行器"，无需键控注入机制

### 4. Registry 二维索引 + Glob 匹配

**决策**：`HookRegistry` 按 `(HookInterceptionPoint, MatcherPattern)` 二维索引，`GlobHookMatcher` 实现 Glob 风格通配符匹配。

```csharp
public sealed class HookRegistry
{
    private readonly Dictionary<HookInterceptionPoint, List<MatcherGroup>> _matcherIndex = new();

    public void Register(HookRegistration hook)
    {
        // 按 Point 查找或创建 MatcherGroup
        // 按 MatcherPattern（大小写不敏感）查找或创建分组
        // 将 hook 追加到分组的 Hooks 列表
    }

    public IReadOnlyList<HookRegistration> GetMatchesForPoint(HookInterceptionPoint point, string? matcherValue)
    {
        // O(1) 拦截点查找 → 遍历该点下的 MatcherGroup
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
// 2. _hookRegistry.GetMatchesForPoint(payload.Point, actualMatcherValue)
// 3. hooks.Sort((a, b) => a.Priority.CompareTo(b.Priority))
// 4. 串行执行：foreach (hook in hooks) { results.Add(await ExecuteSingleHookAsync(hook, payload, ct)); }
// 5. HookResultAggregator.Aggregate(results)
// 6. 清理 once hook（Unregister）
```

**聚合策略**：

| 字段类型 | 聚合策略 | 理由 |
|---------|---------|------|
| 列表字段（`BlockingErrors` / `AdditionalContexts`） | 累加 | 所有阻断错误和额外上下文都应保留 |
| 字符串字段（`Message` / `SystemMessage`） | last-write-wins | 后执行的 hook 可覆盖前者的消息 |
| 结果字段（`Outcome` / `BlockingError`） | 任一阻断即生效 | 见 `HookResultAggregator.Aggregate` |

**理由**：
- 串行执行保证 hook 之间的顺序依赖（如审计 hook 需在通知 hook 之前执行）
- 串行避免并发执行导致的资源竞争（如多个 hook 同时写同一文件）
- 聚合策略保证多个 hook 的结果可合并为单一决策，调用方无需感知 hook 数量
- 异常隔离：单个 hook 执行器异常被吞掉记 Warning，不影响其他 hook 执行

### 6. 配置独立文件

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

**格式**：`HookSettingsLoader.ParseEventHooks` 仅解析 matcher-group 格式（数组元素即 `HookMatcherGroup`，内含 `matcher` 与 `hooks`）——分组用于表达"同一 matcher 下多个 hook"；无 `hooks` 字段的元素会被跳过。

```csharp
var group = JsonSerializer.Deserialize<HookMatcherGroup>(item.GetRawText(), JsonOptions);
if (group is not null && group.Hooks.Count > 0)
    groups.Add(group);
```

### 7. 优先级范围约定

**决策**：`Priority` 字段按范围划分来源，`HookConfigBootstrapper.Bootstrap` 经 private `BuildFromDirectory(configDir, basePriority)` 根据配置目录推导基础优先级（User 目录 100 / Project 目录 200）。

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
    var prePayload = new HookPayload { Point = HookInterceptionPoint.PreToolCall, ToolName = ctx.Function.Name, ... };
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
    var postPayload = new HookPayload { Point = HookInterceptionPoint.PostToolCall, ToolName = ctx.Function.Name, ... };
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

**决策**：飞书 / 企业微信 / 钉钉等 Webhook 类通知渠道共享 `WebhookNotificationProviderBase` 基类，子类只需重写 6 个抽象成员（基类 5 个 protected 成员 + 公共 `Name`）。

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
- 新增渠道（如钉钉）只需实现 6 个抽象成员，无需重复 HTTP/签名/解析逻辑

### 12. 模板插值统一语义

**决策**：`NotificationHookExecutor` 和 `HttpHookExecutor` 共享同一套 `{{Field}}` 模板插值语义，支持的字段完全一致。

**支持的字段**（来自 `HookPayload`）：

| 字段 | 说明 |
|------|------|
| `{{Point}}` | 拦截点名称 |
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
internal static string Render(string template, HookPayload payload)
{
    return TemplatePattern().Replace(template, match =>
    {
        var field = match.Groups[1].Value;
        return field switch
        {
            "Point" => HookInterceptionPoints.ToWireName(payload.Point),
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

两个执行器共享 `HookTemplateRenderer`（同程序集 `internal` 静态类）；字段集若未来分化（如 Http 需要 `{{Headers}}` 复合字段），再按执行器拆分。

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

`src/OneCode.App/Services/Hooks/HookServiceCollectionExtensions.cs` 的 `AddHookServices` 方法统一注册：

```csharp
public static IServiceCollection AddHookServices(this IServiceCollection services)
{
    // 基础设施
    services.AddSingleton<GlobHookMatcher>();
    services.AddSingleton<HookLoadDiagnostics>();
    services.AddSingleton<HookSettingsLoader>();
    services.AddSingleton<HookRegistry>();
    services.AddSingleton<HookPolicyService>();

    // 执行器（经 IEnumerable<IHookExecutor> 注入，按每个执行器的 Type 属性分发——非 Keyed Services）
    services.AddSingleton<IHookExecutor, CommandHookExecutor>();
    services.AddSingleton<IHookExecutor, NotificationHookExecutor>();
    services.AddSingleton<IHookExecutor, HttpHookExecutor>();

    // 通知渠道 Provider（新增渠道只需在此追加一行）
    services.AddSingleton<INotificationProvider, FeishuNotificationProvider>();
    services.AddSingleton<INotificationProvider, WeChatWorkNotificationProvider>();

    // 通知 Provider 的 HttpClient（带合理超时）
    services.AddHttpClient<FeishuNotificationProvider>();
    services.AddHttpClient<WeChatWorkNotificationProvider>();

    // 声明式通知渠道（notification-providers.json）：定义优先、编译型兜底
    services.AddHttpClient(NotificationProviderRegistry.HttpClientName);
    services.AddSingleton<NotificationProviderDefinitionLoader>();
    services.AddSingleton<NotificationProviderRegistry>();

    // 宿主停止时兜底关闭前台会话（reason=other；不再触发 hook）
    services.AddHostedService<HostStopSessionCloseService>();

    // 执行服务（接口 + 实现都注册，支持 Infrastructure 层通过接口注入）
    services.AddSingleton<HookExecutionService>();
    services.AddSingleton<IHookExecutionService>(sp => sp.GetRequiredService<HookExecutionService>());
    services.AddSingleton<HookConfigBootstrapper>();
    services.AddSingleton<HookConfigHotReloader>();

    return services;
}
```

`AddHookServices` 由组合根 `OneCodeApp.Create` 显式调用；`RegistrationOwnershipTests` 守卫注册归属。

**`HttpClient` 注册模式**：
- 通知 Provider 通过 `AddHttpClient<T>` 注册，享受 `IHttpClientFactory` 的连接池、超时、重试策略
- `HttpHookExecutor` 通过 `IHttpClientFactory.CreateClient("HookHttp")` 获取命名 HttpClient，与通知 Provider 的 HttpClient 隔离

### 15. 与 MAF AgentHooks 的关系（终判：不迁移）

**决策**：保留自研 Hook 引擎，不接入 `Microsoft.Agents.AI.AgentHooks`。`hooks.json` DSL 与 6 开放拦截点契约保持不变。

**理由**：

- **装配互斥（硬冲突）**：主链路 `AgentPipelineBuilder` 创建 `HarnessAgent`，其自行装配 `FunctionInvokingChatClient` 并以 `UseProvidedChatClientAsIs = true` 创建内部 agent；MAF 的 `AsAIAgentWithAgentHooks` 工厂以"不可分割整体"装配三层，且对这两种形态均显式拒绝（loud fail）；三缝装饰器为 `internal` 且不支持嵌套
- **依赖代价**：`Microsoft.Agents.AI.AgentHooks` 依赖外部 `ResponsibleAI.AgentHooks`（仅 `0.1.0-alpha.5`），带反射 STJ wire 编解码且 `IsAotCompatible=false`

**重评触发条件**（任一满足时重开评估）：

1. MAF 暴露可与 `HarnessAgent`（`UseProvidedChatClientAsIs` + 已含 `FunctionInvokingChatClient`）组合的缝级公开入口，或解除上述装配限制
2. OneCode 放弃 `HarnessAgent` 主链路装配形态
3. `ResponsibleAI.AgentHooks` GA 且版本与 OneCode 引用的 `Microsoft.Agents.AI*` 对齐

**预期落差**：即便将来可迁移，也是语义收紧（官方 fail-closed + transform 写回 + 历史持久门控），非等价替换；OneCode 侧无对应实现的能力清单见 [hooks.md §4 边界](../hooks.md#4-拦截点清单与边界)（transform 写回、`evaluate_only`、统一审计流均未建）。

## 影响

- **拦截点模型明确**：6 个开放拦截点对齐 AGENT-HOOKS-0.1，每个点都有明确的外部消费场景
- **执行器模型收敛为 3 种**：Command / Notification / Http，不含与工具调用语义重叠的 Prompt / Agent 类型
- **执行模型简单**：所有 hook 同步串行执行（带超时），无异步队列
- **配置分离**：Hook 定义独立到 `hooks.json`，便于审计与版本管理
- **分层架构清晰**：`IHookExecutionService` 接口下沉到 Core 层，Infrastructure 层通过接口注入，避免反向依赖 App 层
- **安全边界明确**：工作区信任 + Pre-hook fail-closed 双层防护，防止恶意仓库通过 hook 执行任意命令
- **扩展点明确**：新增执行器类型只需实现 `IHookExecutor` + 一行 `AddSingleton<IHookExecutor, X>()`；新增通知渠道只需实现 `INotificationProvider` + DI 注册；新增拦截点只需扩展枚举 + 业务模块触发

## 扩展指南

### 新增执行器类型

1. 扩展 `HookType` 枚举（`OneCode.Core/Hooks/HookTypes.cs`）
2. 更新 `HookTypeParser.Parse` 支持新类型字符串
3. 实现 `IHookExecutor`（`OneCode.App/Services/Hooks/`）
4. 在 `src/OneCode.App/Services/Hooks/HookServiceCollectionExtensions.cs` 的 `AddHookServices` 追加 `services.AddSingleton<IHookExecutor, YourExecutor>()`
5. 若需要 HttpClient，通过 `services.AddHttpClient<YourExecutor>()` 注册

### 新增通知渠道

1. 继承 `WebhookNotificationProviderBase`（推荐）或实现 `INotificationProvider`
2. 重写 `Name` / `CodeFieldName` / `MsgFieldName` / `ProviderDisplayName` / `BuildPayload` / `ComputeSign`
3. 在 `src/OneCode.App/Services/Hooks/HookServiceCollectionExtensions.cs` 的 `AddHookServices` 追加 `services.AddSingleton<INotificationProvider, YourProvider>()` 和 `services.AddHttpClient<YourProvider>()`
4. 在 `hooks.json` 中通过 `"provider": "your_provider_name"` 使用

### 新增拦截点

1. 扩展拦截点枚举（`OneCode.Core/Hooks/HookInterceptionPoint.cs`）
2. 在 `HookPointMetadataRegistry.All` 添加拦截点元数据（DisplayName / Description / MatcherMetadata）
3. 在业务模块注入 `IHookExecutionService` 并调用 `FireAsync`：
   ```csharp
   await _hooks.FireAsync(new HookPayload
   {
       Point = HookInterceptionPoint.YourPoint,
       SessionId = sessionId,
       Cwd = workingDirectory,
   }, actualMatcherValue: "some_matcher_value", ct: ct);
   ```
4. 更新 `/hooks events` 命令输出（自动从 `HookPointMetadataRegistry` 生成，无需改代码）

### 新增模板插值字段

1. 在 `HookPayload` 添加字段（`OneCode.Core/Hooks/HookPayload.cs`）
2. 在共享的 `HookTemplateRenderer.Render`（`src/OneCode.App/Services/Hooks/HookTemplateRenderer.cs`）的 switch 表达式添加对应分支
3. 在 [hooks.md §模板插值字段](../hooks.md#8-模板插值字段) 文档中补充字段说明
