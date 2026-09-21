# 记忆模块架构设计

**状态**: Accepted
**日期**: 2026-07-17（§8 决策补充于 2026-08-15；§11 治理与演进补充于 2026-09-16）
**关联**: [memory-overview.md](../memory-overview.md)、[background-services.md §5](../background-services.md#5-autodream-记忆整合)、
[MAF 集成边界与禁止清单](./0007-maf-integration-boundaries.md)

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
    Task RecordHitsAsync(MemoryScope scope, IReadOnlyList<string> keys, CancellationToken ct = default); // 使用反馈（仅写 HitCount/LastHitAt）
    Task ClearAsync(MemoryScope scope, CancellationToken ct = default);
    Task<int> PruneAsync(MemoryScope scope, CancellationToken ct = default);   // 清理过期 + 按价值淘汰
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
    public DateTimeOffset UpdatedAt { get; init; }     // 仅内容变更时更新，使用反馈不会改它
    public DateTimeOffset? ExpiresAt { get; init; }    // null = 永不过期
    public int HitCount { get; init; }                 // 被 search_memories 命中的次数（缺席旧文件读作 0）
    public DateTimeOffset? LastHitAt { get; init; }    // 最近一次命中时刻
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
- hit_count: 3
- last_hit_at: 2024-07-16T09:30:00Z

Build with `dotnet build src/OneCode.sln`. Typical duration ~45s.

## manual:oauth-dpapi

- source: manual
- category: manual
- created_at: 2024-07-16T09:00:00Z
- updated_at: 2024-07-16T09:00:00Z

本项目所有 OAuth 凭据必须使用 DPAPI 加密存储。
```

**序列化顺序**：`Source == "manual"` 优先，然后按 `UpdatedAt` 降序。手动记忆排在文件顶部，便于人工查阅。

**使用反馈字段**：`hit_count` / `last_hit_at` 由 `RecordHitsAsync` 写入、由 `PruneAsync` 读取。
从未命中时省略；旧文件缺失这两个字段时解析为 `0` / `null`（向后兼容）。

**解析容错**：缺失 frontmatter 或部分条目损坏时跳过并继续解析剩余条目。条目通过 `^##\s+(.+)$` 正则识别 header 边界。

### 3. 文件实现特性

| 特性 | 实现方式 |
|------|---------|
| 目录解析 | `User` → `~/.onecode/memory/`；`Project` → `{cwd}/.onecode/memory/`（运行时通过 `IWorkingDirectoryAccessor` 实时读取，`/cd` 切换生效） |
| 并发控制 | 每个目录一把 `SemaphoreSlim`（`ConcurrentDictionary<string, SemaphoreSlim>` 缓存），写操作串行化；读操作无锁 |
| 原子写入 | 写入 `.tmp` 临时文件 → `File.Replace`（已存在）或 `File.Move`（新建），读者永远不会看到半写状态 |
| 过期处理 | 惰性清理：`LoadAsync` 过滤 `IsExpired`；`PruneAsync` 物理删除 |
| 容量治理 | `MaxEntries = 200`；超出时按**使用价值**淘汰：`manual` 豁免 → `HitCount` 升序 → 同次数 `UpdatedAt` 最旧优先 |
| 使用反馈 | `search_memories` 命中时经 `RecordHitsAsync` 递增 `HitCount` / 记 `LastHitAt`（**不改 `UpdatedAt`**，否则高频条目会显得“新”而躲过时间 tie-break） |
| 摘要上限 | `MaxAutoRecalledInSummary = 8`，prompt 中 auto 条目摘要最多展示 8 条 |

### 4. 相关性检索评分算法

`MemoryService.FindRelevantMemoriesAsync` 供 `search_memories` 工具使用。

**Query 分词**：共用 `OneCode.Core/Text/TextTokenizer`——拉丁词按大小写边界切分并去复数后缀（`FindReferences` → `find` / `reference` / `references` / `findreferences`），CJK 按二字滑窗切分（单字保留 unigram）。再过滤 ≥ 2 字符与中英文停用词（`the`/`and`/`继续`/`实现`/`需要` 等）。

> **设计决策（2026-09-18 修订）**：旧实现用 `[\p{L}\p{N}_-]{2,}` 取连续串，但 `\p{L}` 匹配 CJK——中文整句会被切成**单个 token**，语料侧同样如此，导致任何中文查询召回**恒为空**。评测（`MemoryRecallEvaluationTests`）首轮即失败并暴露此缺陷。现与 `ToolRetrievalIndex` 共用同一分词器：两条检索路径规则不一致时，同一查询会在 ToolSearch 命中、在 `search_memories` 为空。

**评分**（`MemoryService.Score`）：

| 维度 | 加分 |
|------|------|
| Key 包含 token | +6 分/次 |
| Value 包含 token | +3 分/次（单 token 上限 5 次 = +15） |
| 作用域加成 | 项目级 +2，用户级 +1（项目级优先） |
| 来源加成 | `Source == "manual"` +2（用户手动记忆权重更高） |

**选取**：评分 > 0 的条目按分数降序 → `UpdatedAt` 降序，最多取 6 条（`MaxRelevantMemories`）。

**输出预算**：`MaxRelevantMemories = 6` 只限条数，6 条完整正文可任意大。`MaxSearchResultChars = 12_000` 限制单次检索总字符数，超出部分显式报告省略。常驻索引另有 `MaxIndexChars = 4_000`——索引**每轮**注入，条目数不等于 token 上限。

> **设计决策**：不使用"年龄桶加成"。评分应反映"相关性"而非"新旧"，且 `UpdatedAt` 降序作为次要排序键已隐含新鲜度偏好。

### 5. System Prompt 注入策略

`MemoryService.LoadMemoryPromptAsync(cwd)` 构建注入段落，由 `PromptConfigBuilder` 填入 `{{memory_section}}` 占位符。

| 注入方式 | 触发时机 | 内容 |
|---------|---------|------|
| 摘要索引常驻 | 每次构建 system prompt | 全部 manual 条目 + auto 条目前 8 条，每条 Key + Value 首行（截断 80 字符） |
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
- `search_memories` 返回完整 Value（Top 6），作为 LLM 主动深入检索的渠道

> 早期设计曾在构建 prompt 时按已知 `query` 拼入 Top 6 相关条目（`LoadMemoryPromptAsync` 的 `memoryQuery` 参数）。
> 实测两个调用点（`InteractiveBootstrapService` / `CronJobExecutor`）均传 `null`，该分支从未生效。
> 已移除（2026-09-15）。

### 5.1 用户手写条目（`manual`）的保护边界

`manual` 条目表达**用户显式意图**，因此有两条不可跨越的边界：

| 路径 | 保护方式 | 位置 |
|---|---|---|
| 容量淘汰 | `PruneAsync` 对 `Source == "manual"` **豁免**，永不被自动淘汰 | `MemoryEntryStore.PruneAsync` |
| AutoDream 写入 | 拒绝 delete / upsert 用户手写条目；拒绝创建 `manual:` 前缀的 key | `AutoDreamService.ApplyConsolidationChangesAsync` |

**为何写入路径也必须守**：AutoDream 的输出是**不可信内容**（LLM 生成）。在补上这三道闸之前，
一条幻觉的 `{"action":"delete","key":"manual:xxx"}` 就能抹掉用户手写记忆——与淘汰豁免的原则直接冲突。

**为何按 `Source` 而非仅按 key 前缀判定**：用户可以**直接编辑 `MEMORY.md`** 写入任意 key
（不限于 `manual:` 前缀），故前缀检查只是第一道闸，`Source` 检查是兜底。
两者分工：前缀闸防止 AutoDream **创建**保留分类；`Source` 闸防止它**删改**用户已有的条目。

**为何禁止 AutoDream 写 `manual:` 前缀**：`Category` 由 key 前缀推导
（`MemoryEntry.DeriveCategory`），而 AutoDream 写入的条目 `Source` 恒为 `"autodream"`。
若允许它创建 `manual:` 前缀，会出现 `Category=manual` 但 `Source=autodream` 的矛盾条目，
使分类语义与 `/memory` 展示错乱。

### 6. 会话记忆（已移除）

早期版本包含 `SessionMemoryContextProvider`（Provide + Store 双向 Provider）与 `SessionMemoryService`
（会话事实提取）。**该子系统已删除**，原因：

1. 语义与 `MEMORY.md` 结构化条目重叠（区别仅在生命周期）
2. 职责与压缩子系统（`App/Services/Compact/`）重叠（两者都从会话中提炼信息）
3. 实现不完整：`SessionMemoryEntry.Importance` / `ExpiresAt` 无写入者（排序为 no-op）；
   `MinTurnsBetweenExtractions` 严格支配「消息增量 ≥ 2」，后者不可达
4. 其"从会话中提炼信息"的能力由 AutoDream 承担

完整评估与理由见本节（§6）。

### 7. AutoDream 写入链路

```text
会话历史  →  AutoDreamService.RunConsolidationAgentAsync()
         →  LLM 输出 JSON 数组 [{action, scope, key, value, ttlHours}, ...]
         →  ApplyConsolidationChangesAsync() 解析
         →  SanitizeKey / SanitizeValue 清洗（防 MEMORY.md 结构注入）
         →  IMemoryEntryStore.UpsertAsync(scope, entries)
         →  MemoryEntryStore 写入对应 MEMORY.md（temp + 原子替换）
         →  PruneAsync(scope) 清理过期 + 按价值淘汰（上限 200）
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

- **共享知识库 = project 级 `MEMORY.md`**。Team 成员经 `SharedContextProviderBuilder.BuildCommon` 无条件获得 `MemorySearchProviderFactory` 创建的 `search_memories` 工具，可检索 project + user 级条目——`PipelineProfile.TeamMember` 关闭的仅是 `CodeAct`，MEMORY.md 检索通道始终开放。
- **写入统一收口**：AutoDream / `/memory` 命令 / `IMemoryEntryStore`，成员 Agent 不直接写（防结构注入安全边界，见「影响」节）。
- **会话内成员协作**（如 reviewer 结论传递给 implementer）由编排机制承担（GroupChat 共享对话流 / Magentic 经 orchestrator 中转），不属于记忆职责。

**理由**：
1. 与本 ADR §1 核心决策（消除多套存储抽象，统一 `MEMORY.md`）一致——引入第三套存储属于方向回退
2. OneCode 是单用户 CLI，"Agent 团队"是同进程同工作目录的子 Agent；独立目录 + 环境变量路由的"团队"语义没有真实落点
3. 独立 md 目录 + 自有 frontmatter 解析器 + 截断保护的维护成本，相对"成员多读一份 MEMORY.md"没有增量价值

已知小缺口（已于 2026-08-15 补齐）：Team 成员的 system prompt 经 `PromptComposer.GetHarnessAsync` + `PromptComposer.RenderRoleBody` 两段取得（合成由 MAF 负责），不含主会话的 `{{memory_section}}` 摘要索引——现由 `RenderRoleBody` 在 role prompt 末尾追加一行 `search_memories` 引导（Team 成员与 Explore/Plan fork 子代理均生效），无需架构变更。

> 历史设计稿（目录解析 / MemdirFrontmatterParser / 截断保护 / 专用 Provider 注入）见 git history 本文件 2026-08-15 之前的版本。

### 9. DI 注册

`src/OneCode.App/Services/Memory/MemoryServiceCollectionExtensions.cs`（`AddMemoryServices`）：

```csharp
services.AddSingleton<IMemoryEntryStore>(sp => new MemoryEntryStore(
    sp.GetRequiredService<IWorkingDirectoryAccessor>(),
    sp.GetRequiredService<ILogger<MemoryEntryStore>>()));

services.AddSingleton<MemoryService>();
services.AddSingleton<IMemoryService>(sp => sp.GetRequiredService<MemoryService>());
```

AutoDream 服务在 `src/OneCode.App/Services/AutoDream/AutoDreamServiceCollectionExtensions.cs`（`AddAutoDreamServices`）：

```csharp
services.AddSingleton<AutoDreamAgentDependencies>();
services.AddSingleton<AutoDreamStorageDependencies>();
services.AddSingleton<AutoDreamService>();
services.AddHostedService(sp => sp.GetRequiredService<AutoDreamService>());
```

> 压缩子系统（`CompactService` / `AutoCompactService` / `CompactPromptBuilder` 等）在独立的
> `Services/Compact/CompactServiceCollectionExtensions.cs`（`AddCompactServices`），**不**在 `AddMemoryServices` 内，
> 见 [compact-thresholds.md](../../docs/compact-thresholds.md)。
> 按启动批次分桶的 `ServiceCollectionExtensions.*.cs` partial 类均已解散，由
> `src/OneCode.Tests/RegistrationOwnershipTests.cs` 守卫禁止复活。

> MAF `AIContextProvider` 实例不注册到 DI——它们在 Agent Runner 构建管线时按需创建（依赖每次调用的 workingDirectory / conversation）。

### 10. ContextProvider 装配

共享 ContextProvider 由 `SharedContextProviderBuilder.BuildCommon(profile, options)` 构建：该方法遍历
`AgentCapability` 枚举，按 `PipelineProfileBehavior.For(profile)` 的能力集合逐项调用注册表工厂。
**哪些 provider 属于哪个 profile 只由能力集合决定**（不再有"profile → bool 开关"的中间层）。

| 顺序 | Provider | 能力（`AgentCapability`） |
|------|---------|------|
| 1 | MAF `AgentSkillsProvider`（`SkillProviderFactory` 构建） | `Skills` |
| 2 | `search_memories`（`MemorySearchProviderFactory` → MAF `TextSearchProvider`） | `MemorySearch` |
| 3 | Harness `FileMemoryProvider`（会话工作记忆，按 profile 启用） | `FileMemory` |
| 4 | `DesignContextProvider` | `DesignContext` |
| 5 | `LspDiagnosticContextProvider` | `LspDiagnostics` |
| 6 | `ShellEnvironmentProvider` | `ShellEnvironment`（且前台会话存在 shell executor） |
| 7 | `CodeActProvider` | `CodeAct` |

> 注入顺序 = `AgentCapability` 枚举声明顺序，保证同一 profile 的 provider 顺序恒定。
> 原 `TaskContextProvider` 已删除：普通清单由 Harness `todos_*` 承担，宿主执行记录仍归 `ITaskService`。
> 各 profile 的能力差异见 `PipelineProfileBehavior.For`（以「全集减去若干能力」形式表达）。
> Team 子 Agent 的角色指令不再由专属 Provider 注入：`TeamAgentFactory` 将角色正文交给
> `ChatOptions.Instructions`，由 MAF 与 Harness 通用指令合成，产品侧不再持有重复的注入职责。
> MAF `AIContextProvider` 实例不注册到 DI——它们在 Agent Runner 构建管线时按需创建。

### 11. 记忆治理与演进方向

> 本节补充于 2026-09-16。§1–§10 回答「记忆模块**长什么样**」（结构），本节回答「记忆模块**如何变好**」（治理）。

结构正确不等于模块会变好。记忆模块的核心风险是随时间**膨胀、矛盾、不准确**——这是结构无法解决的，
需要**治理**。2026-09-15 至 09-16 的一轮重构中确认了两个问题：

1. **AutoDream 从未真正运行**（扫描目录写错，已修复）—— 最有价值的自动化机制此前一直空转
2. **淘汰只看时间** —— 按 `UpdatedAt` 做 LRU 等价于「旧的先死」，与「用得多的应该留下」无关联

#### 11.1 Recall 与 Retention 的职责划分（设计基线）

记忆的两个根本问题必须由**不同主体**回答：

| 问题 | 由谁决定 | 理由 |
|---|---|---|
| **Recall**（想起什么、何时想起） | ✅ **LLM** | 只有 LLM 知道当前任务需要什么。`search_memories` 工具为此存在 |
| **Retention**（留下什么、留多久、何时删） | ❌ **不能给 LLM** | LLM 无全局视野，看不到记忆总量；无记账能力；倾向“多记”；无法自我否定 |

**LLM 只做它擅长的语义判断**：

- “这条新信息与那条旧记忆**是同一条**吗？”（→ 合并，复用 Key）
- “这条新信息**推翻**了那条旧记忆吗？”（→ 取代，旧条目标记 superseded）
- “这条信息**值得**留下吗？”（→ 过滤噪声）

**LLM 不该做的**（宿主负责）：分配 Key、决定配额、决定 TTL、决定淘汰。

> 现状已部分踩在这条线上——`autodream-consolidation.prompt` 让 LLM 输出 `key/scope/ttlHours`，
> 再由 `AutoDreamService.ApplyConsolidationChangesAsync` 做校验、清洗、截断、配额。
> 缺口在于缺少**冲突消解**这一环（§11.3）。

#### 11.2 使用反馈驱动的淘汰（✅ 已实施 2026-09-16）

**决策**：淘汰按**使用价值**而非时间，顺序为：

1. `manual` 条目**豁免**——用户显式意图，永不自动淘汰
2. 其余按 `HitCount` **升序**——从未被检索到的先走
3. 同次数按 `UpdatedAt` **升序**——最旧的先走

**数据基础**：`MemoryEntry.HitCount` / `LastHitAt`，由 `search_memories` 命中时经
`IMemoryEntryStore.RecordHitsAsync` 回写。

**两条关键约束**（违反任一条会使排序退化）：

- **`RecordHitsAsync` 不得改动 `UpdatedAt`**。使用反馈不是内容变更；若 bump 时间戳，
  高频命中条目会因“显得新”而躲过步骤 3 的 tie-break，排序失真。
- **只统计显式检索，不统计 prompt 被动注入**。摘要索引每轮都注入所有条目，计入会让每条目每轮 +1，
  信号被淹没。按需检索才是“模型认为这条有用”的证据。
**`manual` 豁免的对称性**：淘汰路径豁免 `manual`（上表步骤 1），写入路径（AutoDream）也必须拒绝
删改 `manual` 条目——否则"永不淘汰"可被一条 Agent 幻觉输出绕过。保护边界详见 §5.1。
**为何不采用类别权重**：`correction`/`lesson` 权重高于 `fact` 看似合理，但引入调参维度且难以解释，
而“从未被检索 = 低价值”已覆盖绝大多数场景。若后续发现高价值类别被误淘汰，再评估。

代码：`src/OneCode.Core/Memory/MemoryEntry.cs`、`IMemoryEntryStore.cs`（`RecordHitsAsync` 契约）、
`src/OneCode.Infrastructure/Memory/MemoryEntryStore.cs`（`PruneAsync` 淘汰排序）、
`MemorySearchProviderFactory.cs`（命中回写）。

#### 11.3 AutoDream 从“提取器”升级为“治理器”（⛔ 未实施）

**方向**：AutoDream 当前只做**提取**（产出候选记忆）。这不够——记忆膨胀、矛盾、不准确
**不是提取能解决的**：

| 问题 | 当前实现 | 应有机制 |
|---|---|---|
| 记忆膨胀 | ~~LRU 200 条~~ → 已改按 `HitCount`（§11.2） | ✅ 已改善 |
| 记忆矛盾 | prompt 提示 Agent“用 upsert 覆盖同 Key” | ⛔ **主动冲突检测**：与现有条目比对，显式合并/取代 |
| 记忆不准 | 无验证 | ✅ 使用反馈闭环（§11.2） |

**目标闭环**：

```text
会话历史
   │
   ▼
[1] 提取 ─ 产出候选记忆（AutoDream 批量 或 StoreAIContextAsync 即时）
   │
   ▼
[2] 冲突消解 ─ 与现有条目比对：重复→合并；矛盾→supersede；新增→入库   ⛔ 未实施
   │
   ▼
[3] 价值评分 ─ value = f(命中次数, 手动标记, 新鲜度)                  ✅ 部分（§11.2）
   │
   ▼
[4] 淘汰 ─ manual 永不淘汰；其余按价值                                 ✅ 已实施（§11.2）
   │
   ▼
注入 LLM（命中时回写 HitCount / LastHitAt）→ 回到 [3]
```

- **冲突消解靠 LLM**：把“新候选 + 现有同类条目”一起给 LLM，输出 `merge / supersede / skip`。

**两个前置缺口（必须先补，否则 [2] 无从发生）**：

1. **LLM 现在看不到任何已有记忆**。`autodream-consolidation.prompt` 要求输出 "incremental changes"、
   `"upsert" (add or update)`、`"delete" (remove an outdated entry)`，但全文未让 LLM 读到 `MEMORY.md`；
   且工具集为 `Read/Glob/Grep`，而工具中间件挂 `FileSystemInvariant(workingDirectory)`，
   **user 级 `~/.onecode/memory/MEMORY.md` 在项目根之外，LLM 读不到**。
   → 当前"upsert 覆盖同 Key"实为**碰运气**：key 撞对了才更新，撞不对就新增一条平行记忆。
   这解释了为何"异 key 但内容矛盾"无法被自动解决。
2. **已有记忆索引的注入方式必须是 `AIContextProvider`，而非拼进 prompt 模板**。
   `HarnessAgentOptions.AIContextProviders` 是 MAF 的既有扩展点（ADR 0007 §4），
   而 `AutoDreamService.RunConsolidationAgentAsync` 是**唯一没设它**的 agent 构建点。
   MAF 自身 `FileMemoryProvider` 注入记忆索引走的正是这条路径。任务指令留在 prompt 文件，
   运行期数据走 provider —— 两者不应混在同一个模板里。

**`Supersedes` 的归位**：它应作为 **LLM 输出 JSON 的字段**（指令：告诉宿主"我取代了谁"），
**不应加到 `MemoryEntry` 上**。理由：宿主执行时会**硬删**被替代的旧条目，若同时往新条目写
`Supersedes` 指向旧 key，该字段立刻成为**悬空引用**，且无任何读取方 —— 正是本项目已清理多轮的
死字段。取代关系记日志即可。

#### 11.4 即时捕获路径（⛔ 未实施，可选）

MAF `AIContextProvider.StoreAIContextAsync` 在 exchange 结束时被调用，能拿到本轮完整对话
（`InvokedContext` 携带 request + response）。可用它在每轮对话结束时即时捕获“用户纠正”和“失败教训”。

| | AutoDream 批量扫描 | `StoreAIContextAsync` 即时捕获 |
|---|---|---|
| 触发 | 6 小时 + 3 会话门控 | 对话结束时 |
| 数据源 | 扫 `~/.onecode/events/*.jsonl` | 内存中本轮对话 |
| 记忆新鲜度 | 滞后数小时到数天 | 即时 |
| 成本 | 全盘扫描 + 大 prompt | 轻量判断 |
| 记忆过期问题 | 只能靠 TTL 猜 | **新信息立即覆盖旧信息** |

**分工**：即时捕获负责“纠正/教训”这类高价值、时效性强的记忆；AutoDream 保留负责“跨会话归纳”
（如“构建命令”这类需多次观察才稳定的知识）。两者写入同一 `MEMORY.md`，共用 §11.3 的治理闭环。

**为何未做**：即时捕获会引入**每轮一次 LLM 调用**，与 `search_memories` 的免费回写不同量级。
需先确认成本可接受，且 §11.3 的冲突消解先行——否则只是用更快的方式写入矛盾。

#### 11.5 检索抽象与 SQLite（⛔ 未实施）

当前 `MemoryService.Score` 是内存内联评分（全量加载 + 逐个打分）。**应把这一步抽成 `IMemoryIndex`**：
文件后端用现状逻辑（200 条上限下性能无问题）；未来 SQLite 后端用 FTS5 下推为 SQL 查询。
这样更换后端时 `MemoryService` / `AutoDreamService` / `MemoryCommand` **零修改**。

**SQLite 结论：本期不引入。**

| 维度 | 判断 |
|---|---|
| 收益 | 查询性能、FTS5 全文检索、并发写、事务一致性 |
| 成本 | 新 NuGet 依赖（当前项目**无任何 SQLite 包**）+ 迁移逻辑 + 并发/锁模型重写 + 测试改造 |
| 当前规模 | 单用户 CLI，200 条上限，单文件读写 |
| 判定 | **收益不成立，成本成立** |

**触发上 SQLite 的条件**（任一条满足即可立项）：

1. 条目数稳定超过 ~1000 条
2. 需要跨项目聚合检索
3. 需要多进程并发写同一记忆库
4. 需要向量检索 / 语义检索

**预留方式**：先做 `IMemoryIndex` 抽取，不引入任何新依赖。

#### 11.6 召回评测与中文分词（2026-09-18）

评测样本冻结在 `src/OneCode.Tests/MemoryRecallEvaluationTests.cs`（10 条中英混合语料 + 6 个中文查询 +
5 个代码术语查询 + 2 个负样本），分词规则本身由 `TextTokenizerTests.cs` 锁定。

**评测首轮即失败，暴露真实缺陷**：旧分词器 `[\p{L}\p{N}_-]{2,}` 中 `\p{L}` 匹配 CJK，
「测试怎么跑」会被切成**单个 token**，语料侧同样如此——**任何中文查询的召回恒为空**。

**根因是两条检索路径各写一套分词**：`ToolRetrievalIndex` 已有 CJK bigram，`MemoryService` 没有，
同一查询在 ToolSearch 能命中、在 `search_memories` 为空。现抽出 `OneCode.Core/Text/TextTokenizer`
（拉丁按大小写边界切分并去复数后缀 + CJK 二字滑窗，单字保留 unigram），两条路径共用；
`ToolRetrievalIndex` 只叠加 Hint 字段停用词政策。

**评测方法**：断言**相对召回**（期望条目出现在 Top-N）而非绝对分数——分数随语料变化，锁定分数会让每次
新增条目都变成无意义的维护。负样本（无关查询必须零召回）防止「查询非空就返回前 N 条」的实现通过。

**反证**：临时让 `FindRelevantEntries` 跳过评分直接返回前 6 条，10 项用例全部失败（含 2 个负样本），
确认用例有判别力。

**未闭环**：「动态激活用原生请求扩展点」与「历史向量检索」无需求驱动，按 §11.5 的触发条件保留，
不引入向量后端。中文二字滑窗使长词的部分匹配依赖 bigram 重叠数，属可接受的召回/精确度权衡；
如需更高精确度，应先补充固定样本证明现状不足，再考虑词典或向量方案。

#### 11.7 不引入 MAF `FileMemoryProvider` 替换长期记忆（重申 §1 决策并补论证）

> **范围限定（2026-09-18 补充）**：本节结论**只针对长期记忆（Memdir / `MEMORY.md`）**——
> 不用 `FileMemoryProvider` 替换它。会话工作记忆是**另一个领域**，确实已改用原生
> `FileMemoryProvider`（仅 Main 启用，见 [ADR 0007](./0007-maf-integration-boundaries.md) 的能力归属表）。
> 两者分目录共存：Memdir 管跨会话知识，`FileMemoryProvider` 管当前会话工作产物。
> 不得据本节断言「OneCode 不使用 `FileMemoryProvider`」。

MAF 1.21.0 的 `FileMemoryProvider` 已 stable，但**不适合替换长期记忆**：

| 维度 | `FileMemoryProvider` | OneCode 需求 |
|---|---|---|
| 写入者 | **LLM 自主调工具写** | LLM **禁止**直写（防结构注入，§1） |
| 组织形态 | 一记忆一文件 + 索引 | 单文件结构化条目 |
| 检索 | 正则 grep | token 打分 + Top-N 自动注入 |
| 生命周期 | **无 TTL / 无淘汰** | TTL + 容量上限 |
| 注入内容 | 仅索引（名 + 描述） | 摘要索引 + 全量 manual + Top-8 auto |
| 自动整合 | **无** | AutoDream |

**最关键的一点**：它是“让 LLM 自己决定记什么”，而 OneCode 的长期记忆安全模型是“LLM 不能直接写记忆”。
这不仅是实现差异，是**架构冲突**。会话工作记忆则相反——LLM 本就该自主写工作产物，
不存在结构注入风险，因此该冲突不适用于它。

**结构性约束**：`FileMemoryProvider` 为 `sealed`，**不能派生**，只能组合/包装；其
`WorkingFolder` 策略硬编码在 `HarnessAgent` 内，外部无法覆盖；`FileSystemAgentFileStore`
仍标记 `[Experimental(MAAI001)]`。

**可借鉴的模式**（非实现）：

1. `FileMemoryProviderOptions.Instructions` 可配置化——`AIContextProvider` 的注入文案应外移到
   prompt 文件，而非硬编码字符串
2. `ProviderSessionState<T>`——OneCode 已在用
3. `AgentFileStore` 的解耦力度——`IMemoryEntryStore` 已是同类（按 scope 而非按路径）

**不该借鉴**：LLM 持有写工具、一记忆一文件、无生命周期管理。

#### 11.8 验证状态与已知覆盖缺口

> 本节补充于 2026-09-16，记录 §11.2 落地后的**验证边界**——哪些结论已被守卫测试保护、哪些仍是缺口。
> 过程证据（缺陷复现、逐阶段清单、实测数字）原属 `docs/plan/memory-module-refactor-plan.md`，
> 该文档已按 [ADR 0007](./0007-maf-integration-boundaries.md) 处理计划文档的先例删除（结论入 ADR，引用已迁移）。

| # | 项 | 状态 | 守卫手段 |
|---|---|---|---|
| 1 | 使用反馈驱动的淘汰（§11.2） | ✅ 已覆盖 | `MemoryEntryStoreTests` 三个淘汰测试；已反证（写反排序即失败） |
| 2 | 命中回写不改 `UpdatedAt`（§11.2 约束 1） | ✅ 已覆盖 | `MemoryEntryStoreTests.RecordHitsAsync_DoesNotBumpUpdatedAt` |
| 3 | 只统计显式检索（§11.2 约束 2） | ✅ 已覆盖 | `MemoryServiceTests.LoadMemoryPromptAsync_DoesNotRecordHits`（被动注入）+ `MemorySearchProviderFactoryTests`（显式检索按 scope 回写） |
| 4 | `manual` 写入路径三道闸（§5.1） | ✅ 已覆盖 | `MemoryEntryStoreTests` 5 条守卫测试；三闸分别反证 |
| 5 | AutoDream 扫描目录契约 | ✅ 已覆盖 | `AutoDreamProjectAwarenessTests`（走真实 `FileSessionEventStore` 写出） |
| 6 | `/insights` 事件格式契约 | ✅ 已覆盖 | `InsightsCommandTests`（真实事件写出 → 断言统计数字） |
| 7 | **AutoDream 端到端**（真实门控 + LLM 产出条目） | ⚠️ **未验证** | 仅单元测试覆盖门控分支；真实链路（4 层门控 → Agent → `MEMORY.md` 落盘）无自动化 |
| 8 | 会话记忆删除后的行为回归（§6） | ✅ 结构上不可能 | 子系统已物理删除，无调用方可回归 |

**项 7 的人工验证步骤**（需真实环境与模型调用，不适合单元测试）：

1. 在目标项目目录用 CLI 正常产生 **≥ 3 个会话**（会话文件落在 `~/.onecode/events/*.jsonl`，
   且首行 `payload.working_directory` 等于该项目根）
2. 删除 `{cwd}/.onecode/memory/last_consolidated_at`（重置时间门控；该文件存在时 6 小时内不再触发）
3. 执行 `/memory autodream trigger`，随后 `/memory autodream status` 观察 `Last consolidated` 是否推进
4. 断言 `MEMORY.md` 实际新增条目；或查日志 `AutoDream completed: {In}+{Out} tokens, {N} memory entries written`

> **为何 4 层门控使端到端难以自动化**：启用检查 → 时间门控（≥ 6h）→ 扫描节流（10min，持久化）→
> 会话门控（≥ 3）。`Trigger()` 只发送信号，**不绕过任何门控**（`AutoDreamService.TryConsolidateAsync`）。
> 门控分支本身已由单元测试直接调用 `TryConsolidateAsync` 覆盖，故缺口仅在"真实 LLM 产出 → 落盘"这一段。

## 影响

- **存储模型简化**：消除 `memory-store/` 目录与 `IMemoryStore` 抽象，结构化记忆统一到 `MEMORY.md`——子系统收敛为 1 个（结构化条目记忆）；会话记忆子系统已删除；团队共享记忆不设独立子系统，由 project 级 `MEMORY.md` 覆盖（§8）
- **后端可替换**：`IMemoryEntryStore` 基于 `MemoryScope` 操作，未来可无缝替换为 SQLite 等后端，无需修改 `MemoryService` / `AutoDreamService` / `MemoryCommand`
- **自动积累闭环**：AutoDream 直接写入 `MEMORY.md`，用户知识库可持续自动丰富，无需手动迁移
- **安全边界明确**：Agent 不通过工具直接写 `MEMORY.md`，所有程序化写入经 `IMemoryEntryStore` 或 AutoDream 清洗管线，防止结构注入
- **并发安全**：per-directory `SemaphoreSlim` 保护进程内并发；AutoDream 的 `autodream.lock`（`FileStream` + `FileShare.None`）保护跨进程并发，僵尸锁（超 2 小时）可安全抢占

## 扩展指南

### 新增存储后端

实现 `IMemoryEntryStore`，在 `src/OneCode.App/Services/Memory/MemoryServiceCollectionExtensions.cs` 的 `AddMemoryServices` 替换注册即可，调用方零修改。

### 新增记忆类别

在 AutoDream 整合 prompt（`prompts/system/autodream-consolidation.prompt`）中引导 Agent 输出新 category 的 Key，`MemoryEntry.DeriveCategory` 自动从 Key 前缀推导，无需改代码。
