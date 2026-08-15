# ADR 0004: 记忆模块架构设计

**状态**: Accepted
**日期**: 2026-07-17（§8 决策补充于 2026-08-15）
**关联**: [memory-overview.md](../memory-overview.md)、[background-services.md §5](../background-services.md#5-autodream-记忆整合)

## 语境

OneCode 需要记忆子系统让 Agent 跨会话、跨项目、跨团队积累复用知识。早期版本包含 4 个子系统：持久化文件记忆（Memdir）、会话记忆、键值记忆存储（KV Store）、团队记忆。

实践中发现 KV Store 与文件记忆存在二元割裂：
- 用户手动记忆存 Markdown 文件，AutoDream 提取的记忆存 KV JSON 文件
- 两套存储抽象（`IMemoryStore` vs Memdir 扫描）、两套路径（`memory/` vs `memory-store/`）、两套注入逻辑
- 维护成本高，且"文件记忆只读"的约束导致 AutoDream 无法直接丰富用户知识库

本 ADR 记录重构后的架构决策与实现细节。

## 决策

### 1. 统一结构化条目存储

**决策**：移除 KV Store 子系统（`IMemoryStore` / `FileSystemMemoryStore` / `PathAwareMemoryStore`），所有结构化记忆统一存入 `MEMORY.md`，由 `IMemoryEntryStore` 抽象。

**理由**：
- 消除"文件记忆"与"机器记忆"的二元割裂，用户与 AutoDream 共享同一存储
- `IMemoryEntryStore` 基于 `MemoryScope` 枚举操作，调用方不感知物理路径，未来可无缝替换为 SQLite
- AutoDream 可直接丰富用户知识库，无需用户手动迁移

**抽象接口**：

```csharp
public interface IMemoryEntryStore
{
    Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryScope scope, CancellationToken ct = default);      // 过滤过期
    Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(MemoryScope scope, CancellationToken ct = default);   // 含过期（管理命令用）
    Task UpsertAsync(MemoryScope scope, IEnumerable<MemoryEntry> entries, CancellationToken ct = default);
    Task<bool> RemoveAsync(MemoryScope scope, string key, CancellationToken ct = default);
    Task ClearAsync(MemoryScope scope, CancellationToken ct = default);
    Task<int> PruneAsync(MemoryScope scope, CancellationToken ct = default);   // 清理过期 + LRU 淘汰
}
```

`MemoryScope` 枚举：`User`（全局，`~/.onecode/memory/`）/ `Project`（当前工作目录，`{cwd}/.onecode/memory/`）。作用域由调用方显式指定，不从 entry key 推导。

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
    public DateTimeOffset? ExpiresAt { get; init; }    // null = 永不过期
    public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow > ExpiresAt.Value;

    public static string DeriveCategory(string key);   // 从 key 前缀推导
}
```

**Key 格式约定**：`{category}:{short-id}`，如 `fact:build-command`、`manual:oauth-dpapi`。Key 是稳定身份标识——重复 upsert 同一 Key 覆盖原值（保留原始 `CreatedAt`），Key 大小写不敏感（`OrdinalIgnoreCase`）。

**文件格式**（每个作用域一个 `MEMORY.md`）：

```markdown
---
last_updated: 2024-07-16T10:00:00Z
entry_count: 2
---

## fact:build-command

- source: autodream
- category: fact
- created_at: 2024-07-15T10:00:00Z
- updated_at: 2024-07-16T10:00:00Z
- expires_at: 2024-10-14T10:00:00Z

Build with `dotnet build src/OneCode.sln`. Typical duration ~45s.

## manual:oauth-dpapi

- source: manual
- category: manual
- created_at: 2024-07-16T09:00:00Z
- updated_at: 2024-07-16T09:00:00Z

本项目所有 OAuth 凭据必须使用 DPAPI 加密存储。
```

**序列化顺序**：`Source == "manual"` 优先，然后按 `UpdatedAt` 降序。手动记忆排在文件顶部，便于人工查阅。

**解析容错**：缺失 frontmatter 或部分条目损坏时跳过并继续解析剩余条目。条目通过 `^##\s+(.+)$` 正则识别 header 边界。

### 3. 文件实现特性

| 特性 | 实现方式 |
|------|---------|
| 目录解析 | `User` → `~/.onecode/memory/`；`Project` → `{cwd}/.onecode/memory/`（运行时通过 `IWorkingDirectoryAccessor` 实时读取，`/cd` 切换生效） |
| 并发控制 | 每个目录一把 `SemaphoreSlim`（`ConcurrentDictionary<string, SemaphoreSlim>` 缓存），写操作串行化；读操作无锁 |
| 原子写入 | 写入 `.tmp` 临时文件 → `File.Replace`（已存在）或 `File.Move`（新建），读者永远不会看到半写状态 |
| 过期处理 | 惰性清理：`LoadAsync` 过滤 `IsExpired`；`PruneAsync` 物理删除 |
| 容量治理 | `MaxEntries = 200`，超出时按 `UpdatedAt` 升序 LRU 淘汰 |
| 摘要上限 | `MaxAutoRecalledInSummary = 8`，prompt 中 auto 条目摘要最多展示 8 条 |

### 4. 相关性检索评分算法

`MemoryService.FindRelevantMemoriesAsync` 供 `search_memories` 工具与 prompt 注入共用。

**Query 分词**：正则 `[\p{L}\p{N}_-]{2,}` 提取 token（≥ 2 字符），过滤中英文停用词（`the`/`and`/`继续`/`实现`/`需要` 等）。

**评分**（`MemoryService.Score`）：

| 维度 | 加分 |
|------|------|
| Key 包含 token | +6 分/次 |
| Value 包含 token | +3 分/次（单 token 上限 5 次 = +15） |
| 作用域加成 | 项目级 +2，用户级 +1（项目级优先） |
| 来源加成 | `Source == "manual"` +2（用户手动记忆权重更高） |

**选取**：评分 > 0 的条目按分数降序 → `UpdatedAt` 降序，最多取 6 条（`MaxRelevantMemories`）。

> **设计决策**：不使用"年龄桶加成"。评分应反映"相关性"而非"新旧"，且 `UpdatedAt` 降序作为次要排序键已隐含新鲜度偏好。

### 5. System Prompt 注入策略

`MemoryService.LoadMemoryPromptAsync(cwd, query)` 构建注入段落，由 `PromptConfigBuilder` 填入 `{{memory_section}}` 占位符。

| 注入方式 | 触发时机 | 内容 |
|---------|---------|------|
| 摘要索引常驻 | 每次构建 system prompt | 全部 manual 条目 + auto 条目前 8 条，每条 Key + Value 首行（截断 80 字符） |
| Query 相关性注入 | `query` 非空时 | 评分 Top 6 条目，Value 截断 500 字符，附作用域标签 |
| 按需检索 | LLM 调用 `search_memories` 工具 | 评分 Top 6 条目，返回完整 Value |

段落结构：

```markdown
## Memory

Memories are stored per-scope: user-level (global) and project-level (current working directory).

### User memories
- `manual:oauth-dpapi` — 本项目所有 OAuth 凭据必须使用 DPAPI 加密存储。

### Auto-recalled memories
- `[fact]` Build with `dotnet build src/OneCode.sln`. Typical duration ~45s.
- ... and 3 more (use search_memories tool to retrieve)

### Relevant memories for this request
#### fact:build-command (project)
Build with `dotnet build src/OneCode.sln`. Typical duration ~45s.

_Use the `search_memories` tool to retrieve full memory content._
```

**设计决策**：
- 摘要索引常驻保证 LLM 知道"有什么记忆可用"，无需为获取目录额外调用工具
- 构建 prompt 时若已知 query，直接拼入 Top 6 相关条目（截断 500 字符），避免 LLM 为基础上下文反复调用 `search_memories`
- `search_memories` 作为 LLM 主动深入检索的补充渠道，返回完整 Value

### 6. 会话记忆双向 Provider

`SessionMemoryContextProvider` 继承 MAF `AIContextProvider`，实现 Provide（注入）+ Store（提取）双向交互。

#### 6.1 注入（Provide）

每次 LLM 调用前：按 `Importance` 降序 → `UpdatedAt` 降序取 Top 5 条（`MaxInjectedMemories`），拼接为 `## Session memories` 系统消息。

#### 6.2 提取（Store）四重节流

| 节流条件 | 阈值常量 | 说明 |
|---------|---------|------|
| 最小消息数 | `MinMessagesForExtraction = 4` | 会话过短不提取 |
| 消息数增量 | ≥ 2 条（自上次提取后） | 无新消息则跳过；首次提取自动放行 |
| 轮次间隔 | `MinTurnsBetweenExtractions = 5` | 避免每轮都提取 |
| Token 增量 | `MinTokensBeforeExtraction = 2000` | 内容无明显增长则跳过 |

节流计数器通过 `ProviderSessionState<SessionMemoryState>` 持久化：`LastExtractedMessageCount` / `LastExtractedTurnCount` / `TotalTokenEstimate`。

#### 6.3 提取流程

1. 节流通过 → 构建最近 16 条消息 transcript（`TakeLast(16)`）
2. 调用 fast model（`MaxOutputTokens=384`）生成摘要，最多取 6 条
3. LLM 失败或返回空时回退到 `ExtractKeyFacts` 启发式
4. 归一化 → 去重 → 合并写入（`MergeExtractedMemoriesAsync`）

#### 6.4 启发式提取（兜底）

`ExtractKeyFacts` 扫描最近 24 条消息（`TakeLast(24)`），按句子切分（中英文标点 `.!?。！？；;` + 换行），保留满足以下任一条件的句子：
- 含偏好信号词：`prefer`/`always`/`never`/`remember`/`important`/`deadline`/`must`/`should`/`use`/`don't`/`do not`/`avoid`/`priority`/`偏好`/`记住`/`不要`/`必须`/`优先`/`截止`
- 用户消息且长度 ≥ 24 字符

约束：长度 12~220 字符，过滤以 `/` 开头的命令。

### 7. AutoDream 写入链路

```text
会话历史  →  AutoDreamService.RunConsolidationAgentAsync()
         →  LLM 输出 JSON 数组 [{action, scope, key, value, ttlHours}, ...]
         →  ApplyConsolidationChangesAsync() 解析
         →  SanitizeKey / SanitizeValue 清洗（防 MEMORY.md 结构注入）
         →  IMemoryEntryStore.UpsertAsync(scope, entries)
         →  MemoryEntryStore 写入对应 MEMORY.md（temp + 原子替换）
         →  PruneAsync(scope) 清理过期 + LRU 淘汰（上限 200）
```

| 维度 | 实现 |
|------|------|
| 写入目标 | 直接写入 `MEMORY.md`（与用户手动记忆共享文件） |
| 作用域 | Agent 输出 `scope` 字段决定：`user` / `project`；其他值跳过 |
| 操作类型 | `upsert`（默认）/ `delete`；其他未知 action 跳过 |
| 配额限制 | 单次整合最多 50 条（`MaxChangesPerConsolidation`），超出截断 |
| 输入清洗 | Key：换行→空格、移除前导 `#`、长度 ≤ 100；Value：行首 `## ` → `# # `（防 entry header 注入）、长度 ≤ 10,000 |
| 不写 AGENTS.md | 工程规范文件由人工维护，自动改写会污染规范 |
| 状态文件 | `{cwd}/.onecode/memory/` 下：`autodream.lock`（跨进程锁）、`last_consolidated_at`、`last_session_scan_at` |

**结构注入防护**：AutoDream 的 Agent 输出为不可信内容。若不清洗，Value 中 `## ` 开头的行会被 `MemoryEntryStore.ParseEntries` 的 `^##\s+(.+)$` 正则误识别为新的 entry header，导致条目边界错乱、内容串入相邻条目。`SanitizeValue` 将行首 `## ` 替换为 `# # ` 破坏 header 模式，保留可读性。

**门控**：默认开启，距上次整合 ≥ 6 小时（`autodream.minHours`）+ 新会话数 ≥ 3 个（`autodream.minSessions`）。`ONECODE_AUTODREAM=false` 或 `ONECODE_REMOTE=true` 时关闭。

### 8. 决策：不实现独立团队记忆子系统

**决策**（2026-08-15 确立）：不为 Team 模式的多 Agent 团队引入独立的团队记忆子系统（早期迁移规划中的 `team-memory/` 目录 + frontmatter 解析器 + 专用 `TeamMemoryContextProvider` 设计不再实施）。Team 共享记忆需求由现有机制覆盖：

- **共享知识库 = project 级 `MEMORY.md`**。Team 成员经 `SharedContextProviderBuilder.BuildCommon` 无条件获得 `MemoryFileContextProvider`（`search_memories` 工具），可检索 project + user 级条目——`PipelineProfile.TeamMember` 关闭的仅是 `SessionMemory`（主会话私有事实）与 `CodeAct`，MEMORY.md 检索通道始终开放。
- **写入统一收口**：AutoDream / `/memory` 命令 / `IMemoryEntryStore`，成员 Agent 不直接写（防结构注入安全边界，见「影响」节）。
- **会话内成员协作**（如 reviewer 结论传递给 implementer）由编排机制承担（GroupChat 共享对话流 / Magentic 经 orchestrator 中转），不属于记忆职责。

**理由**：
1. 与本 ADR §1 核心决策（消除多套存储抽象，统一 `MEMORY.md`）一致——引入第三套存储属于方向回退
2. OneCode 是单用户 CLI，"Agent 团队"是同进程同工作目录的子 Agent；独立目录 + 环境变量路由的"团队"语义没有真实落点
3. 独立 md 目录 + 自有 frontmatter 解析器 + 截断保护的维护成本，相对"成员多读一份 MEMORY.md"没有增量价值

已知小缺口（已于 2026-08-15 补齐）：Team 成员的 system prompt 经 `ComposeWithRoleAsync`（harness + role）合成，不含主会话的 `{{memory_section}}` 摘要索引——现由 `PromptComposer.ComposeWithRoleAsync` 在 role prompt 末尾追加一行 `search_memories` 引导（Team 成员与 Explore/Plan fork 子代理均生效），无需架构变更。

> 历史设计稿（目录解析 / MemdirFrontmatterParser / 截断保护 / 专用 Provider 注入）见 git history 本文件 2026-08-15 之前的版本。

### 9. DI 注册

`ServiceCollectionExtensions.Memory.cs`（`RegisterMemoryServices`）：

```csharp
services.AddSingleton<IMemoryEntryStore>(sp => new MemoryEntryStore(
    sp.GetRequiredService<IWorkingDirectoryAccessor>(),
    sp.GetRequiredService<ILogger<MemoryEntryStore>>()));
services.AddSingleton<MemoryService>();
services.AddSingleton<IMemoryService>(sp => sp.GetRequiredService<MemoryService>());
services.AddSingleton<SessionMemoryService>();
services.AddSingleton<ISessionMemoryService>(sp => sp.GetRequiredService<SessionMemoryService>());

// 同一方法内还注册了压缩子系统服务（CompactSessionDependencies / CompactService /
// AutoCompactService / ReviewCacheService / CompactPromptBuilder / CompactApplier），
// 见 compact-thresholds.md
```

`ServiceCollectionExtensions.Advanced.cs`：

```csharp
services.AddSingleton<AutoDreamService>();
services.AddHostedService(sp => sp.GetRequiredService<AutoDreamService>());
```

MAF `AIContextProvider` 实例不注册到 DI——它们在 Agent Runner 构建管线时按需创建（依赖每次调用的 workingDirectory / conversation）。

### 10. ContextProvider 装配

`SharedContextProviderBuilder.ApplyProfileDefaults` 按 `PipelineProfile` 应用默认开关（`Worker`/`Explore`/`Plan` 关闭 LSP 诊断与 Shell 环境；`TeamMember` 关闭会话记忆与 CodeAct），随后 `BuildCommon(options)` 按以下顺序构建所有 Agent 共享的 ContextProvider：

| 顺序 | Provider | 开关 |
|------|---------|------|
| 1 | MAF `AgentSkillsProvider` | `skillProviderHolder.Current != null` |
| 2 | `MemoryFileContextProvider` | 始终添加 |
| 3 | `SessionMemoryContextProvider` | `IncludeSessionMemory` |
| 4 | `DesignContextProvider` | 始终添加 |
| 5 | `LspDiagnosticContextProvider` | `IncludeLspDiagnostics` |
| 6 | `TaskContextProvider`（运行时注入） | 始终添加 |
| 7 | `ShellEnvironmentProvider` | `IncludeShellEnvironment` 且前台会话存在 shell executor |
| 8 | `CodeActProvider` | `IncludeCodeAct` |

> Team 子 Agent 专用 Provider（如 `TeamSystemPromptProvider`）由 `TeamAgentFactory` 在装配时追加。MAF `AIContextProvider` 实例不注册到 DI——它们在 Agent Runner 构建管线时按需创建。

## 影响

- **存储模型简化**：消除 `memory-store/` 目录与 `IMemoryStore` 抽象，结构化记忆统一到 `MEMORY.md`——子系统收敛为 2 个（结构化条目记忆 + 会话记忆）；团队共享记忆不设独立子系统，由 project 级 `MEMORY.md` 覆盖（§8）
- **后端可替换**：`IMemoryEntryStore` 基于 `MemoryScope` 操作，未来可无缝替换为 SQLite 等后端，无需修改 `MemoryService` / `AutoDreamService` / `MemoryCommand`
- **自动积累闭环**：AutoDream 直接写入 `MEMORY.md`，用户知识库可持续自动丰富，无需手动迁移
- **安全边界明确**：Agent 不通过工具直接写 `MEMORY.md`，所有程序化写入经 `IMemoryEntryStore` 或 AutoDream 清洗管线，防止结构注入
- **并发安全**：per-directory `SemaphoreSlim` 保护进程内并发；AutoDream 的 `autodream.lock`（`FileStream` + `FileShare.None`）保护跨进程并发，僵尸锁（超 2 小时）可安全抢占

## 扩展指南

### 新增存储后端

实现 `IMemoryEntryStore`，在 `ServiceCollectionExtensions.Memory.cs` 替换注册即可，调用方零修改。

### 新增记忆类别

在 AutoDream 整合 prompt（`prompts/system/autodream-consolidation.prompt`）中引导 Agent 输出新 category 的 Key，`MemoryEntry.DeriveCategory` 自动从 Key 前缀推导，无需改代码。
