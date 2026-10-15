# 记忆模块架构设计

**状态**: Accepted
**日期**: 2026-07-17
**关联**: [memory-overview.md](../memory-overview.md)、[background-services.md](../background-services.md#5-autodream-记忆整合)、
[MAF 集成边界与禁止清单](./0007-maf-integration-boundaries.md)

## 语境

OneCode 需要记忆子系统让 Agent 跨会话、跨项目地积累与复用知识。早期版本存在二元割裂：
- 用户手动记忆存 Markdown 文件，AutoDream 提取的记忆存 KV JSON 文件
- 两套存储抽象、两套路径、两套注入逻辑

本文档记录统一后的轻量结构化记忆架构与 AutoDream 任务级增量整合机制。

## 决策

### 1. 统一结构化条目存储（保持轻量 MEMORY.md，不引入 SQLite）

**决策**：所有结构化记忆（用户手动 `/memory add` 与 AutoDream 自动整合）统一存入 `MEMORY.md`，由 `IMemoryEntryStore` 抽象，不引入 SQLite 或外部检索索引。

**理由**：
- 单用户 CLI 在单作用域 `MaxEntries = 50` 与总注入预算 `8,000` 字符的硬约束下，`MEMORY.md` 纯文本分段存储足够轻量、可读且支持用户直接编辑，无需承担 SQLite 依赖与索引同步成本
- 消除"文件记忆"与"机器记忆"的二元割裂，用户与 AutoDream 共享同一存储

**抽象接口**：

```csharp
public interface IMemoryEntryStore
{
    Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryScope scope, CancellationToken ct = default);      // 过滤过期
    Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(MemoryScope scope, CancellationToken ct = default);   // 含过期（管理命令与保护校验用）
    Task UpsertAsync(MemoryScope scope, IEnumerable<MemoryEntry> entries, CancellationToken ct = default);
    Task<bool> RemoveAsync(MemoryScope scope, string key, CancellationToken ct = default);
    Task ClearAsync(MemoryScope scope, CancellationToken ct = default);
    Task<int> PruneAsync(MemoryScope scope, CancellationToken ct = default);   // 清理过期 + 超 50 条按时间淘汰（manual 豁免）
}
```

`MemoryScope` 枚举：`User`（全局，`~/.onecode/memory/`）/ `Project`（当前工作目录，`{cwd}/.onecode/memory/`）。作用域由调用方显式指定。

### 2. MEMORY.md 文件格式与数据模型

**数据模型**：

```csharp
public sealed record MemoryEntry
{
    public required string Key { get; init; }          // {category}:{short-id}
    public required string Value { get; init; }        // 记忆正文（可多行）
    public required string Source { get; init; }       // "manual" | "autodream"
    public required string Category { get; init; }     // manual/fact/convention/lesson/correction
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }    // null = 永不过期（manual 条目）
    public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow > ExpiresAt.Value;

    public static string DeriveCategory(string key);   // 从 key 前缀推导
}
```

**Key 格式约定**：`{category}:{short-id}`，如 `fact:build-command`、`manual:build-slnx-command-10142130`。Key 是稳定身份标识——重复 upsert 同一 Key 覆盖原值（保留原始 `CreatedAt`），Key 大小写不敏感（`OrdinalIgnoreCase`）。

**文件格式**（每个作用域一个 `MEMORY.md`）：

```markdown
---
last_updated: 2026-10-14T10:00:00Z
entry_count: 2
---

## manual:oauth-dpapi-10140900

- source: manual
- category: manual
- created_at: 2026-10-14T09:00:00Z
- updated_at: 2026-10-14T09:00:00Z

本项目所有 OAuth 凭据必须使用 DPAPI 加密存储。

## fact:build-command

- source: autodream
- category: fact
- created_at: 2026-10-13T10:00:00Z
- updated_at: 2026-10-14T10:00:00Z
- expires_at: 2026-11-13T10:00:00Z

Build with `dotnet build src/OneCode.slnx`.
```

**序列化顺序**：`Source == "manual"` 优先，然后按 `UpdatedAt` 降序。手动记忆排在文件顶部，便于人工查阅。

**解析容错与手写列表保护**：`MemoryEntryStore.ParseEntry` 仅当 `- key: value` 的 `key` 属于已知元数据白名单（`source`、`category`、`created_at`、`updated_at`、`expires_at` 及兼容旧文件的 `hit_count`、`last_hit_at`）时才作为元数据消费；一旦遇到非元数据行即进入正文区，防止用户在 `MEMORY.md` 手写无元数据头的 Markdown 列表时被误判为属性并丢弃。

### 3. 文件实现特性与防爆炸三道防线

| 特性 | 实现方式 |
|------|---------|
| 目录解析 | `User` → `~/.onecode/memory/`；`Project` → `{cwd}/.onecode/memory/`（运行时通过 `IWorkingDirectoryAccessor` 实时读取，`/cd` 切换生效） |
| 并发控制 | 跨进程锁文件 + 原子替换（`DurableFile`），写操作串行化；读操作无锁 |
| 过期处理 | `AutoDreamMemoryApplier` 对未指定 `ttlHours` 的自动记忆默认赋予 `720h`（30 天）；`LoadAsync` 惰性过滤 `IsExpired`；`PruneAsync` 物理删除 |
| 容量治理 | 单作用域硬上限 `MaxEntries = 50`；超出时 `manual` 豁免，其余按 `UpdatedAt` 升序淘汰最旧条目 |
| 读取预算 | `MemoryService.LoadMemoryPromptAsync` 设总字符预算 `MaxMemoryContextChars = 8_000`（约 2,000 Tokens），按 `manual` 优先 → `Project` 优先于 `User` → `UpdatedAt` 降序截断兜底 |

### 4. 单一读取收口：`MemoryContextProvider` 全文注入

**决策**：所有长期记忆读取收口到单一的 `MemoryContextProvider`（继承 `ReadOnlyAIContextProviderBase`）。

- **注入方式**：`MemoryContextProvider` 在每轮请求时调用 `IMemoryService.LoadMemoryPromptAsync`，通过 `AIContext.Instructions` 注入有效记忆的**完整正文**（剥离 `source` / `updated_at` / `expires_at` 等内部管理属性，且不新增额外 System Message 块以免占用 Anthropic `cache_control` 断点预算）。
- **统一挂载**：在 `SharedContextProviderBuilder`（主会话与子代理）及 `AutoDreamService.BuildConsolidationAgentOptions`（AutoDream 整合代理）中统一挂载 `MemoryContextProvider`。

### 5. 用户手写条目（`manual`）的保护边界

`manual` 条目表达**用户显式意图**，具有不可跨越的保护边界：

| 路径 | 保护方式 | 位置 |
|---|---|---|
| 容量淘汰 | `PruneAsync` 对 `Source == "manual"` **豁免**，永不被自动淘汰 | `MemoryEntryStore.PruneAsync` |
| AutoDream 写入 | 拒绝 delete / upsert / supersede 用户手写条目；拒绝创建 `manual:` 前缀的 key | `AutoDreamMemoryApplier.ApplyConsolidationChangesAsync` |

### 6. AutoDream 任务完成触发与当前对话增量整理

**唯一自动触发点**：仅当 `QueryStreamEngine.FinalizeAsync` 中 `request.WorkingMode == WorkingMode.Build` 且任务正常完成未回滚（`outcome.Reason == RunTerminalReason.Completed && !outcome.TransactionRolledBack`）时，调用 `IAutoDreamService.NotifyTaskCompleted()`。`PLAN` 与 `GOAL` 模式不触发记忆更新；用户仍可通过 `/memory autodream trigger` 手动强制触发。

**增量快检与写入链路**：

```text
BUILD 任务正常完成 → QueryStreamEngine.FinalizeAsync()
                  → IAutoDreamService.NotifyTaskCompleted()
                  → C# 零 Token 增量快检（working-memory 新笔记 或 当前内存会话自 last_consolidated_at 以来有新对话轮次）
                  → AutoDreamConsolidationPromptBuilder 提取 working-memory 快照与当前对话 User/Assistant 轮次注入 prompt
                  → AutoDreamService.RunConsolidationAgentAsync()（单轮无工具调用，挂载 MemoryContextProvider 注入既有 MEMORY.md）
                  → LLM 输出 JSON 数组 [{action, scope, key, value, ttlHours, supersedes}, ...]
                  → AutoDreamMemoryApplier 校验、清洗、应用 upsert / supersedes / delete
                  → PruneAsync(scope) 清理过期 + 超 50 条按时间淘汰
                  → 仅当 JSON 数组合法解析并应用成功后更新 last_consolidated_at
```

### 7. 决策：不实现独立团队记忆子系统

不为多 Agent 协作引入独立的团队记忆子系统：
- **共享知识库 = project 级 `MEMORY.md`**。主会话与子代理经 `SharedContextProviderBuilder` 挂载的 `MemoryContextProvider` 共享读取同一份 `Project` + `User` 记忆。
- **写入统一收口**：AutoDream / `/memory` 命令 / `IMemoryEntryStore`，子代理不直接写。

### 8. 不使用 MAF `FileMemoryProvider` 替换长期记忆

本结论只针对长期记忆（`MEMORY.md`）。会话工作记忆由原生 `FileMemoryProvider` 承载（落在 `{cwd}/.onecode/agent-file-memory/`，作为 AutoDream 的首选候选输入）。长期记忆不由 LLM 调工具直写，而是经 AutoDream 清洗管线与 `IMemoryEntryStore` 维护 TTL 与 50 条配额。

### 9. DI 注册与 ContextProvider 装配

`src/OneCode.App/Services/Memory/MemoryServiceCollectionExtensions.cs`（`AddMemoryServices`）：

```csharp
services.AddSingleton<IMemoryEntryStore>(sp => new MemoryEntryStore(
    sp.GetRequiredService<IWorkingDirectoryAccessor>(),
    sp.GetRequiredService<ILogger<MemoryEntryStore>>()));

services.AddSingleton<MemoryService>();
services.AddSingleton<IMemoryService>(sp => sp.GetRequiredService<MemoryService>());
```

`src/OneCode.App/Services/AutoDream/AutoDreamServiceCollectionExtensions.cs`（`AddAutoDreamServices`）：

```csharp
services.AddSingleton<AutoDreamAgentDependencies>();
services.AddSingleton<AutoDreamStorageDependencies>();
services.AddSingleton<AutoDreamService>();
services.AddSingleton<IAutoDreamService>(sp => sp.GetRequiredService<AutoDreamService>());
services.AddHostedService(sp => sp.GetRequiredService<AutoDreamService>());
```

共享 ContextProvider 由 `SharedContextProviderBuilder.BuildCommon(profile, options)` 按 `AgentCapability` 枚举装配：

| 顺序 | Provider | 能力（`AgentCapability`） |
|------|---------|------|
| 1 | MAF `AgentSkillsProvider`（`SkillProviderFactory` 构建） | `Skills` |
| 2 | `MemoryContextProvider` | `MemorySearch` |
| 3 | `DesignContextProvider` | `DesignContext` |
| 4 | `LspDiagnosticContextProvider` | `LspDiagnostics` |
| 5 | `ShellEnvironmentProvider` | `ShellEnvironment`（且前台会话存在 shell executor） |
| 6 | `CodeActProvider` | `CodeAct` |
