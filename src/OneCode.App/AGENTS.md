# OneCode.App 项目约束

> 本文件为 App 层的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)，Core 层规范见 [Core/AGENTS.md](../OneCode.Core/AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

## 层职责定义

App 层是系统的**组合与实现层**，负责将 Core 接口与 Infrastructure 实现粘合在一起。

| 子目录 | 职责 |
|--------|------|
| `Commands/` | CLI 命令：`/review`、`/commit` 等斜杠命令 |
| `Tools/` | AI 工具实现：Agent 调用的 50+ 工具（`sealed class` + `[Description]` + `AddTool<T>` 注册） |
| `Services/` | 应用层服务：Memory、Agent、Swarm、Lsp、MCP 等 |
| `Tui/` | Terminal.Gui 界面组件（TUI 独有依赖隔离在此） |
| `Skills/` | Skills 执行引擎（frontmatter 解析、参数替换） |
| `Session/` | 会话上下文和生命周期管理 |
| `prompts/` | Prompt 模板文件（`system/*.prompt` + `teams/*.yaml`） |

---

## 依赖约束

### 允许的依赖

| 依赖 | 用途 | 层 |
|------|------|----|
| `OneCode.Core` | 接口与领域模型 | Core |
| `OneCode.Infrastructure` | 外部系统适配 | Infrastructure |
| `Terminal.Gui` | TUI 渲染（仅 `Tui/` 子目录） | App |
| `System.CommandLine` | 命令行解析（仅 `Commands/` 和入口） | App |
| `Microsoft.Extensions.AI` | AI 调用抽象（`IChatClient` / `ChatMessage` 等核心接口） | App |
| `Microsoft.Extensions.Hosting` | DI + 生命周期 | App |
| `YamlDotNet` | YAML 解析（Skill frontmatter、主题文件） | App |
| `Markdig` | Markdown 渲染（TUI 消息展示） | App |
| `CliWrap` | 进程调用（Bash/PowerShell 工具执行） | App |
| `Microsoft.Extensions.FileSystemGlobbing` | 文件 Glob 匹配 | App |

### 禁止的依赖

| 禁止项 | 原因 | 正确位置 |
|--------|------|---------|
| 直接 `File.ReadAllText` 跨越 Infrastructure 层边界 | 绕过抽象层 | 使用 `IFileSystem` |
| `HttpClient` 直接实例化 | 绕过 DI 池 | 注入 `IHttpClientFactory` |
| 静态可变状态（全局单例非 DI 管理） | 测试不可控 | 通过 DI Singleton 管理 |

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
| `ChatClientFactory` | `ServiceCollectionExtensions.ChatClient.cs` 内联工厂 | `OneCode.Infrastructure.Ai.ChatClientFactory` |
| `MaxOutputTokensDecorator` | `Services/Agent/MaxOutputTokensDecorator.cs` | `OneCode.Infrastructure.Ai.MaxOutputTokensDecorator` |
| `ProviderAwareDecorator` | `Services/Agent/ProviderAwareDecorator.cs` | `OneCode.Infrastructure.Ai.ProviderAwareDecorator` |
| `HyperlightCodeActService` | `Services/Agent/HyperlightCodeActService.cs` | `OneCode.Infrastructure.Agent.HyperlightCodeActService` |
| `RetryOnOverloadChatClient` | `Services/Agent/RetryOnOverloadMiddleware.cs` | `OneCode.Infrastructure.Ai.RetryOnOverloadChatClient` |
| `OpenAiResponseSanitizingHandler` | `Services/Agent/OpenAiResponseSanitizingHandler.cs` | `OneCode.Infrastructure.Ai.OpenAiResponseSanitizingHandler` |
| `EditTransaction` | `Services/Agent/EditTransaction.cs` | `OneCode.Infrastructure.Agent.EditTransaction` |
| `CompactionPipelineBuilder` | `Services/Agent/CompactionPipelineBuilder.cs` | `OneCode.Infrastructure.Agent.CompactionPipelineBuilder` |
| `AgentPipelineBuilder` | `Services/Agent/AgentPipelineBuilder.cs` | `OneCode.Infrastructure.Agent.AgentPipelineBuilder` |
| `UnifiedDiff` | `Tools/UnifiedDiff.cs` | `OneCode.Infrastructure.Text.UnifiedDiff` |
| 9 个 MAF 中间件 + 3 个 Invariants + `FileEditContract` | `Services/Agent/`、`Middleware/` | `OneCode.Infrastructure.Middleware` / `Middleware.Invariants` / `Middleware.Contracts` |

### 保留在 App 层的 MAF 编排代码

以下 MAF 相关代码**保留在 App 层**，不继续下沉到 Infrastructure：

| 类型 | 位置 | 保留原因 |
|------|------|---------|
| `MainAgentRunner` / `ForkedAgentRunner` | `App.Services.Agent` | 业务编排器，依赖 13+ 个 App 服务（MemoryService、SessionMemoryService、SessionManager、LspDiagnosticRegistry、PermissionModeProvider、SkillProviderHolder、ConversationShellExecutorManager 等），属于 Application 层"组合"职责，不属于 Infrastructure "外部系统适配"职责。下沉需提取 10+ 个 Core 接口，会让 Core 沦为接口垃圾场。 |
| `DesignContextProvider` / `LspDiagnosticContextProvider` / `MemoryFileContextProvider` / `SessionMemoryContextProvider` / `PlanModeAttachmentProvider` / `BuildModeAttachmentProvider` / `GoalContextProvider` / `ShellEnvironmentProvider` / `AgentModeProvider` | `App.Services.Context` 等 | MAF `AIContextProvider` 子类，但本质是"将 App 层业务状态注入 LLM 上下文"的胶水代码，依赖 App 层服务（GoalContextState、PermissionModeProvider、SessionManager 等）。下沉不会减少 App↔Infrastructure 的耦合面。 |

**判断准则**：
- **下沉到 Infrastructure**：纯 SDK 适配代码（无状态、仅依赖 Core 抽象）→ Infrastructure。例：`ChatClientFactory`、MAF 中间件。
- **下沉到 Automation**：仅依赖 Core 抽象的后台调度/启动加载服务（`BackgroundService` / `IHostedService`）→ Automation。例：Cron 调度、Hook 清理、ModelCatalog 刷新、YOLO 规则加载。
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

所有 AI 工具均为普通 `sealed class`，通过 `[Description]` 特性描述方法与参数，由 `ToolCatalog` 在运行时反射解析为 `AIFunction`。新增工具在 `ServiceCollectionExtensions.Tools.cs` 的 `RegisterToolServices` 方法中通过 `AddTool<T>` 扩展方法注册，一次性完成 DI 注册与 `ToolMetadataRegistry` 元数据登记。

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

### 工具注册

在 `ServiceCollectionExtensions.Tools.cs` 的 `RegisterToolServices` 方法中追加：

```csharp
services.AddTool<MyTool>("MyTool", nameof(MyTool.ExecuteAsync), ToolRisk.Destructive,
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
| `Dynamic` | 风险取决于运行时输入 | `PowerShellTool`, `WebFetchTool` |

### 三种注册模式

1. **标准工具**（推荐）：`AddTool<T>(name, methodName, risk, ...)` — DI 解析实例 + 反射调用方法
2. **静态方法工具**：`AddToolStatic(name, type, methodName, risk, ...)` — 无需 DI 实例，调用静态方法
3. **工厂工具**：`AddToolInstance(name, factory, risk, ...)` — 运行时通过 `Func<IServiceProvider, AIFunction>` 创建 AIFunction（用于需要访问 `ToolMetadataRegistry` 等运行时状态的特殊工具）

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

**兜底常量模式**：每个文件化 prompt 的命令必须提供内联 `FallbackXxxPrompt` 常量，保证 `PromptManager` 为 null（单元测试）或文件缺失时仍可工作：

```csharp
private const string FallbackXxxPrompt = "极简版本，含 {{var}} 占位符";

private async Task<string> LoadPromptAsync(
    string name, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
{
    if (promptManager is null)
        return new PromptTemplate(name, FallbackXxxPrompt).Render(variables);

    var loaded = await promptManager.GetPromptAsync(name, ct).ConfigureAwait(false);
    var raw = string.IsNullOrWhiteSpace(loaded) ? FallbackXxxPrompt : loaded;
    return new PromptTemplate(name, raw).Render(variables);
}
```

**csproj 打包**：prompt 文件必须同时声明为 `Content`（复制到输出目录）和 `EmbeddedResource`：

```xml
<Content Include="prompts\**\*.prompt">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
<EmbeddedResource Include="prompts\**\*.prompt" />
```

**参考实现**：`CompactPromptBuilder`（文件优先 + 兜底常量）、`CommitCommand` / `ReviewCommand`（含 `--focus` 切换多套 prompt）/ `InitCommand`（文件化 + 变量渲染 + 兜底）。

### Prompt 分层与合成（harness / default / role / mode）

共享安全与探索底线集中在 `system/harness.prompt`，经 [`PromptComposer`](Services/PromptComposer.cs) 合成，**不要**在各 role / mode 文件中复制通用 injection / 并行工具 / Glob→Grep→Read 段。

| 片段 | 文件 | 内容职责 | 合成路径 |
|------|------|----------|----------|
| **Harness** | `system/harness.prompt` | 敏感文件、注入防御、并行工具、代码库分析顺序 | 所有 Agent system 的公共前缀 |
| **Default** | `system/default.prompt` | 产品身份、CLI 任务/工具说明、`{{…}}` 运行时占位符 | 主会话：`harness + default`（`PromptConfigBuilder`） |
| **Mode** | `system/build\|plan\|team\|goal.prompt` | WorkingMode 工作流 overlay | 主会话 ContextProvider（Turn 1+），不替代 harness |
| **Role** | `system/{role}.prompt`（含 `planner`） | Team 成员身份与汇报格式；可含角色特有增量（如 coordinator 对 worker 结果的防御） | Team：`harness + role`（`TeamAgentFactory`） |
| **Fork overlay** | `PipelineProfileBehavior.GetRoleInstruction` | Explore/Plan 子代理短角色句（勿误用 `system/plan.prompt` 主会话工作流） | Fork：`harness + overlay`（`ForkedAgentRunner`） |

覆盖 `system/harness.prompt`（项目/用户层）会同时影响主会话、Team 工人与 Explore/Plan fork。角色文件只写差异化约束。

---

## TUI 组件规范（Tui/）

- **颜色**：所有颜色通过 `TuiTheme` 取值，**禁止硬编码**颜色值
- **状态指示器**：新增全局状态（如沙箱模式 🔒、Plan 模式 📋）须在 `TuiStatusBar` 中注册
- **Terminal.Gui 限制**：不支持字体大小/行间距配置，不支持鼠标精确位置，不支持透明背景

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
