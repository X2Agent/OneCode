# OneCode.Core 项目约束

> 本文件为 Core 项目的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

## 目录职责

| 目录 | 职责 |
|------|------|
| `Agent/` | 子代理能力枚举（`AgentCapability`） |
| `Ai/` | AI 客户端工厂契约（`IChatClientFactory`） |
| `Build/` | Build 运行领域模型（`BuildRun` 状态迁移） |
| `Commands/` | 命令契约：`Command` 抽象基类、`ICommand`、`CommandResult`、参数辅助 |
| `Config/` | 配置模型（`ConfigModels` 及字段描述符） |
| `Coordinator/` | 编排事件族（`OrchestrationEvent`） |
| `Cron/` | `ICronParser` 契约 + `CronExpressionHelper` 纯格式化（Cronos 实现在 Automation） |
| `Domain/` | 会话领域模型：`Conversation` / `Message` / `ContentBlock` / `MessageCloner` + 模型自配套 JSON converter（`ContentBlockConverter`） |
| `Errors/` | 错误记录类型 |
| `Exec/` | Shell 执行契约（`IShellExecutor` + DTO） |
| `Goals/` | Goal 运行领域模型：`GoalRun` 状态机、`GoalBudget` |
| `Hooks/` | Hook 契约 + 纯聚合逻辑（`HookResultAggregator`、`HookTypeParser`） |
| `IO/` | 纯路径算法（`PathBoundary`、`PathComparer`），无系统调用 |
| `Keybindings/` | 快捷键引擎：解析（`KeybindingParser`）、校验（`KeybindingValidator`）、默认表、冲突解析 |
| `Lsp/` | LSP 契约：`ILspServerManager`、`LanguagePack`、`ICodeIndexService` |
| `Mcp/` | MCP 契约（`IMcpConnectionManager` 等）+ `McpToolFilter` 白名单匹配 |
| `Memory/` | 记忆持久化契约（`IMemoryEntryStore` / `MemoryEntry`） |
| `Models/` | 模型目录（`ModelCatalog`，含 JSON 流解析） |
| `Permissions/` | 权限引擎：Shell 分类器（PowerShell/Bash）、`ShellTokenizer`、YOLO 规则存储（持久化经 `IYoloRuleFileStore` 外移 Infrastructure） |
| `PlanMode/` | Plan 记录族 + `PlanStepValidator` |
| `Product/` | 产品信息（`ProductInfo`） |
| `Prompt/` | `PromptTemplate` 插值 + `PromptManager` 责任链（存储经 `IPromptStore` 外移） |
| `Results/` | `Result<T>` 操作结果单子 |
| `Search/` | Web 搜索契约（`IWebSearchProvider`） |
| `Session/` | 会话事件类型与投影（`ConversationEventMapper`、`SessionEventProjection`）+ 会话存储契约（`ISessionStore` / `ConversationSummary` / `ISessionEventStore`） |
| `Skills/` | 内置技能文本（`BundledSkills`） |
| `Tasks/` | 子代理任务：`TaskService` 内存任务表（持久化经 `ITaskStore` 外移） |
| `Text/` | 纯文本算法（`StringDistance`、`TextTokenizer`） |
| `Tokens/` | 计费算式（`UsageMath`）与用量累加器（`SessionTokenUsage`） |
| `Tools/` | 工具注册/元数据/检索引擎（`ToolMetadataRegistry`、`ToolArgumentExtractor`、`ToolRetrievalIndex`、`DisplayJsonSerializer`） |
| `Workflows/` | Workflow 定义、拓扑排序（`WorkflowTopology`）、幂等记录 |

根文件：`Constants.cs`（跨层共享常量，分层归置规则见 [AGENTS.md](../AGENTS.md) §10.2）、`ITokenEstimator.cs`。

### 内容构成规则

Core = 契约（接口 / 记录类型）+ 领域模型 + **无副作用的纯内存引擎**。带真实逻辑的引擎（权限分类器、快捷键解析、工具检索索引等）允许且应当放在 Core，前提是：纯内存运算、无环境副作用、所需 I/O 一律经接口外移（由 Infrastructure 实现并注册）。引擎居 Core 是消费方驱动的结构性必然——Infrastructure 中间件（如 `PermissionAndLimitMiddleware`）只能依赖 Core，权限与工具引擎必须在其可及范围内。判断某个类型是否该进 Core：消费方是否跨到 Infrastructure（是 → 必须在 Core）；否则纯逻辑可就近放在消费方所在层，不为对称性而搬家。

---

## 依赖约束（不可违反）

Core 是整个系统的契约层，其依赖纯洁性决定了架构的健康度。

### 允许的依赖

| 依赖 | 用途 | 理由 |
|------|------|------|
| BCL（`System.*`） | 基础类型 | 运行时内置 |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | DI 接口 | 仅接口定义 |
| `Microsoft.Extensions.Logging.Abstractions` | 日志接口 | 仅接口定义 |
| `Microsoft.Extensions.AI.Abstractions` | AI 抽象接口 | 仅接口定义 |

### Cron 依赖必须经 Core 契约

`Cronos` **不属于 Core 允许的依赖**，必须通过接口下沉到 Automation：

- Cron 解析：`ICronParser`（Core 契约） + `CronosCronParser`（Automation 实现）

`CronExpressionHelper` 是基于 `ICronParser` 的纯字符串辅助类，不得引用 Cronos。

### 禁止的依赖

| 禁止项 | 原因 | 正确位置 |
|--------|------|---------|
| `Serilog` / `NLog` 等日志实现 | 具体实现，客户端单机程序用 `Microsoft.Extensions.Logging` 即可 | Infrastructure（注：当前项目已移除 Serilog 依赖） |
| `YamlDotNet` | 具体实现 | Infrastructure |
| `Microsoft.Extensions.Http` | 具体实现 | Infrastructure |
| `Terminal.Gui` | UI 框架 | App |
| `ModelContextProtocol` | 协议实现 | Infrastructure |
| `Microsoft.ML.Tokenizers` | 计算实现 | Infrastructure |
| 任何 `OneCode.*` 项目 | 反向依赖 | -- |
| BCL 副作用 I/O（`File.` / `Directory.` / `Process.` 的创建/删除/探测、`Environment.GetFolderPath`、`Console.*`） | Core 必须无环境副作用（无副作用的内存解析不受限，如 `ModelCatalog.LoadFromStream` 吃 Stream 入参） | 外移接口实现（Infrastructure）或由 App 调用方执行 |

### 新增依赖的评审流程

在 Core 中引入新依赖前，必须回答：

1. **这是接口/抽象还是具体实现？** → 只有接口/抽象允许
2. **它是否属于 `MS.Extensions.*.Abstractions` 范畴？** → 优先选择官方 Abstractions
3. **能否在 Core 中定义接口、在 Infrastructure 中引用实现？** → 优先此方案
4. **是否有 3 个以上项目需要此依赖？** → 否则考虑下沉

---

## 接口定义规范

Core 中的接口是全系统的契约，修改需考虑向后兼容。

### 命名与文件

- 接口命名：`IXxx`，文件命名：`IXxx.cs`
- 命名空间：`OneCode.{模块}`（如 `OneCode.Permissions`、`OneCode.Tools`）
- 一个文件一个主接口（可包含密切相关的辅助类型）

### 接口设计原则

```csharp
// ✅ 正确：小接口，单一职责
public interface IToolMetadata
{
    string Name { get; }
    ToolRisk Risk { get; }
}

// ✅ 正确：按能力拆分的小接口
public interface IToolClassification
{
    ToolRisk AssessRisk(JsonElement input);
}

// ❌ 禁止新增：上帝接口
public interface IMyFeature : IMyFeatureCore, IMyFeatureValidation, IMyFeaturePermission
{
    // 新接口不应聚合所有能力，消费者按需依赖小接口。
}
```

### 接口稳定性

- **公共接口**（`public`）：开发期可直接修改签名，不保留 `[Obsolete]` 过渡期
- **内部接口**（`internal`）：可自由修改，但需更新 `InternalsVisibleTo` 项目
- **新增方法**：优先提供默认实现（C# 8 接口默认方法）或扩展方法，避免破坏现有实现

---

## Result\<T> 类型

`Result<T>` 是 Core 中定义的操作结果类型，位于 `OneCode.Core.Results` 命名空间。

```csharp
// 用法（非 BCL 内置，是自定义类型）
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

// 同步用法（仅用于 CPU 密集型非 I/O 操作）
public Result<int> ParseNumber(string input)
{
    return int.TryParse(input, out var value)
        ? Result.Success(value)
        : Result.Failure("Invalid number format");
}
```

- 用于预期失败（文件不存在、验证失败等）
- 异常用于意外/不可恢复情况
- I/O 操作必须使用 async 版本，不得同步阻塞
- 详见 `OneCode.Core/Results/Result.cs`

---

## 构建与测试

```bash
# 构建 Core 项目
dotnet build src/OneCode.Core/OneCode.Core.csproj

# 运行 Core 相关测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~PermissionCheckerTests"

# 运行所有 Core 命名空间下的测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~OneCode.Core"
```
