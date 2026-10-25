# OneCode.App 项目约束

> 本文件为 App 层的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)，Core 层规范见 [Core/AGENTS.md](../OneCode.Core/AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

## 层职责定义

App 层是系统的**组合与实现层**，负责将 Core 接口与 Infrastructure 实现粘合在一起。

| 子目录 | 职责 |
|--------|------|
| `Commands/` | CLI 命令：`/review`、`/commit` 等斜杠命令 |
| `Daemon/` | 无头守护进程：`DaemonHost` 宿主 + `DaemonLifecycle`（互斥体/状态文件/停止哨兵）+ `DaemonStopWatcher` |
| `Logging/` | 调试文件日志提供器（`DebugFileLoggerProvider`）与日志 DI 扩展 |
| `Modes/` | 工作模式定义（`WorkingMode`：BUILD / PLAN / GOAL） |
| `Tools/` | AI 工具实现：Agent 调用的 27 个工具（`sealed class` + `[Description]` + `AddToolInstance<T>` 注册） |
| `Services/` | 应用层服务：Agent、Memory、PlanMode、BuildMode、GoalMode、Hooks、Lsp、Mcp 等 |
| `Query/` | 运行事件契约：`QueryEvent` 层级（唯一 UI 契约）、`StreamingSession`、`QueryStreamEngine`（**零 Terminal.Gui、零前端引用**） |
| `Skills/` | 子进程脚本运行器（`SubprocessScriptRunner`）；Skills 执行引擎在 `Services/Skills/`，frontmatter 解析在 `OneCode.Infrastructure/Skills/` |
| `Session/` | 会话上下文和生命周期管理 |
| `prompts/` | Prompt 模板文件（`system/*.prompt`） |

---

## 依赖约束

### 允许的依赖

| 依赖 | 用途 | 层 |
|------|------|----|
| `OneCode.Core` | 接口与领域模型 | Core |
| `OneCode.Infrastructure` | 外部系统适配 | Infrastructure |
| `Terminal.Gui` | **禁止**——TUI 渲染栈只允许出现在 `OneCode.Tui` 项目 | OneCode.Tui |
| `Microsoft.Extensions.AI` | AI 调用抽象（`IChatClient` / `ChatMessage` 等核心接口） | App |
| `Microsoft.Extensions.Hosting` | DI + 生命周期 | App |
| `System.Security.Cryptography.ProtectedData` | Windows DPAPI 密钥解密（HookSecretExpander） | App |
| `Markdig` | **禁止**——Markdown 渲染在 `OneCode.Tui` | OneCode.Tui |
| `CliWrap` | 进程调用（Bash/PowerShell 工具执行） | App |
| `Microsoft.Extensions.FileSystemGlobbing` | 文件 Glob 匹配 | App |

### 禁止的依赖

| 禁止项 | 原因 | 正确位置 |
|--------|------|---------|
| `HttpClient` 直接实例化 | 绕过 DI 池 | 注入 `IHttpClientFactory` |
| 静态可变状态（全局单例非 DI 管理） | 测试不可控 | 通过 DI Singleton 管理 |

> 文件 I/O 直接使用 `System.IO`；需要工作目录边界校验 + glob 枚举的代码注入
> `LocalAgentFileStore`（Infrastructure），其 `ResolvePath` 将路径限制在工作目录与
> `/add-dir` 附加目录内，越界抛 `UnauthorizedAccessException`。

### AI SDK 依赖约束

App 层**不直接引用**任何具体 AI SDK 包；以下 SDK 已下沉至 Infrastructure 层，App 通过 `ProjectReference` 传递依赖使用 MAF 类型，通过 Core 抽象（`IChatClient` 等）使用 AI 客户端：

| 依赖 | 所在层 | App 使用方式 |
|------|--------|-------------|
| `Anthropic` | Infrastructure | 仅通过 `IChatClient` 抽象使用，工厂在 `Infrastructure.Ai.ChatClientFactory` |
| `Microsoft.Extensions.AI.OpenAI` | Infrastructure | OpenAI 客户端工厂在 `Infrastructure.Ai.ChatClientFactory` |
| `Microsoft.Agents.AI.*` | Infrastructure | App 内 MAF 中间件 / AgentRunner 等仍直接 using MAF 类型（传递依赖），SDK 特化工厂代码已下沉 |
| `Hyperlight.*` | Infrastructure | 通过 `Infrastructure.Agent.HyperlightCodeActService` 注入使用 |

**已下沉到 Infrastructure 的 SDK 特化代码**：

| 类型 | 原位置（App） | 新位置（Infrastructure） |
|------|--------------|------------------------|
| `ChatClientFactory` | 原 `ServiceCollectionExtensions.ChatClient.cs` 内联工厂 | `OneCode.Infrastructure.Ai.ChatClientFactory` |
| `MaxOutputTokensDecorator` | `Services/Agent/MaxOutputTokensDecorator.cs` | `OneCode.Infrastructure.Ai.MaxOutputTokensDecorator` |
| `ProviderAwareDecorator` | `Services/Agent/ProviderAwareDecorator.cs` | `OneCode.Infrastructure.Ai.ProviderAwareDecorator` |
| `HyperlightCodeActService` | `Services/Agent/HyperlightCodeActService.cs` | `OneCode.Infrastructure.Agent.HyperlightCodeActService` |
| `RetryOnOverloadChatClient` | `Services/Agent/RetryOnOverloadMiddleware.cs` | `OneCode.Infrastructure.Ai.RetryOnOverloadChatClient` |
| `OpenAiResponseSanitizingHandler` | `Services/Agent/OpenAiResponseSanitizingHandler.cs` | `OneCode.Infrastructure.Ai.OpenAiResponseSanitizingHandler` |
| `EditTransaction` | `Services/Agent/EditTransaction.cs` | `OneCode.Infrastructure.Agent.EditTransaction` |
| `CompactionPipelineBuilder` | `Services/Agent/CompactionPipelineBuilder.cs` | `OneCode.Infrastructure.Agent.CompactionPipelineBuilder` |
| `AgentPipelineBuilder` | `Services/Agent/AgentPipelineBuilder.cs` | `OneCode.Infrastructure.Agent.AgentPipelineBuilder` |
| `UnifiedDiff` | `Tools/UnifiedDiff.cs` | `OneCode.Infrastructure.Text.UnifiedDiff` |
| `MissingApiKeyChatClient` | `Query/MissingApiKeyChatClient.cs` | `OneCode.Infrastructure.Ai.MissingApiKeyChatClient` |
| `HtmlToMarkdownConverter` | `Tools/HtmlToMarkdownConverter.cs` | `OneCode.Infrastructure.Text.HtmlToMarkdownConverter` |
| MAF 中间件集 + Invariants | `Services/Agent/`、`Middleware/` | `OneCode.Infrastructure.Middleware` / `Middleware.Invariants` |

### 保留在 App 层的 MAF 编排代码

以下 MAF 相关代码**保留在 App 层**，不继续下沉到 Infrastructure：

| 类型 | 位置 | 保留原因 |
|------|------|---------|
| `MainAgentRunner` / `ForkedAgentRunner` | `App.Services.Agent` | 业务编排器，依赖 13+ 个 App 服务（MemoryService、SessionManager、LspDiagnosticRegistry、PermissionModeProvider、SkillProviderFactory、ConversationShellExecutorManager 等），属于 Application 层"组合"职责，不属于 Infrastructure "外部系统适配"职责。下沉需提取 10+ 个 Core 接口，会让 Core 沦为接口垃圾场。 |
| `DesignContextProvider` / `LspDiagnosticContextProvider` / `PlanExecutionContextProvider` / `GoalContextProvider` / `ModeInstructionProvider` | `App.Services.Context` 等 | MAF `AIContextProvider` 子类，但本质是"将 App 层业务状态注入 LLM 上下文"的胶水代码，依赖 App 层服务（GoalContextState、PermissionModeProvider、SessionManager 等）。下沉不会减少 App↔Infrastructure 的耦合面。注：`ShellEnvironmentProvider` 不在此列——它是 MAF 包（`Microsoft.Agents.AI.Tools.Shell`）提供的类型，`SharedContextProviderBuilder` 直接消费。 |

**判断准则**：
- **下沉到 Infrastructure**：纯 SDK 适配代码（无状态、仅依赖 Core 抽象）→ Infrastructure。例：`ChatClientFactory`、MAF 中间件。
- **下沉到 Automation**：仅依赖 Core 抽象的后台调度/启动加载服务（`BackgroundService` / `IHostedService`）→ Automation。例：Cron 调度、ModelCatalog 刷新、YOLO 规则加载。
- **保留在 App**：业务编排代码（有状态、依赖多个 App 服务、组合多个组件）→ App。例：Runners、ContextProviders。

### 已迁出到 OneCode.Automation 的服务

以下服务**已从 App 迁出到 Automation 层**，App 仅通过 DI 反向注入实现 `ICronJobExecutor` 等接口供 Automation 调用：

| 类型 | 原位置（App） | 新位置（Automation） | 依赖 |
|------|--------------|---------------------|------|
| `CronSchedulerService` | `Services/Cron/CronSchedulerService.cs` | `OneCode.Automation.Cron.CronSchedulerService` | `ICronParser` + `ICronJobExecutor`（App 实现，DI 反向注入） |
| `ModelCatalogRefreshService` | `Services/ModelCatalog/ModelCatalogRefreshService.cs` | `OneCode.Automation.ModelCatalog.ModelCatalogRefreshService` | `IModelCatalogCache`（Core） |
| `YoloRuleStoreLoader` | `Services/Yolo/YoloRuleStoreLoader.cs` | `OneCode.Automation.Yolo.YoloRuleStoreLoader` | `YoloRuleStore`（Core） |

App 层通过 `OneCode.Automation.ServiceCollectionExtensions` 的 `AddCronScheduler` / `AddModelCatalogRefresh` / `AddYoloRuleStoreLoader` 注册这些服务。

---

## 工具开发规范（Tools/）

所有 AI 工具均为普通 `sealed class`，通过 `[Description]` 特性描述方法与参数，由 `ToolCatalog` 消费显式 AIFunction 工厂构建工具列表（**无反射**）。新增工具在 `Tools/ToolServiceCollectionExtensions.cs` 的 `AddToolServices` 注册流中通过 `AddToolInstance<T>` 扩展方法注册，一次性完成 DI 注册与 `ToolMetadataRegistry` 元数据登记。

### 工具类实现

```csharp
using System.ComponentModel;

namespace OneCode.App.Tools;

public sealed class MyTool
{
    [Description("Human-readable description of what this tool does.")]
    public async Task<string> ExecuteAsync(
        [Description("Action: foo or bar")] string action,
        [Description("Path to the target file")] string? path = null,
        CancellationToken ct = default)
    {
        // Validate input, perform work, return string result.
        return $"Done: {action} on {path}";
    }
}
```

### 工具注册（唯一入口）

`AddToolInstance<T>(name, Func<T, AIFunction>, risk, ...)`（`OneCode.Core.Tools.ToolRegistrationExtensions`）是工具注册的**唯一入口**，一次调用同时完成：

1. **DI 注册**：`TryAddSingleton<T>()` 解析工具类实例（幂等，可安全重复调用）
2. **元数据登记**：追加 `ToolRegistration`，其 `IEnumerable` 注入顺序 = `AddToolServices` 中的调用顺序（**重排会改变工具目录装配结果**）

`AIFunction` 由**显式工厂委托**创建（`sp => functionFactory(sp.GetRequiredService<T>())`）——无 `nameof` 方法名绑定、无运行时方法反射。

在 `Tools/ToolServiceCollectionExtensions.cs` 的 `AddToolServices` 注册流中追加：

```csharp
services.AddToolInstance<MyTool>("MyTool", tool => AIFunctionFactory.Create(tool.ExecuteAsync, name: "MyTool"), ToolRisk.Destructive,
    aliases: ["mt"],            // optional aliases
    concurrency: false,        // not concurrency-safe (default false)
    searchHint: "describe tool purpose");  // for ToolSearchTool retrieval
```

### ToolRisk 风险等级（`OneCode.Core.Tools.ToolRisk`）

| 值 | 含义 | 示例 |
|----|------|------|
| `ReadOnly` | 只读操作，可并发 | `ReadTool`, `GlobTool`, `GrepTool` |
| `Safe` | 修改但可回滚/可并发 | `TaskTool` |
| `Destructive` | 不可逆修改，禁止并发 | `WriteTool`, `EditTool`, `BashTool` |
| `Dynamic` | 风险取决于运行时输入 | `BashTool`（powershell 方言）、`WebFetchTool` |

### 错误处理

```csharp
public async Task<string> ExecuteAsync(string action, CancellationToken ct = default)
{
    try
    {
        // ...
        return result;
    }
    catch (OperationCanceledException)
    {
        throw; // 让框架处理取消，不要捕获
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "MyTool failed");
        return $"Error: Tool failed: {ex.Message}"; // 返回错误字符串，不抛出
    }
}
```

---

## 命令开发规范（Commands/）

**新命令必须继承 `Command` 抽象基类**（`OneCode.Core.Commands.Command`），而非直接实现 `ICommand`。基类提供了所有可选成员的 `virtual` 默认实现，减少样板代码。现有直接实现 `ICommand` 的命令不强制迁移。

### 必须实现的属性

```csharp
// ✅ 正确：继承 Command 抽象基类（新命令）
public sealed class MyCommand : Command
{
    public override string Name => "mycommand";
    public override string Description => "简短描述";
    public override CommandCategory Category => CommandCategory.Git;
    public override string? ArgumentHint => "[--flag] [arg]";
    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        // ...
    }
}

// ⚠️ 旧模式：直接实现 ICommand（现有命令保留，新命令不再使用）
public sealed class MyCommand : ICommand
{
    public string Name => "mycommand";
    // ...
}
```

### 参数解析约定

- 使用 `args.Contains("--flag")` 检测布尔标志
- 使用 `ParseFlag(args, "--key")` 提取 key-value 参数（辅助方法模式参见 `ReviewCommand`）
- 非 `--` 前缀的参数视为位置参数（文件路径等）
- 始终校验参数合法性并返回 `CommandResult.Error(msg)` 而非抛出异常

### 返回值约定

| 方法 | 用途 |
|------|------|
| `CommandResult.Text(msg)` | 直接输出文本到 TUI |
| `CommandResult.Prompt(prompt, tools?)` | 交给 LLM 处理（构建 Prompt） |
| `CommandResult.Error(msg)` | 错误提示（红色）|

### Prompt 文件管理

**必须文件化**：命令返回 `CommandResult.Prompt` 时，prompt 文本必须从 `.prompt` 文件加载，不得硬编码在 C# 字符串中。

**文件位置与覆盖机制**（三层，先命中先返回）：

| 层级 | 路径 | 优先级 |
|------|------|--------|
| 项目级 | `{workingDir}/.onecode/prompts/system/{name}.prompt` | 最高 |
| 用户级 | `~/.onecode/prompts/system/{name}.prompt` | 中 |
| 内置 | `{AppContext.BaseDirectory}/prompts/system/{name}.prompt` | 最低 |

**占位符语法**：prompt 文件中使用 `{{varName}}`，由 `PromptTemplate.Render(variables)` 替换。

**缺失处理策略**（二选一，按调用场景决定，禁止内联兜底常量——prompt 经 csproj 双重打包，生产环境必然存在，静默降级到低质量内置版本会掩盖打包问题）：

| 场景 | 策略 | 实现 |
|------|------|------|
| 用户可触发的命令路径（`/commit`、`/compact`、`/review`…） | 转为用户可见错误 | `Command.LoadPromptAsync` 返回 null → `CommandResult.Error("Prompt 'xxx' is not available...")` |
| 系统内部关键路径（harness、mode overlay、goal-decomposer、compaction 管道） | fail-fast 抛异常 | `?? throw new InvalidOperationException("Prompt 'xxx' not found in any IPromptManager store.")` |

```csharp
// 命令路径：基类 LoadPromptAsync 返回 null 时转错误
var prompt = await LoadPromptAsync(promptManager, "system/commit", variables, ct);
if (prompt is null)
    return CommandResult.Error("Prompt 'system/commit' is not available. Verify prompts/system/commit.prompt exists.");

// 内部关键路径：null 合并抛异常
var harness = await promptManager.GetPromptAsync(HarnessPromptName, ct).ConfigureAwait(false)
    ?? throw new InvalidOperationException($"Harness prompt '{HarnessPromptName}' not found in any IPromptManager store.");
```

单元测试中通过 `PromptManager.RegisterTemplate(new PromptTemplate(name, body))` 注入测试用 prompt，不依赖任何生产兜底。

**csproj 打包**：prompt 文件必须同时声明为 `Content`（复制到输出目录）和 `EmbeddedResource`：

```xml
<Content Include="prompts\**\*.prompt">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
<EmbeddedResource Include="prompts\**\*.prompt" />
```

**参考实现**：`CommitCommand`（错误呈现 + `--focus` 切换多套 prompt）、`MainAgentRunner` / `MainModeContextProviderBuilder`（内部关键路径 fail-fast）、`CompactCommand` / `CompactPromptBuilder`（服务层抛出、命令层转用户错误）。

### Prompt 分层与合成（harness / default / role / mode）

共享安全与探索底线集中在 `system/harness.prompt`，经 [`PromptComposer`](Services/PromptComposer.cs) 合成，**不要**在各 role / mode 文件中复制通用 injection / 并行工具 / Glob→Grep→Read 段。

| 片段 | 文件 | 内容职责 | 合成路径 |
|------|------|----------|----------|
| **Harness** | `system/harness.prompt` | 敏感文件、注入防御、并行工具、代码库分析顺序 | 所有 Agent system 的公共前缀 |
| **Default** | `system/default.prompt` | 产品身份、CLI 任务/工具说明、`{{…}}` 运行时占位符 | 主会话：`harness + default`（`PromptConfigBuilder`） |
| **Mode** | `system/build\|plan\|goal.prompt` | WorkingMode 工作流 overlay | 主会话 ContextProvider（Turn 1+），不替代 harness |
| **Role** | `system/{role}.prompt`（含 `planner`） | 角色身份与汇报格式；可含角色特有增量（如 coordinator 对 worker 结果的防御） | Fork：`harness + role`（`ForkedAgentRunner`，自定义代理 `.onecode/agents/*.md` 正文同理） |
| **Fork overlay** | `PipelineProfileBehavior.GetRoleInstruction` | Explore/Plan 子代理短角色句（勿误用 `system/plan.prompt` 主会话工作流） | Fork：`harness + overlay`（`ForkedAgentRunner`） |

覆盖 `system/harness.prompt`（项目/用户层）会同时影响主会话与全部子代理。角色文件只写差异化约束。

---

## UI 事件契约规范

- 唯一契约是 `Query/QueryEventTypes.cs` 的 `QueryEvent` 层级：引擎只产 UI 无关的强类型事件，前端只消费它
- 新增子类型必须同时更新 `src/OneCode.Tests/QueryEventContractTests.cs` 的基线清单、TUI 分发器与 WebUI 客户端；该测试对程序集内全部具体子类型做集合相等断言，不同步即失败
- 「穷尽性」指每个子类型在两个前端都有显式渲染分支的行为测试；不得用「反射计数硬编码数字」代替。未知子类型必须在两个前端各留一处 Warning 日志，不得静默丢弃
- 交互（审批 / 提问 / 向导 / 预算）走 Core 端口，事件不携带 `TaskCompletionSource`；端口默认实现留在 App（无头语义），交互式前端用 `Replace` 覆盖
- **零 Terminal.Gui 依赖**：不得 `using Terminal.Gui`，也不得引用 `OneCode.Tui` / `OneCode.WebUI` 类型
- 非运行通知（MCP 连接 / LSP 诊断）不经事件流，由前端直接订阅源事件
- Terminal.Gui 组件规范（颜色 token、状态栏注册、文件拆分原则）与 TUI 项目约束见 [OneCode.Tui/AGENTS.md](../OneCode.Tui/AGENTS.md)

---

## 服务组合规范（Services/）

- 服务通过 DI 注入，不手动实例化
- 跨服务调用通过接口，不直接引用具体服务类型
- 后台服务（`IHostedService`）必须支持 `CancellationToken` 并在停止时优雅退出
- `AutoDreamService` 等睡眠整合服务使用文件锁（`autodream.lock`）防止多进程并发

---

## 构建与测试

```bash
# 构建 App 项目
dotnet build src/OneCode.App/OneCode.App.csproj

# 运行 App 相关测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~OneCode.App"

# 运行特定命令测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~ReviewCommand"
```
