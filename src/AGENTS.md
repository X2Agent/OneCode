# OneCode .NET — 重写规范与迁移规划

> 本文件为 AI 编码代理的核心约束文档。所有对 `src` 下 OneCode .NET 工程的修改必须遵守本文档中的所有规范。

---

## 目录

- [项目背景](#项目背景)
- [开发环境与构建命令](#开发环境与构建命令)
- [强制编码规范](#强制编码规范)
  - [0. 重构总则](#0-重构总则)
  - [1. C# 语言规范](#1-c-语言规范)
  - [2. 代码注释规范](#2-代码注释规范)
  - [3. 异步编程](#3-异步编程)
  - [4. 依赖注入](#4-依赖注入)
  - [5. 错误处理](#5-错误处理)
  - [6. 不许重复造轮子](#6-不许重复造轮子)
  - [7. 源生成器优先](#7-源生成器优先)
  - [8. Central Package Management](#8-central-package-management)
  - [9. 性能优化指引](#9-性能优化指引)
  - [10. 魔法数字与硬编码字符串](#10-魔法数字与硬编码字符串)
  - [11. 类设计约束](#11-类设计约束)
- [项目架构约束](#项目架构约束)
- [质量门禁](#质量门禁)
- [安全约束](#安全约束)
- [常见陷阱与最佳实践](#常见陷阱与最佳实践)
- [文件和目录命名约定](#文件和目录命名约定)
- [参考资源](#参考资源)

> **项目级补充文件：** [OneCode.Core/AGENTS.md](OneCode.Core/AGENTS.md)（依赖约束 + 接口规范）| [OneCode.Infrastructure/AGENTS.md](OneCode.Infrastructure/AGENTS.md)（外部集成约束）| [OneCode.Automation/AGENTS.md](OneCode.Automation/AGENTS.md)（后台调度服务约束）| [OneCode.App/AGENTS.md](OneCode.App/AGENTS.md)（业务逻辑 + TUI 约束）| [OneCode.Cli/AGENTS.md](OneCode.Cli/AGENTS.md)（AOT 约束 + 入口点规范）| [OneCode.Tests/AGENTS.md](OneCode.Tests/AGENTS.md)（单元测试约束，禁止敷衍测试）

---

## 项目背景

`src` 下的 `OneCode.slnx` 是基于 .NET 10 的完整重写实现，采用 Terminal.Gui v2 全屏 TUI + Microsoft.Extensions.AI 抽象 + Microsoft.Agents.AI (MAF) 编排框架。

| 维度 | 选型 |
|------|------|
| 运行时 | .NET 10 |
| UI | Terminal.Gui v2（全屏 TUI）；仅交互式 TUI，无独立非交互打印模式 |
| 异步 | Task + async-await |
| 包管理 | NuGet（Central Package Management） |
| 类型系统 | C# records/interfaces |
| 测试 | xUnit v3 + NSubstitute + FluentAssertions |

---

## 开发环境与构建命令

### 环境要求

- **.NET SDK 10** — 项目目标框架为 `net10.0`，必须安装对应 SDK
- **IDE** — Visual Studio 2022 v17.14+、Rider 2025.1+ 或 VS Code + C# Dev Kit
- **AOT 分析** — CLI 项目启用 `EnableAotAnalyzer`，详见 [Cli/AGENTS.md](OneCode.Cli/AGENTS.md)

### 构建命令

```bash
# 还原依赖
dotnet restore src/OneCode.slnx

# 构建整个解决方案
dotnet build src/OneCode.slnx

# 构建特定项目
dotnet build src/OneCode.Core/OneCode.Core.csproj
dotnet build src/OneCode.App/OneCode.App.csproj

# 发布 CLI（AOT 裁剪）
dotnet publish src/OneCode.Cli/OneCode.Cli.csproj -c Release
```

### 测试命令

```bash
# 运行所有测试
dotnet test src/OneCode.slnx

# 运行特定测试类
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~PermissionCheckerTests"

# 运行单个测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~PermissionCheckerTests.CheckAsync_BypassPermissions_AlwaysAllows"

# 运行测试并收集覆盖率
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --collect:"XPlat Code Coverage"

# 运行集成测试（需要 API Key）
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~IntegrationTests" -- RunConfiguration.TestSessionTimeout=120000
```

### PR 规范

- **标题格式**：`[dotnet] <简短描述>`，例如 `[dotnet] Add streaming tool execution support`
- **提交前必须通过**：
  ```bash
  dotnet build src/OneCode.slnx    # 无新增警告（历史警告不得扩大）
  dotnet test src/OneCode.slnx     # 全部通过
  ```
- **变更类型**需包含对应测试；新增公共 API 需包含 XML doc 注释
- **TODO 注释**必须关联 GitHub Issue：`// TODO(#123): ...`

---

## 强制编码规范

### 0. 重构总则

除非 PR 描述中明确声明需要保留兼容性，所有代码重构遵循以下原则：

- **简洁优先**：删除冗余的兼容层、双签名、过渡垫片；不为已废弃的调用方保留旧入口
- **不考虑向后兼容**：直接修改签名、删除废弃代码，不保留 `[Obsolete]` 双写期；调用方一并改到位
- **不保留死代码**：被替代的实现、注释掉的代码、不再调用的方法一律删除，依赖 Git history
- **默认参数用于简化常见调用方**：如 `Error = null` 让成功场景省略参数，而非为兼容旧构造而存在

```csharp
// ✅ 正确：新增字段给默认值，成功路径调用方简洁
public sealed record AgentRunResult(
    string Agent, Guid ConversationId, string? Output,
    int TurnsCompleted, bool MaxTurnsReached,
    AgentProblemDetails? Error = null);

// 真实失败路径显式传 Error: result.Error
// 成功路径（含测试 mock）省略 Error 参数

// ❌ 禁止：为兼容旧签名保留双构造函数 / [Obsolete] 过渡方法
public sealed record AgentRunResult(string Agent, ...) { // 旧 5 参数构造
    public AgentRunResult(string Agent, ..., AgentProblemDetails? Error) : this(...) {} // 新 6 参数
}
```

### 1. C# 语言规范

```csharp
// ✅ 正确：使用 C# 12+ 特性
public sealed record ConversationOptions(string Model, int MaxTurns);
public sealed class QueryEngine(IAnthropicClient client, IPermissionChecker perms);

// ❌ 禁止：旧式 C# 风格
public class ConversationOptions {
    public ConversationOptions(string model) { Model = model; }
    public string Model { get; set; }
}
```

**必须遵守：**
- 目标框架：`net10.0`，强制启用 `<Nullable>enable</Nullable>` 和 `<ImplicitUsings>enable</ImplicitUsings>`
- 优先使用 `record` / `sealed record` 表示不可变数据；不可变 DTO 必须用 `sealed record`，并优先使用 `required` 关键字（C# 11+）
- 类默认 `sealed`，除非明确设计为继承基类
- 所有公共 API 参数必须带 nullable 注解
- 使用 primary constructor（C# 12）减少样板代码
- 枚举值使用 `PascalCase`，不加前缀
- 文件名与主类名相同，一个文件一个主类型（helper 除外）

### 2. 代码注释规范

**原则：注释说明「为什么」，代码说明「做什么」**

注释的价值在于解释设计决策、边界条件、算法原理和非显而易见的副作用，而不是复述代码已经表达的逻辑。

#### 2.1 必须写注释的场景

| 场景 | 示例 | 说明 |
|------|------|------|
| 公开 API | `/// <summary>` 文档注释 | 所有 public/internal 类型和方法 |
| 非显而易见的设计决策 | `// Retry strategy: first 3 failures are fast (100ms), then exponential backoff` | 解释「为什么这样做」 |
| 算法核心步骤 | `// Step 3: Dijkstra relaxation — update each neighbor's tentative distance` | 复杂算法的分步说明 |
| 边界条件处理 | `// If the buffer is exactly full (rare edge case), allocate +1 to avoid sentinel overflow` | 解释特殊输入/异常路径 |
| 跨模块副作用 | `// This also updates the session-level token counter (see TokenTracker)` | 隐式依赖/副作用 |
| 临时变通方案 | `// Workaround: HttpClient on .NET 10 doesn't support HTTP/2 prior knowledge` | 标注技术债务 |
| 数值魔数 | `const int MaxRetryDelayMs = 30_000; // 30s — Anthropic's rate-limit window` | 解释常量来源 |

#### 2.2 不应写注释的场景

| 反模式 | 问题 | 改进方式 |
|--------|------|----------|
| 复述代码 | `// Increment counter by 1` ← 对应 `counter++` | 删除注释 |
| 显而易见的流程 | `// Step 1: validate input; Step 2: call API; Step 3: return result` | 用方法名和结构表达 |
| 过期注释 | 注释与代码逻辑不一致 | 代码变更时同步更新注释，否则删除 |
| 被注释掉的代码 | `// var oldCode = DoSomething();` | 彻底删除，依赖 Git history |
| 长篇设计文档 | `// 🚀 ARCHITECTURE: This uses a hexagonal... (50行)` | 放入设计文档而非代码中 |

#### 2.3 方法级注释（XML doc）

```csharp
/// <summary>
/// 调用 Anthropic Messages API 发送流式请求，返回所有事件（文本增量、工具调用等）。
/// 内部使用 Channel 实现真正的异步流式：后台写，前台 yield。
/// </summary>
/// <param name="messages">消息列表，不应为空。</param>
/// <param name="options">包含 model、max_tokens、tools 等配置。</param>
/// <param name="ct">取消令牌。</param>
/// <returns>IAsyncEnumerable 流式查询事件序列。</returns>
/// <exception cref="OperationCanceledException">当 ct 被取消时抛出。</exception>
/// <remarks>
/// 异常处理策略：
/// - PromptTooLong → 触发自动压缩并重试一次
/// - 其他错误 → 包装为 ErrorEvent 并终止查询
/// </remarks>
public async IAsyncEnumerable<QueryEvent> StreamQueryAsync(
    IReadOnlyList<ChatMessage> messages,
    ChatOptions options,
    [EnumeratorCancellation] CancellationToken ct)
```

#### 2.4 行内注释

```csharp
// ✅正确：解释「为什么」，代码本身说明「做什么」
// Anthropic requires tool_use blocks in the assistant message
// so tool_result blocks in the following user message can reference them by ID.
// Omitting them causes HTTP 400.
if (!string.IsNullOrEmpty(assistantText))
    assistantContents.Add(new TextContent(assistantText));

// ✅正确：标注非显而易见的边界条件
if (messages.Count < 4)
    return;  // Not enough messages to apply caching — need at least system + 3 turns

// ❌错误：复述代码
// Add text content to assistant contents if assistant text is not null or empty
if (!string.IsNullOrEmpty(assistantText))
    assistantContents.Add(new TextContent(assistantText));

// ❌错误：长篇注释应在方法文档中
// This method handles the full lifecycle of a tool execution request:
// 1. First it validates the tool input against the tool's schema
// 2. Then it checks permissions via the permission checker
// ... (should be a <summary> or <remarks> doc comment)
```

#### 2.5 注释语言

- **XML doc 注释（`///`）** — 中文或英文均可，保持项目一致
- **行内注释（`//`）** — 英文优先（代码是英文环境），复杂业务逻辑可用中文标注

#### 2.6 简化检查清单

每次提交前检查：
1. 是否有可能被误解的逻辑？→ 加注释
2. 是否有公开 API 缺少 `<summary>`？→ 补文档注释
3. 是否有「回文注释」（复述代码）？→ 删除
4. 是否有注释掉的代码块？→ 删除
5. 注释是否在代码变更后依然准确？→ 验证或更新

### 3. 异步编程

```csharp
// ✅ 正确
public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
{
    await using var stream = File.OpenRead(path);
    return ToolResult.Success(await ReadAsync(stream, ct));
}

// ❌ 禁止：.Result / .Wait() / 丢弃 CancellationToken
var result = ExecuteAsync(input).Result;
```

**规则：**
- 所有 I/O 操作必须是异步的
- 所有公共异步方法接受 `CancellationToken ct` 参数（最后一个参数）
- 不允许 `.Result`、`.Wait()`、`.GetAwaiter().GetResult()`、`Task.Run(() => syncCode).Result`
- **CLI 入口点 `Main` 必须返回 `Task<int>`**（详见 [Cli/AGENTS.md](OneCode.Cli/AGENTS.md)）
- 使用 `ConfigureAwait(false)` 在库代码中（非 UI 代码）

#### 3.1 IAsyncEnumerable / Channel 的强制使用场景

| 场景 | 必须使用 | 说明 |
|------|---------|------|
| 流式 SSE 响应 | `IAsyncEnumerable<T>` + `[EnumeratorCancellation]` | 使用 `await foreach` 消费，不得用 `.Result` 阻塞 |
| 多消费者广播 | `Channel<T>` | `Channel.CreateUnbounded<T>()` / `CreateBounded<T>()` |
| UI 增量渲染 | `Channel<T>` 单生产者多消费者 | 写端 `channel.Writer.WriteAsync(chunk, ct)` |

```csharp
// ✅ 正确：流式 SSE → IAsyncEnumerable
await foreach (var chunk in client.StreamAsync(request, ct).ConfigureAwait(false))
    yield return chunk;

// ✅ 正确：多消费者 → Channel（含错误传播）
var ch = Channel.CreateUnbounded<StreamChunk>();
var producerTask = Task.Run(async () =>
{
    try
    {
        await foreach (var c in source.ConfigureAwait(false))
            await ch.Writer.WriteAsync(c, ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        ch.Writer.TryComplete(ex);
        return;
    }
    ch.Writer.Complete();
});

// 消费端应 await producerTask 或检查其异常
```

**Channel 使用要点：**
- 生产者必须处理异常并通过 `ch.Writer.TryComplete(ex)` 传播错误，否则消费者将无限等待
- 禁止 fire-and-forget（`_ = Task.Run(...)`），必须追踪生产者 Task 以便错误传播
- 在消费端通过 `await producerTask` 或存储引用以供后续错误检查

### 4. 依赖注入

```csharp
// ✅ 正确：构造函数注入（primary constructor），所有依赖均为接口或抽象
public sealed class MemoryService(
    IFileSystemHelper fs,
    ILogger<MemoryService> logger,
    IOptions<MemoryOptions> options)
{
    // 直接使用字段
}

// ❌ 禁止：服务定位器、静态访问、注入具体类
public void DoWork() {
    var service = ServiceLocator.Get<IFoo>();  // 禁止
    var cfg = new ConfigManager();             // 禁止（应注入接口）
}
```

**规则：**
- 所有服务通过构造函数注入；**禁止在业务逻辑中使用 ServiceLocator（`GetService<T>()`）**
- **DI 工厂委托中允许 `sp.GetService<T>()`**：在 `services.AddSingleton<T>(sp => ...)` 等工厂委托中，`sp.GetService<T>()` 是获取可选依赖的标准模式，不视为 ServiceLocator 反模式
- **跨模块服务依赖优先注入接口或抽象类型**；只有配置对象、不可替换的状态持有器、框架类型、工厂/Options、以及当前尚未抽象的历史服务可注入具体类型
- 新增可替换服务时必须先定义接口；改动历史服务时，如调用方跨越模块边界，应优先补齐接口后再扩展
- 使用 `Microsoft.Extensions.DependencyInjection`（不引入额外 DI 容器）
- 服务生命周期：Singleton（无状态/线程安全）、Scoped（请求作用域）、Transient（轻量级无状态）

#### 4.1 IOptions\<T> 与 Keyed Services

- **所有配置注入必须使用 `IOptions<TOptions>` 或 `IOptionsMonitor<TOptions>`**，不直接注入裸 POCO
- **多实现注册**（如多个 `IHookExecutor`）使用 .NET 8+ Keyed Services：
  ```csharp
  services.AddKeyedSingleton<IHookExecutor, CommandHookExecutor>(HookType.Command);
  services.AddKeyedSingleton<IHookExecutor, NotificationHookExecutor>(HookType.Notification);
  services.AddKeyedSingleton<IHookExecutor, HttpHookExecutor>(HookType.Http);
  // 注入时：
  public HookExecutionService(
      [FromKeyedServices(HookType.Command)] IHookExecutor commandExecutor,
      [FromKeyedServices(HookType.Notification)] IHookExecutor notificationExecutor,
      [FromKeyedServices(HookType.Http)] IHookExecutor httpExecutor,
      ...) { }
  ```

### 5. 错误处理

```csharp
// ✅ 正确：使用 Result<T> 模式用于预期失败（异步版本）
public async Task<Result<string>> ReadFileAsync(string path, CancellationToken ct = default)
{
    try
    {
        return Result.Success(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
    }
    catch (FileNotFoundException)
    {
        return Result.Failure("File not found");
    }
}

// ✅ 正确：异常用于意外/不可恢复情况
throw new InvalidOperationException($"Tool '{name}' already registered.");

// ❌ 禁止：吞掉异常
catch (Exception) { }

// ❌ 禁止：用异常做流程控制
try { return int.Parse(s); } catch { return 0; }  // 用 int.TryParse
```

> **注意：** `Result<T>` / `Result` 是 `OneCode.Core.Results` 命名空间下的自定义类型，非 BCL 内置。完整定义和接口规范见 [Core/AGENTS.md](OneCode.Core/AGENTS.md)。

#### 5.1 catch 块最低要求

任何 `catch` 块**必须**通过 `ILogger` 记录日志（含异常对象）。具体要求：

1. **首选**：通过 `ILogger` 记录 `LogWarning` 或 `LogError` 级别日志，必须包含异常对象以保留堆栈
2. **兜底**：仅当类中确实无法注入 `ILogger`（如纯静态工具方法、性能关键的热路径 struct），允许 `ILogger?` 可空注入并在 null 时使用 `Debug.WriteLine`
3. **有意忽略场景**（如 JSON 反序列化失败导致 `parsedArgs = null`）仍须通过 `ILogger.LogDebug` 记录，而非静默吞掉

```csharp
// ✅ 正确：通过 ILogger 记录（推荐方式）
try { parsedArgs = JsonSerializer.Deserialize<Dictionary<string, object?>>(input); }
catch (Exception ex) { _logger.LogWarning(ex, "Failed to deserialize tool input"); }

// ✅ 正确：有意忽略但仍记录 Debug 级别
try { snapshot = File.ReadAllText(path); }
catch (Exception ex) { _logger.LogDebug(ex, "Snapshot read failed for {Path}, proceeding without diff", path); }

// ❌ 禁止：完全静默的 catch 块
catch { }
catch (Exception) { }

// ❌ 禁止：仅使用 Debug.WriteLine 而有 ILogger 可用
catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed: {ex.Message}"); }
```

#### 5.2 跨进程错误码（MCP / SSE / Subagent）

MCP、SSE 及子代理间错误传播**必须使用 `ProblemDetails` 风格的结构化错误对象**（RFC 7807），不得使用纯字符串 `message`：

```json
{
  "type": "https://errors.claudecode.dev/tool-execution-failed",
  "title": "Tool execution failed",
  "status": 500,
  "detail": "BashTool: command timed out after 30s",
  "traceId": "00-abc123-def456-01"
}
```

#### 5.3 全局错误处理策略

- **CLI 入口**：顶层 `try-catch` 捕获未处理异常，记录结构化日志后返回非零退出码
- **TUI 模式**：未处理异常通过 `Application.Invoke` 显示错误对话框，避免崩溃
- **异常到用户消息映射**：内部异常不得直接暴露给用户，需转换为友好提示
- **结构化日志**：所有异常必须通过 `ILogger` 记录，包含 `CorrelationId` / `TraceId`

#### 5.4 重试与弹性策略

使用 `Microsoft.Extensions.Http.Resilience`（Polly 集成），标准配置：

```csharp
services.AddHttpClient("anthropic")
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Backoff.Delay = TimeSpan.FromMilliseconds(100);
        options.CircuitBreaker.FailureRatio = 0.5;
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
    });
```

### 6. 不许重复造轮子

在实现任何功能前，首先检查以下官方/优秀开源库是否已经提供：

| 功能需求 | 使用库 | 选型理由 | 禁止自造 |
|---------|--------|---------|----------|
| HTTP 客户端 | `HttpClient` + `Microsoft.Extensions.Http` | BCL 内置，官方 IHttpClientFactory 集成 | 自定义 HTTP 包装器 |
| 弹性/重试 | `Microsoft.Extensions.Http.Resilience` (Polly) | 官方 Polly 集成，零额外依赖 | 自己写重试循环 |
| JSON 序列化 | `System.Text.Json` | BCL 内置，高性能，AOT 友好 | Newtonsoft.Json（除非有特殊需要） |
| YAML 解析 | `YamlDotNet` | 最成熟的 .NET YAML 库 | 自定义 YAML 解析器 |
| MCP 协议 | `ModelContextProtocol` v1.4.0（官方 SDK） | 官方支持，活跃维护 | 自实现 MCP 协议 |
| 全屏 TUI | `Terminal.Gui` v2.4+ | 实例化 IApplication 模型 + Scheme 主题 + Command/KeyBindings 输入架构；唯一的交互式 UI 框架 | `Spectre.Console`（命令式 `LiveDisplay` 无法模拟组件树，已下线）、手动 ANSI 转义码 |
| Glob 匹配 | `Microsoft.Extensions.FileSystemGlobbing` | 官方内置，零依赖 | 自写 glob |
| Markdown 渲染 | `Markdig` | 最完整的 .NET Markdown 实现 | 手写 MD 解析 |
| 日志 | `Microsoft.Extensions.Logging`（console + 自定义 file provider） | 官方抽象，接口与实现解耦；客户端单机场景不引入 Serilog 等第三方日志框架 | `Console.WriteLine` 日志、`Serilog`（已移除，客户端无需结构化日志后端） |
| Token 计算 | `Microsoft.ML.Tokenizers` | 微软官方，支持 OpenAI/Claude tokenizer | 手写 BPE |
| 内存缓存 | `Microsoft.Extensions.Caching.Memory` | 官方内置，支持过期策略 | 自定义字典缓存 |
| DI 容器 | `Microsoft.Extensions.DependencyInjection` | 官方标准，与 .NET 生态完全集成 | Autofac/Castle/自建 |
| LSP 客户端 | 自实现 JSON-RPC（LspClient） | 轻量化实现，无需额外依赖 | -- |
| 命令行解析 | `System.CommandLine` | 官方库，支持自动补全和帮助生成 | 自写 args 解析 |
| 密钥存储 | Windows DPAPI / `SecretService`（Linux） | OS 级安全存储，跨平台 | 明文文件/环境变量 |

---

## 项目架构约束

### 依赖方向（严格遵守，不可违反）

```
OneCode.Core          ← 仅 BCL + MS.Ext.Abstractions + MS.Extensions.AI.Abstractions
OneCode.Infrastructure← 依赖 Core + Microsoft.ML.Tokenizers + YamlDotNet
OneCode.Automation    ← 依赖 Core + Infrastructure（后台调度服务：Cron / Hooks 清理 / ModelCatalog 刷新 / YOLO 规则加载）
OneCode.App           ← 依赖 Core + Infrastructure + Automation（主业务逻辑 + TUI）
OneCode.Cli           ← 依赖 App（命令行入口，AOT 发布）
OneCode.Tests         ← 依赖上述所有（xUnit v3 测试）
```

**禁止：**
- Core 依赖任何具体库（详见 [Core/AGENTS.md](OneCode.Core/AGENTS.md)）
- 低层项目依赖高层项目（循环依赖）
- 任何项目直接依赖 Cli/App（反向依赖）
- Automation 反向依赖 App（Automation 通过 DI 反向注入接口消费 App 实现，如 `ICronJobExecutor`，禁止 ProjectReference）

> 编写代码时，以本文件为工程规范基线；各子项目的补充约束见上方"项目级补充文件"链接。

---

## 质量门禁

### 所有 PR 必须满足

1. **无新增警告** — 编译不得引入新的 CS/Analyzer 警告；触碰已有警告代码时应就近修复，确需保留必须有明确 `#pragma warning disable` 注释
2. **Nullable 干净** — 无 `!` 强制非空操作，除非有注释说明理由
3. **测试覆盖** — 新业务逻辑必须有对应 xUnit 测试（目标 80%+ 覆盖率）
4. **无 TODO 泄漏** — `// TODO` 注释必须有对应 GitHub Issue 编号
5. **禁止 `.GetAwaiter().GetResult()` / `.Result` / `.Wait()`** — 任何路径不允许同步阻塞 async
6. **禁止 ServiceLocator** — 禁止在业务逻辑中使用 `GetService<T>()`；DI 工厂委托中 `sp.GetService<T>()` 允许
7. **CLI 入口点必须返回 `Task<int>`** — 全链路 async（详见 [Cli/AGENTS.md](OneCode.Cli/AGENTS.md)）

### 测试规范

```csharp
// 测试项目：OneCode.Tests
// 文件命名：XxxTests.cs 对应 Xxx.cs
// 使用 xUnit v3 + NSubstitute + FluentAssertions

public sealed class PermissionCheckerTests
{
    private readonly PermissionChecker _sut;

    public PermissionCheckerTests()
    {
        var ruleStore = new YoloRuleStore(logger: null);
        ruleStore.ClearRules();
        var classifier = new YoloClassifier(ruleStore, new ToolMetadataRegistry(), logger: null);
        _sut = new PermissionChecker(classifier);
    }

    [Fact]
    public async Task CheckAsync_BypassPermissions_AlwaysAllows()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.BypassPermissions };
        using var doc = JsonDocument.Parse("{}");

        var result = await _sut.CheckAsync("Write", doc.RootElement, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task CheckAsync_PlanMode_DeniesWriteTool()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new ToolPermissionContext { Mode = PermissionMode.Plan };
        using var doc = JsonDocument.Parse(@"{""path"":""file.txt"",""content"":""x""}");

        var result = await _sut.CheckAsync("Write", doc.RootElement, ctx, ct);

        result.Decision.Should().Be(PermissionDecision.Deny);
    }
}
```

**xUnit v3 注意事项：**
- 使用 `TestContext.Current.CancellationToken` 替代手动传递 `CancellationToken.None`
- `JsonDocument` 实现 `IDisposable`，测试中必须 `using var doc = JsonDocument.Parse(...)`
- 使用 `[Fact]` / `[Theory]` + `[InlineData]`，与 xUnit v2 一致

---

## 安全约束

### 必须遵守（OWASP Top 10 缓解措施）

1. **命令注入防护** — `BashTool`/`PowerShellTool` 不得使用字符串拼接构造命令
   ```csharp
   // ✅ 正确：通过 ProcessStartInfo.ArgumentList 传参
   var psi = new ProcessStartInfo("bash") { ArgumentList = { "-c", userCommand } };

   // ❌ 危险：字符串拼接
   Process.Start($"bash -c {userCommand}");
   ```
   > **注意：** `ArgumentList` 避免了 shell 参数注入，但 `-c` 标志仍让 bash 解释 `userCommand` 的内容。对于不可信输入，需额外进行命令白名单验证或转义。

2. **路径遍历防护** — 所有文件操作必须调用 `PathsHelper.SafeResolve()`
   ```csharp
   // ✅ 正确
   var safePath = PathsHelper.SafeResolve(workingDir, userInput);

   // ❌ 危险：直接使用用户输入
   File.ReadAllText(userInput);
   ```

3. **API 密钥保护** — 不得在日志中输出 API Key、Bearer Token 的完整内容

4. **危险命令二次确认** — `BashCommandClassifier` / `PowerShellCommandClassifier` 标记的命令必须在执行前请求用户确认

---

## 常见陷阱与最佳实践

### 常见 C# 与 Terminal.Gui 模式陷阱

| 模式 | 正确做法 | 注意事项 |
|------|---------|---------|
| 不可变数据 | `record Foo(string? Bar)` | 使用 record，不用 class 表示数据 |
| 联合类型 | `Result<T>` or union via abstract record | 使用 discriminated union 模式 |
| 异步流 | `IAsyncEnumerable<T>` | 使用 `await foreach` 消费 |
| 事件流 | `IObservable<T>` 或 C# `event` | 偏好 `Channel<T>` 用于流数据 |
| 环境变量 | `EnvHelper.Get("VAR")` | 封装在基础设施层 |
| JSON 解析 | `JsonDocument.Parse(str)` | 注意 using/Dispose |
| 集合转换 | `.ToDictionary()` / LINQ | 使用 LINQ，不用手动循环 |
| 延时调度 | `await Task.Delay(ms, ct)` | 必须传 CancellationToken |
| 并行任务 | `await Task.WhenAll(...)` | 注意异常聚合 |
| Try 模式 | `TryXxx()` 模式 | `int.TryParse`, `dict.TryGetValue` |
| Terminal.Gui 组件 | `Terminal.Gui.ViewBase.View` 子类 + `Dim.Fill()` / `Pos.*` | 每个组件派生 `View` 子类；一组 `View` 加到 `Window` 构成组件树 |
| UI 状态更新 | 字段 + `SetNeedsDraw()`；跨线程用 `Application.Invoke(...)` | 可选叠加 `System.Reactive`；**禁止**直接从后台线程改 `View` 属性 |
| Spinner | 自绘 `View` + `Application.AddTimeout(interval, cb)` | 回调返回 `true` 继续、`false` 结束 |
| 键位绑定 | `View.KeyDown` / `Application.Top.KeyPress` / `KeyBinding` | **禁止**自开线程 `Console.ReadKey`；Terminal.Gui 已代管 stdin |
| 全屏 TUI 入口 | `using var app = Application.Create(); app.Init();` → `app.Run(Window)` → `app.Dispose()` | 入口统一封装在 `Ui/Tui/TuiHost.Run(TuiContext)` |

### MAF (Microsoft.Agents.AI) 使用规范

#### 结构化输出

需要 LLM 返回强类型 JSON 时，**必须**使用 `ChatResponseFormat.ForJsonSchema<T>()` 而非 `ChatResponseFormat.Json`：

```csharp
// ✅ 正确：JSON Schema 结构化输出 — 模型收到完整 schema 约束，输出准确率高
var chatOptions = new ChatOptions
{
    MaxOutputTokens = 2048,
    ResponseFormat = ChatResponseFormat.ForJsonSchema<GoalPlan>(),
};

// ❌ 禁止：裸 JSON 模式 — 模型不知道目标 schema，易产出缺字段/类型错误
var chatOptions = new ChatOptions
{
    ResponseFormat = ChatResponseFormat.Json,  // 仅保证输出是合法 JSON，不保证结构
};
```

**注意事项：**
- 反序列化仍需 `JsonSerializer.Deserialize<T>()`（`IChatClient` 层面返回的是 `ChatResponse.Text`）
- 建议保留 try/catch 降级路径（个别 provider 可能不完整支持 JSON Schema 模式）
- 用作 `ForJsonSchema<T>()` 的类型应为 `sealed record`，属性命名清晰

#### 中间件（Function Invocation Middleware）

项目当前使用 MAF 原生的 **委托式 Function Calling Middleware**（`agent.AsBuilder().Use(delegate)`），通过 `AgentPipelineBuilder` 统一构建中间件管道。这是 MAF 标准 API，**不是**自定义实现。

MAF 同时提供 `IFunctionInvocationFilter` 接口作为类型化的函数调用拦截机制。两种方式的对比：

| 机制 | API | 适用场景 |
|------|-----|---------|
| 委托式中间件 | `agent.AsBuilder().Use(Func<AIAgent, FunctionInvocationContext, ...>)` | 管道组合（当前项目主模式）：审计、权限、状态机、事务等需要顺序编排的中间件 |
| `IFunctionInvocationFilter` | DI 注册 `IFunctionInvocationFilter` 实现 | 单一关注点的独立拦截器：日志、指标采集等无管道依赖的 Filter |

**约定：**
- `AgentPipelineBuilder` 中的管道式中间件是主拦截路径，新增管道级中间件继续使用 `.Use()` 委托模式
- 如需添加**无管道位置依赖**的独立拦截器（如全局指标采集），可实现 `IFunctionInvocationFilter` 并通过 DI 注册
- **禁止**在同一关注点上同时注册 `.Use()` 中间件和 `IFunctionInvocationFilter`，避免双重拦截
- 新增 Filter 必须在 PR 中说明为何不适合纳入 `AgentPipelineBuilder` 管道

---

### 7. 源生成器优先

减少反射、提升 AOT 友好性，下列场景**必须**使用源生成器：

| 场景 | 方式 | 示例 |
|------|------|------|
| JSON 序列化（类型 > 100KB 或 AOT 发布） | `JsonSerializerContext` 源生成器 | `[JsonSerializable(typeof(MyDto))]` |
| 正则常量 | `[GeneratedRegex]` | `[GeneratedRegex(@"\d+")]` |
| P/Invoke | `LibraryImport`（替代 `DllImport`） | `[LibraryImport("kernel32.dll")]` |

```csharp
// ✅ 正确：使用源生成器正则
[GeneratedRegex(@"[\p{L}\p{N}_-]{2,}")]
private static partial Regex QueryTokenRegex();

// ❌ 禁止：运行时编译正则（反射开销）
private static readonly Regex QueryTokenRegex = new(@"[\p{L}\p{N}_-]{2,}", RegexOptions.Compiled);
```

> AOT 发布的完整约束和 JSON 源生成器迁移路线图见 [Cli/AGENTS.md](OneCode.Cli/AGENTS.md)。

---

### 8. Central Package Management

仓库根目录下的 `Directory.Packages.props` 和 `Directory.Build.props` 统一管理所有项目的：

- **包版本**：所有 `<PackageReference>` 不写版本号，版本统一在 `Directory.Packages.props` 中声明
- **编译选项**：`Nullable=enable`、`ImplicitUsings=enable`、`LangVersion=latest`、`AnalysisLevel=latest-default`、`TreatWarningsAsErrors`（当前设为 `false`，通过 `NoWarn` 排除部分规则；目标逐步收紧至 `true`）
- **目标框架**：统一 `TargetFramework=net10.0`

> 已落地：`src/Directory.Packages.props` + `src/Directory.Build.props` + `.editorconfig`

---

### 9. 性能优化指引

#### 9.1 ValueTask\<T>

对于热路径且通常同步完成的方法，使用 `ValueTask<T>` 替代 `Task<T>` 以减少堆分配：

```csharp
// ✅ 正确：热路径 + 通常同步完成
public ValueTask<string> GetCachedValueAsync(string key, CancellationToken ct = default)
{
    return _cache.TryGetValue(key, out var value)
        ? new ValueTask<string>(value)
        : FetchFromSourceAsync(key, ct);
}

// ⚠️ 注意：禁止多次 await 同一个 ValueTask 实例
// 如果需要多次消费，先 .AsTask() 转为 Task
```

#### 9.2 ArrayPool\<T> / ObjectPool\<T>

高频分配场景（如 JSON 序列化缓冲区、大数组处理）应使用内存池化：

```csharp
// ✅ 正确：租用缓冲区而非每次分配
var buffer = ArrayPool<byte>.Shared.Rent(minSize);
try
{
    // 使用 buffer
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```

#### 9.3 字符串分配优化

- 使用 `ReadOnlySpan<char>` / `AsSpan()` 避免子字符串分配
- 使用 `string.Create()` 或 `StringBuilder` 替代字符串拼接
- 对大量字符串比较使用 `StringComparison.Ordinal`

---

### 10. 魔法数字与硬编码字符串

#### 10.1 禁止魔法数字

所有非自明的数字字面量**必须**定义为命名常量，并附带注释说明其来源：

```csharp
// ✅ 正确：命名常量 + 注释说明来源
private const int MaxOutputTokensFallback = 4096;  // Claude's default max output for many models
private const int RequiredTokensAboveThinkingBudget = 1024;  // Buffer above thinking budget for response

// ❌ 禁止：在代码中直接出现非自明的数字
if (tc.Arguments is { Length: > 2 })  // ← 2 是什么？
var maxTurns = options.MaxTurns ?? 100;  // ← 为什么默认 100？
```

**例外：** 以下场景允许使用内联数字：
- 数学公式中的已知常数（`radius * Math.PI * 2`）
- 数组索引和计数循环（`for (int i = 0; i < items.Count; i++)`）
- `+1` / `-1` 的偏移操作（`count - 1`）
- 已在名称中自明的常量（`const int MaxRetries = 3;`）

#### 10.2 硬编码字符串规范

以下类型字符串**必须**集中在常量类中统一管理，具体位置按分层原则如下：

| 类别 | 常量位置 | 示例 |
|------|---------|------|
| 环境变量名 | [Core.Constants.EnvVars](OneCode.Core/Constants.cs) | `OneCodeApiKey`, `OneCodeBaseUrl`, `OneCodeModel` |
| 配置 JSON 字段名 | [Core.Constants.ConfigKeys](OneCode.Core/Constants.cs) | `ApiKey`, `Model`, `Provider`, `MaxTurns` |
| Model Provider 标识 | [Core.Constants.ModelProviders](OneCode.Core/Constants.cs) | `Anthropic`, `OpenAI`, `Ollama` |
| 会话默认值 | [Core.Constants.Session](OneCode.Core/Constants.cs) | `MaxTurnsDefault`, `MaxBudgetUsdDefault` |
| Permission 模式 | [Core.Constants.PermissionModes](OneCode.Core/Constants.cs) | `Default`, `BypassPermissions`, `Plan` |
| 消息类型 | [Core.Constants.MessageTypes](OneCode.Core/Constants.cs) | `User`, `Assistant`, `Result` |
| HttpClient 注册名 | [Infrastructure.Constants.HttpClientNames](OneCode.Infrastructure/Config/Constants.cs) | `McpRegistry`, `WebSearch` |
| 子目录名 | [Infrastructure.Constants.Subdirs](OneCode.Infrastructure/Config/Constants.cs) | `Skills`, `Prompts`, `MemoryStore` |
| 超时/阈值 | [Infrastructure.Constants.Timeouts](OneCode.Infrastructure/Config/Constants.cs) | `McpRegistry`, `WebSearch` |
| 应用文件信息 | [Infrastructure.Constants.App](OneCode.Infrastructure/Config/Constants.cs) | `SettingsFileName`, `McpFileName` |

> **注：** 工具执行限制（如 `MaxResultSizeChars`、`MaxOutputChars`）当前散落在 App 层工具类内部私有常量中，尚未集中到 Infrastructure 层。新增此类常量时，建议集中到 `Infrastructure.Constants.Tools` 子类。

```csharp
// ✅ 正确：使用 Core 层常量（引用时使用别名避免命名冲突）
using CoreConstants = OneCode.Core.Constants;

var apiKey = Environment.GetEnvironmentVariable(CoreConstants.EnvVars.OneCodeApiKey);
settings.MaxTurns = Get(CoreConstants.ConfigKeys.MaxTurns, CoreConstants.Session.MaxTurnsDefault);

// ✅ 正确：使用 Infrastructure 层常量（已有 using OneCode.Infrastructure.Config; 时直接使用）
var storeDir = Path.Combine(home, Constants.Subdirs.MemoryStore);

// ❌ 禁止：散落的字符串字面量（多使用场景）
var apiKey = Environment.GetEnvironmentVariable("ONECODE_API_KEY");
var maxTurnsValue = _values["maxTurns"];
```

> **跨层引用规则：** 由于 Core 层不依赖 Infrastructure 层，所有跨层共享的常量定义在 `OneCode.Core.Constants` 中。各层引用 Core 常量时，推荐使用 `using CoreConstants = OneCode.Core.Constants;` 别名，以避免与 Infrastructure 的 `OneCode.Config.Constants` 冲突。

#### 10.3 常量定义判定标准（何时定义常量、何时用内联字符串）

判定原则：**是否是多个地方使用的 Key/值**。

| 场景 | 判定 | 处理方式 |
|------|------|---------|
| 环境变量名在多处使用 | ✅ 必须定义常量 | 添加到 `Core.Constants.EnvVars` |
| 配置 JSON Key 在多处使用 | ✅ 必须定义常量 | 添加到 `Core.Constants.ConfigKeys` |
| 配置 JSON Key 仅在单个类内部使用 | ❌ 可直接用字符串 | `Environment.GetEnvironmentVariable("ONECODE_AUTODREAM")`（只在 AutoDreamService 中使用） |
| 多文件共享的字符串常量 | ✅ 必须定义常量 | 中心化到对应层级的常量类 |
| 方法内部的局部魔数 | ❌ 留在方法内 | `const int MaxRetries = 3;` 或 `foreach (var i in Enumerable.Range(0, 3))` |
| 格式化/路径拼接中的分隔符 | ❌ 可用字面量 | `Path.Combine(home, ".onecode")` 或 `$"prefix-{id}.json"` |

```csharp
// ✅ 正确：仅在单类使用的环境变量，直接内联字符串
// AutoDreamService.cs 中 ONECODE_AUTODREAM 仅在当前类中使用一次
var autoDreamEnv = Environment.GetEnvironmentVariable("ONECODE_AUTODREAM");

// ✅ 正确：多文件共享的环境变量，必须使用常量
// 在 ServiceCollectionExtensions.ChatClient.cs 和 DoctorCommand.cs 中多处使用
var apiKey = Environment.GetEnvironmentVariable(CoreConstants.EnvVars.OneCodeApiKey);

// ❌ 错误：多文件共享的 Key 没有使用常量
var key = _values["apiKey"];  // "apiKey" 在 ServiceCollectionExtensions.cs 和 ConfigManager.cs 中均使用
```

> **例外规则：** 标准操作系统环境变量（如 `HOME`、`USERPROFILE`、`PATH`）在多处使用时仍然建议定义为常量，已在 `Core.Constants.EnvVars` 中覆盖。

---

### 11. 类设计约束

#### 11.1 单一职责原则（SRP）

- 单个类文件建议不超过 **500 行**（不含 XML doc 注释、空行）
- **硬上限 600 行**：超过必须在 PR 评审中说明拆分障碍（如 TUI 视图、生成代码）
- 测试文件（`*Tests.cs`）放宽至 **700 行**：测试场景天然偏长，内聚性更重要
- 构造函数注入参数不超过 **8 个**（超过说明职责过多）
- 超过以上限制的类应拆分为多个聚焦的协作类

```csharp
// ✅ 正确：通过组合拆分职责
public sealed class StreamingQueryCoordinator(
    ITokenRecoveryHandler recovery,
    IMessageCompactionService compaction,
    IToolExecutionOrchestrator executor) { ... }

// ❌ 反模式：上帝类（God Class）——单类承担多职责，注入 10+ 依赖
public sealed class SomeGodService  // 流式查询 + token 恢复 + 消息压缩 + 工具执行 + 缓存控制 + 钩子触发 ...
{
}
```

#### 11.2 接口隔离原则（ISP）

- 新增接口原则上不应超过 **5 个成员**（属性+方法）；超过时必须拆分或在评审中说明原因
- 不应新增聚合接口（如 `IXxx : IXxxCore, IXxxValidation, ...`）；消费者按需依赖小接口，而非大接口

#### 11.3 依赖倒置原则（DIP）

已在 §4 中规定"**跨模块服务依赖优先注入接口或抽象类型**"，此处强调：
- 新增 `public` 且可替换的服务类应有对应接口；纯数据类型、静态工具类、内部组合根、Options/配置类除外
- 通用工具类（如 `MessageCloner`）可使用 `static` 方法
- DI 注册对外暴露接口优先；组合根内部可注册具体实现以便工厂、Options、生命周期管理或兼容历史构造路径

#### 11.4 开闭原则（OCP）

- 避免在核心逻辑中使用 `switch` 判断类型/模式来决定行为
- 新增行为应通过添加新类（策略/提供者/处理器）实现，而非修改现有类
- 使用策略模式、工厂模式或 `IToolProvider` 发现机制替代新的硬编码注册；历史集中注册路径只做兼容维护

```csharp
// ✅ 正确：声明式权限配置表（PermissionProfiles）
var profile = PermissionProfiles.GetProfile(PermissionMode.Plan);
var result = PermissionProfiles.Check(PermissionMode.Plan, toolName, toolInput, context);

// ❌ 反模式：在 CheckAsync 中对每种模式写独立 switch 分支
public PermissionCheckResult Check(...)
{
    switch (context.Mode)  // 每增加一种模式就要改这段代码
    {
        case PermissionMode.BypassPermissions: ...
        case PermissionMode.Plan: ...
        // ... 7 种模式 ...
    }
}
```

权限模式行为由 <c>PermissionProfiles</c> 静态注册表定义（<c>GetProfile</c> / <c>Check</c>），
<code>PermissionChecker</code> 在 Auto 模式下委托 <c>YoloClassifier</c>，其余模式直接查表。
工具注册通过 <c>ToolRegistration</c> + <c>AddTool&lt;T&gt;</c> 扩展方法在 DI 注册时一并完成元数据登记（详见 [OneCode.App/AGENTS.md](OneCode.App/AGENTS.md) 工具开发规范），由 <c>ToolCatalog</c> 在运行时通过反射解析为 <c>AIFunction</c>。新增工具时通过 <c>ServiceCollectionExtensions.Tools.cs</c> 中的 <c>AddTool&lt;T&gt;</c> 调用注册即可，无需修改集中注册表。

---

## 文件和目录命名约定

| 类型 | 规范 | 示例 |
|------|------|------|
| 接口 | `IXxx.cs` | `ITool.cs`, `IPermissionChecker.cs` |
| 实现 | `XxxService.cs` / `XxxManager.cs` | `MemoryService.cs` |
| DTO/记录 | `XxxDto.cs` / `XxxModel.cs` 或直接 `Xxx.cs` | `ApiMessage.cs` |
| 扩展方法 | `XxxExtensions.cs` | `JsonExtensions.cs` |
| 测试 | `XxxTests.cs` | `PermissionCheckerTests.cs` |
| 枚举 | `XxxKind.cs` / `XxxMode.cs` | `PermissionMode.cs` |
| 常量类 | `XxxConstants.cs` | `ApiConstants.cs` |

---

## 参考资源

- **Anthropic API 文档：** https://docs.anthropic.com/en/api/
- **ModelContextProtocol SDK：** https://github.com/modelcontextprotocol/csharp-sdk
- **Terminal.Gui v2 文档：** https://gui-cs.github.io/Terminal.Gui/
- **Microsoft .NET 10 文档：** https://learn.microsoft.com/en-us/dotnet/
- **Microsoft.ML.Tokenizers：** https://github.com/dotnet/machinelearning
- **Microsoft.Agents.AI (MAF)：** https://learn.microsoft.com/en-us/semantic-kernel/frameworks/agent-framework/
