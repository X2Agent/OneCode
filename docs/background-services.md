# 后台服务与定时任务

> 本文档梳理 OneCode 中所有后台服务（`BackgroundService` / `IHostedService`）和定时任务的设计、职责、执行时机，以及分层原则与开发规范。

---

## 目录

- [1. 概述](#1-概述)
- [2. 分层原则](#2-分层原则)
- [3. 服务清单](#3-服务清单)
- [4. Cron 子系统详解](#4-cron-子系统详解)
- [5. AutoDream 记忆整合](#5-autodream-记忆整合)
- [6. 开发规范](#6-开发规范)
- [7. 已知问题与改进方向](#7-已知问题与改进方向)

---

## 1. 概述

OneCode 共有 **5 个长循环后台服务** + **3 个启动加载服务** + **4 个 UI 定时器**：

| 类别 | 机制 | 数量 | 说明 |
|------|------|------|------|
| 长循环后台服务 | `BackgroundService` | 5 个 | 周期性轮询或事件驱动，进程生命周期内常驻 |
| 启动加载服务 | `IHostedService` | 3 个 | 仅启动时执行一次，停止时清理资源 |
| UI 定时器 | `IApplication.AddTimeout` | 4 个 | Terminal.Gui 主循环节拍，非业务调度 |

[AutoDreamService](#5-autodream-记忆整合) 是标准 `BackgroundService`，随宿主生命周期自动启停，同时支持外部 `Trigger()` 信号触发，记忆统一写入 `MEMORY.md`。

---

## 2. 分层原则

### 2.1 判据：依赖面，不是"是不是后台任务"

后台服务分布在两个层：

```
OneCode.Automation  ← 仅依赖 Core 抽象的后台调度/启动加载层
OneCode.App         ← 依赖 App 业务编排类型的后台服务
```

下沉到 Automation 的硬性条件：**仅依赖 Core 抽象**（如 `ICronParser`、`IModelCatalogCache`、`YoloRuleStore`）。一旦服务依赖了 App 层类型（MAF Agent、`ToolCatalog`、`PromptManager` 等），就必须留在 App 层。

### 2.2 为什么不强行统一到一层

以 AutoDreamService 为例，它依赖 `IChatClient`、`ToolCatalog`、`PromptManager`、`ModelManager`、`IMemoryEntryStore` 等 5+ 个 App 层服务。若要下沉到 Automation，需要把这些都抽成 Core 接口，会让 Core 沦为"接口垃圾场"，违反项目"不为统一而强行抽接口"的明确决策。

**结论：物理位置保持分层，通过统一生命周期抽象和注册模式来实现一致性。**

### 2.3 反向依赖处理

Automation 层需要调用 App 层运行时（如 Cron 触发后要把 prompt 交给会话执行），通过 DI 反向注入接口实现，**禁止 ProjectReference**：

```csharp
// Automation 层定义接口
public interface ICronJobExecutor
{
    Task ExecuteJobAsync(string prompt, CancellationToken ct);
}

// App 层实现并注册
services.AddSingleton<CronJobExecutor>();
services.AddSingleton<ICronJobExecutor>(sp => sp.GetRequiredService<CronJobExecutor>());
services.AddCronScheduler();  // Automation 提供的扩展方法
```

---

## 3. 服务清单

### 3.1 Automation 层后台服务

#### 3.1.1 CronSchedulerService

| 属性 | 值 |
|------|-----|
| 文件 | [OneCode.Automation/Cron/CronSchedulerService.cs](../src/OneCode.Automation/Cron/CronSchedulerService.cs) |
| 类型 | `BackgroundService` |
| 执行时机 | 启动加载 + **30 秒轮询** + FileSystemWatcher（2 秒去抖） |
| 职责 | 扫描 `~/.onecode/cron/*.json`，到期则委托 `ICronJobExecutor` 执行 prompt |
| 依赖 | `ICronParser`（Core）、`ICronJobExecutor`（App 实现，DI 反向注入） |

详见 [Cron 子系统详解](#4-cron-子系统详解)。

#### 3.1.2 ModelCatalogRefreshService

| 属性 | 值 |
|------|-----|
| 文件 | [OneCode.Automation/ModelCatalog/ModelCatalogRefreshService.cs](../src/OneCode.Automation/ModelCatalog/ModelCatalogRefreshService.cs) |
| 类型 | `BackgroundService` |
| 执行时机 | `StartAsync` 同步加载磁盘缓存 → `ExecuteAsync` 启动即检查（若 stale）→ 之后 **24 小时检查** |
| 职责 | 维护 models.dev 快照，缓存 7 天过期，过期则从远程刷新 |
| 依赖 | `IModelCatalogCache`、`IModelCatalog`（Core） |

24 小时检查间隔选择理由：缓存本身的过期阈值是 7 天，每天检查一次足够及时。

#### 3.1.3 YoloRuleStoreLoader

| 属性 | 值 |
|------|-----|
| 文件 | [OneCode.Automation/Yolo/YoloRuleStoreLoader.cs](../src/OneCode.Automation/Yolo/YoloRuleStoreLoader.cs) |
| 类型 | `IHostedService` |
| 执行时机 | **仅启动时一次** |
| 职责 | 加载 `~/.onecode/yolo_rules.json` 到 `YoloRuleStore` |
| 依赖 | `YoloRuleStore`、`IYoloRuleFileStore`（Core） |

设计选择（HostedService 而非 DI 工厂内同步加载）：
- 同步阻塞 DI 工厂会拖慢启动且违反 async 规范
- 文件不存在/解析失败不阻断启动，规则集为空时 `YoloClassifier` 返回 None，`PermissionChecker` fallback 到 Ask，保证安全兜底

### 3.2 App 层后台服务

#### 3.2.1 SkillFilesWatcher

| 属性 | 值 |
|------|-----|
| 文件 | [OneCode.App/Services/Skills/SkillFilesWatcher.cs](../src/OneCode.App/Services/Skills/SkillFilesWatcher.cs) |
| 类型 | `BackgroundService` |
| 执行时机 | FileSystemWatcher 事件驱动 + **300ms Channel 去抖** |
| 职责 | 监视 skills 目录（内置/用户/项目），变化后触发 `SkillsChanged` 事件，让 TUI 重渲染斜杠命令列表 |
| 依赖 | `SkillCatalog`（读文件系统） |

**它不构建也不替换 provider**：agent 可见的技能由 `SkillProviderFactory` 在**每次 agent run** 时解析，
斜杠命令发现由 `SkillCatalog` 直接读文件系统——两者都不需要重建。因此本服务只负责通知，不持有
`AgentSkillsProvider` 或 `SkillProviderFactory`。

留在 App 层的原因：需要 `SkillCatalog` 与 TUI 重渲染通知，不属于 Core 抽象。

去抖机制：使用 `BoundedChannel`（容量 1，`DropOldest` 模式）+ 300ms `CancellationTokenSource.CancelAfter` 实现去抖，避免文件连续修改触发多次通知。

#### 3.2.2 LspHostedService

| 属性 | 值 |
|------|-----|
| 文件 | [OneCode.App/Services/Lsp/LspHostedService.cs](../src/OneCode.App/Services/Lsp/LspHostedService.cs) |
| 类型 | `IHostedService` |
| 执行时机 | `ApplicationStarted` 回调启动；`StopAsync` 停止全部 |
| 职责 | 为已安装的语言包启动 LSP 服务器，关闭时停止全部 |
| 依赖 | `LanguagePackRegistry`、`ILspServerManager`、`LanguagePackInstaller`、`IWorkingDirectoryAccessor`、`IStartupHintCollector`、`IHostApplicationLifetime` |

留在 App 层的原因：依赖 App/Infra 层的语言包类型。

延迟启动设计：LSP 启动延迟到 `ApplicationStarted` 之后，确保会话工作目录（`--workspace` 或 `/cwd` 设置）已就绪。启动 Task 被追踪到 `_startTask` 字段，`StopAsync` 中 await 它，确保：
- 启动失败异常被观察（避免 unobserved-task-exception）
- 快速关闭时不与 `StopServerAsync` 竞争

#### 3.2.3 AutoDreamService

| 属性 | 值 |
|------|-----|
| 文件 | [OneCode.App/Services/AutoDream/AutoDreamService.cs](../src/OneCode.App/Services/AutoDream/AutoDreamService.cs) |
| 类型 | `BackgroundService`（Singleton + HostedService 双注册） |
| 执行时机 | 定时轮询（每小时 1 次，硬编码安全网）+ 外部 `Trigger()` 信号（如 `/memory autodream trigger` 命令） |
| 职责 | 四重门控后启动轻量 Agent 整理**会话工作记忆与历史会话**，增量变更 JSON 合并写入 `MEMORY.md` |
| 依赖 | `IChatClient`、`ToolCatalog`、`PromptManager`、`IModelManager`、`IMemoryEntryStore`、`IConfigManager`、`IWorkingDirectoryAccessor` |

双注册模式说明：`AddSingleton<AutoDreamService>()` + `AddHostedService(sp => sp.GetRequiredService<AutoDreamService>())`，
确保既可通过 DI 获取实例调用 `Trigger()`，又让 `BackgroundService.ExecuteAsync` 随宿主自动启停。
注册位置：`src/OneCode.App/Services/AutoDream/AutoDreamServiceCollectionExtensions.cs` 的 `AddAutoDreamServices()`。

详见 [AutoDream 记忆整合](#5-autodream-记忆整合)。

#### 3.2.4 PlanExecutionRecoveryService

| 属性 | 值 |
|------|-----|
| 文件 | [OneCode.App/Services/PlanMode/PlanExecutionRecoveryService.cs](../src/OneCode.App/Services/PlanMode/PlanExecutionRecoveryService.cs) |
| 类型 | `BackgroundService`（注册于 `src/OneCode.App/Services/PlanMode/PlanWorkflowServiceCollectionExtensions.cs`） |
| 执行时机 | `PeriodicTimer` 每 **5 秒**扫描 + `AttachSession` 时立即触发一次 |
| 职责 | 扫描持久化的 Plan 执行工作流并恢复：`StartingExecution` 状态重试启动；`Executing`/`Verifying` 状态与持久化 BuildRun 对账后由幂等派发器续跑；BuildRun 缺失/身份不匹配/与已批准计划不一致时按协议失败 |
| 依赖 | `IPlanAggregateStore`、`IPlanWorkflowApplicationService`、`IBuildRunStore`、`IPlanAgentRunDispatcher` |

会话门控：TUI 启动时通过 `AttachSession` 挂接交互会话；未挂接或会话未加载时仅保留持久化恢复状态，不执行派发。扫描经 `SemaphoreSlim(1,1)` 串行化，单工作流恢复失败仅记 Warning，不影响其余工作流。

留在 App 层的原因：依赖 Plan/Build 工作流应用服务与交互会话抽象（App 层业务）。

#### 3.2.5 HostStopSessionCloseService

| 属性 | 值 |
|------|-----|
| 文件 | [OneCode.App/Services/Hooks/HostStopSessionCloseService.cs](../src/OneCode.App/Services/Hooks/HostStopSessionCloseService.cs) |
| 类型 | `IHostedService`（注册于 `src/OneCode.App/Services/Hooks/HookServiceCollectionExtensions.cs`） |
| 执行时机 | `StartAsync` 空实现；**仅宿主停止时**（`StopAsync`）执行一次 |
| 职责 | 宿主停止时关闭前台会话，覆盖 TUI 退出键 / Ctrl+C / 宿主关闭等未走 `/exit` 的退出路径 |
| 依赖 | `ISessionManager`（App 层会话编排） |

位于 `Services/Hooks/` 目录，但**不触发任何 hook**：`SessionEnd` 拦截点已移出 Hook 范围，本服务只负责会话收尾。
`/exit`、`/quit` 已由 `ExitCommand` 以 `prompt_input_exit` 显式关闭会话，此处为幂等 no-op。
收尾使用 `CancellationToken.None` 而非停止令牌——停止阶段令牌可能已取消，不能截断收尾投递。

### 3.3 UI 定时器

| 定时器 | 文件 | 周期 | 用途 |
|--------|------|------|------|
| `SpinnerController` | [OneCode.App/Tui/SpinnerController.cs](../src/OneCode.App/Tui/SpinnerController.cs) | 帧间隔 | Braille spinner 帧动画 |
| `AgentStatusBar` 模式闪烁 | [OneCode.App/Tui/AgentStatusBar.cs](../src/OneCode.App/Tui/AgentStatusBar.cs) | 400ms，一次性 | 模式切换时的高亮闪烁，到时清除标记 |
| 流式预览重建 | [OneCode.App/Tui/ChatTranscriptView.Streaming.cs](../src/OneCode.App/Tui/ChatTranscriptView.Streaming.cs) | 16ms（约 60 FPS 上限），一次性 | 合并同一节拍窗口内的多次 token 到达，把每 token 重排的 O(n²) 降为每窗口一次 |
| 启动首帧调度 | [OneCode.App/Tui/OneCodeToplevel.cs](../src/OneCode.App/Tui/OneCodeToplevel.cs) | `TimeSpan.Zero`，一次性 | 界面就绪后补一次欢迎页、MCP 状态栏刷新与焦点设置 |

均通过 `IApplication.AddTimeout` 注册，没有业务调度：不轮询后台状态，不替代 `BackgroundService`。

---

## 4. Cron 子系统详解

Cron 是项目唯一面向用户的"定时任务"功能，通过 AI 工具暴露给 LLM 调用。

### 4.1 架构

```
┌─────────────────────────────────────────────────────────────┐
│                    OneCode.Automation                        │
│  ┌─────────────────────────────────────────────────────┐   │
│  │             CronSchedulerService                     │   │
│  │  - 扫描 ~/.onecode/cron/*.json                       │   │
│  │  - FileSystemWatcher 监视变更（2s 去抖）              │   │
│  │  - 30s 轮询检查到期                                  │   │
│  │  - 到期 → ICronJobExecutor.ExecuteJobAsync(prompt)  │   │
│  └──────────────────────┬──────────────────────────────┘   │
│                         │ ICronJobExecutor（接口）           │
└─────────────────────────┼───────────────────────────────────┘
                          │ DI 反向注入
┌─────────────────────────┼───────────────────────────────────┐
│                    OneCode.App                               │
│  ┌──────────────────────▼──────────────────────────────┐   │
│  │                CronJobExecutor                       │   │
│  │  - SemaphoreSlim 串行化（防与 TUI 主循环重叠）        │   │
│  │  - 构建/缓存系统提示词                                │   │
│  │  - 确保前台会话存在                                   │   │
│  │  - WorkingMode.Goal（无人值守）执行 prompt           │   │
│  │  - 排空事件流至完成                                   │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### 4.2 Cron 工具

通过单个 `Cron` AI 工具的 `action` 参数暴露 5 个操作：

| `action` | 说明 |
|------|------|
| `create` | 创建定时任务，支持标准 5 字段 cron 表达式 |
| `list` | 列出所有定时任务 |
| `delete` | 按 ID 删除定时任务 |
| `pause` | 暂停任务（保留历史，不删除） |
| `resume` | 恢复暂停的任务（重算 NextRunAt，不补执行错过的） |

工具整体声明为 `ToolRisk.Destructive`（`ToolPolicyDefaults.ForRisk` 据此给出 `Always` 审批模式），加载策略为
`ToolLoadPolicy.Deferred`（不自动加载，需经 ToolSearch 激活）。注册处按 `action` 细分的风险等级不存在——
5 个操作共用同一个工具级风险标注，因此只读的 `list` 也会经过审批边界。

### 4.3 CronJobEntry 字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `Id` | string | 8 字符 GUID 前缀 |
| `Cron` | string | 标准 5 字段 cron 表达式（本地时间） |
| `Prompt` | string | 每次触发时入队的 prompt |
| `Recurring` | bool | true=循环，false=一次性 |
| `Durable` | bool | 是否持久化到磁盘（需 `ONECODE_DURABLE_CRON=true`） |
| `Paused` | bool | 是否暂停 |
| `CreatedAt` | long | 创建时间（Unix 秒） |
| `LastRunAt` | long? | 上次执行时间 |
| `NextRunAt` | long? | 下次执行时间 |

### 4.4 持久化策略

- **默认会话级**：`Durable = false`，仅存在于内存，进程退出即失
- **持久化需显式启用**：`ONECODE_DURABLE_CRON=true` + `durable=true`，写入 `~/.onecode/cron/{id}.json`
- **上限**：单进程最多 50 个 job

### 4.5 执行隔离

`CronJobExecutor` 使用 `SemaphoreSlim` 串行化执行，确保 cron 触发的运行不会与 TUI 主循环在同一个 `ForegroundConversation` 上重叠。

### 4.6 执行策略

Cron 触发的任务以 `WorkingMode.Goal` 执行——无人值守下模型可自主分解子任务并迭代验证（`CronJobExecutor.ExecuteJobAsync`）。
Plan 模式下提交的计划会进入持久化 `AwaitingApproval`，必须由用户审批，不适合无人值守场景，因此 Cron 不使用 Plan 模式。

---

## 5. AutoDream 记忆整合

AutoDream 是一种"睡眠整理"机制，类似大脑睡眠时整理记忆的过程。

### 5.1 设计思想（参考 Claude Dreams）

AutoDream 模拟人类睡眠时大脑整理记忆的过程：当用户积累了足够多的新会话后，
在对话空闲时自动启动一个轻量 Agent，回顾这些会话，提取关键信息并以增量变更
JSON 形式合并写入 `MEMORY.md`。

**解决的问题：**
- 跨会话遗忘：每次会话从零开始，重复犯错
- 手动记忆负担：用户不应手动告诉 Agent "记住这个"
- 知识积累：随时间构建知识库，改善后续交互
- 模式识别：识别单次会话无法发现的跨会话规律

### 5.2 触发机制（双触发）

| 触发方式 | 机制 | 说明 |
|----------|------|------|
| 定时轮询 | `BackgroundService.ExecuteAsync` 周期循环 | 每小时 1 次（硬编码，安全网） |
| 外部信号 | `Trigger()` | 供 `/memory autodream trigger` 命令、测试调用 |

双触发通过 `Channel<bool>` 实现：`ExecuteAsync` 同时等待定时器和通道信号，
任一就绪即执行门控检查。通道容量 1（DropWrite 模式），保证幂等。

### 5.3 门控条件（四重）

| 门控 | 条件 | 默认值 | 配置键 / 环境变量 |
|------|------|--------|----------|
| 启用门控 | 非远程模式 + AutoDream 未被显式关闭 | **开启** | `autodream.enabled` / `ONECODE_AUTODREAM=false` 关闭；`ONECODE_REMOTE=true` 自动关闭 |
| 时间门控 | 距上次整合 ≥ `minHours` | 6 小时 | `autodream.minHours` / `ONECODE_AUTODREAM_MIN_HOURS` |
| 扫描节流 | 距上次扫描 ≥ 10 分钟 | 10 分钟（硬编码） | — |
| 会话门控 | 新会话数 ≥ `minSessions` | 3 个 | `autodream.minSessions` / `ONECODE_AUTODREAM_MIN_SESSIONS` |

AutoDream **默认开启**，无需任何配置。两个自然门控（时间 + 会话数）已足够防止频繁触发。
扫描节流时间戳持久化到 `{cwd}/.onecode/memory/last_session_scan_at` 文件，跨重启生效，且按项目隔离。

### 5.4 执行流程

1. 门控检查（四重，任一不通过即退出）
2. 获取跨进程文件锁（`FileStream` + `FileShare.None`，原子获取）——锁文件在 `{cwd}/.onecode/memory/autodream.lock`
3. 收集候选：`{cwd}/.onecode/agent-file-memory/` 中自上次整合后被改动的**会话工作记忆**（最多 20 个，最新优先，只读快照）；再扫描 `~/.onecode/events/*.jsonl` 统计**当前项目**的新会话（按事件信封的 `payload.working_directory` 字段过滤）
4. 构建整合提示词（从 `prompts/system/autodream-consolidation.prompt` 加载，注入 `project_root`、`since`、`session_count`、`working_memory_root`、`working_memory_section` 变量）
5. 启动轻量 `ChatClientAgent`（仅 Read/Glob/Grep 只读工具）
6. Agent 输出结构化 JSON 数组
7. 解析增量变更 JSON 数组后，按 `scope` 写入 `IMemoryEntryStore`：`user` → `~/.onecode/memory/MEMORY.md`，`project` → `{cwd}/.onecode/memory/MEMORY.md`（Agent 输出经 `SanitizeKey`/`SanitizeValue` 清洗，防 MEMORY.md 结构注入）
8. 清理过期/超容量记忆条目（`IMemoryEntryStore.PruneAsync`，按 `MemoryScope.User`/`Project` 分别调用，移除过期条目 + 按使用价值驱逐超容量条目）
9. 更新 `last_consolidated_at` 时间戳（仅成功时更新，存入 `{cwd}/.onecode/memory/`）

### 5.5 写入位置与项目隔离

通过 `IMemoryEntryStore` + `MemoryScope` 枚举区分作用域，实现层按 scope 解析物理目录，所有记忆统一存在 `MEMORY.md` 中：

| Scope | 物理目录 | 入口文件 | 用途 |
|-------|---------|----------|------|
| `MemoryScope.User` | `~/.onecode/memory/` | `MEMORY.md` | 跨项目用户偏好（如沟通风格、工具偏好） |
| `MemoryScope.Project` | `{cwd}/.onecode/memory/` | `MEMORY.md` | 项目特定决策与约定（如 OAuth 必须用 DPAPI） |

**状态文件按项目隔离**：`autodream.lock`、`last_consolidated_at`、`last_session_scan_at` 存入 `{cwd}/.onecode/memory/`，与项目 `MEMORY.md` 同目录，每个项目有独立的整合时间线和锁。

**会话扫描按项目过滤**：仅统计 `working_directory` 匹配当前项目的会话文件。

**AutoDream 不写入 AGENTS.md 等工程规范文件。** AGENTS.md 是人工维护的约束规范，自动改写会污染规范并破坏稳定性。

### 5.6 配置

AutoDream **默认开启**，开箱即用。如需调整，在 `settings.json` 中配置：

```json
{
  "autodream": {
    "enabled": true,
    "minHours": 6,
    "minSessions": 3
  }
}
```

| 配置键 | 类型 | 默认值 | 作用 |
|--------|------|--------|------|
| `autodream.enabled` | `bool` | `true` | 是否启用。设为 `false` 关闭 |
| `autodream.minHours` | `int` | `6` | 距上次整合的最小间隔小时数 |
| `autodream.minSessions` | `int` | `3` | 触发整合的最小新会话数 |

**优先级**：环境变量 > `settings.json` > 默认值。

| 环境变量 | 覆盖的配置键 | 说明 |
|----------|-------------|------|
| `ONECODE_AUTODREAM` | `autodream.enabled` | `false` 时关闭 |
| `ONECODE_AUTODREAM_MIN_HOURS` | `autodream.minHours` | — |
| `ONECODE_AUTODREAM_MIN_SESSIONS` | `autodream.minSessions` | — |
| `ONECODE_REMOTE` | — | `true` 时无论配置如何都强制关闭（远程模式） |

模型直接复用已有的 `fastModel`（未配置时回退到主 `model`），不需要单独配置。

### 5.7 并发控制

| 层级 | 机制 | 说明 |
|------|------|------|
| 进程内 | `volatile bool _isRunning` | 防止 `ExecuteAsync` 重入 |
| 跨进程 | `FileStream` + `FileShare.None` | 原子获取，`Dispose` 自动释放 |
| 僵尸锁 | `StaleLockTimeout`（2 小时） | 超时锁可安全抢占 |

跨进程锁使用 `FileStream` 独占模式（`FileShare.None`）原子获取，无"先检查再写入"的窗口。
即使进程崩溃，GC 也会回收文件句柄，不会留下永久锁。

---

## 6. 开发规范

### 6.1 新增后台服务的分层判断

```
新增后台服务
    │
    ▼
仅依赖 Core 抽象？
    │
    ├─ 是 → OneCode.Automation
    │       使用 AddXxx 扩展方法注册
    │       依赖 App 实现时通过 ICronJobExecutor 模式反向注入
    │
    └─ 否 → OneCode.App
            直接注册为 HostedService / BackgroundService
```

### 6.2 BackgroundService 通用要求

- 必须 `sealed`，构造函数注入依赖（primary constructor 优先）
- `ExecuteAsync` 必须响应 `stoppingToken`，循环内使用 `Task.Delay(interval, stoppingToken)`
- 异常处理：`OperationCanceledException` 直接 break 退出；其他异常记录 `LogWarning` 后继续下一轮
- **禁止 fire-and-forget**：所有 Task 必须追踪，异常必须观察
- 调度间隔必须定义为 `private static readonly TimeSpan`，并附注释说明选择理由

```csharp
// 30 秒：cron 调度轮询间隔，平衡及时性与开销
private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
```

### 6.3 IHostedService（启动加载服务）规范

- 仅做"启动时一次性加载"，不进入长循环
- `StartAsync` 应快速返回，重活走异步 Task 但**必须追踪 Task 引用**
- 文件不存在/解析失败不阻断启动，需有兜底（默认值、空集合、降级路径）
- `StopAsync` 中 await 所有启动 Task，确保关闭前启动完成

### 6.4 注册模式

**Automation 层**：通过 `OneCode.Automation.ServiceCollectionExtensions` 的 `AddXxx` 扩展方法注册，App 层组合根调用：

```csharp
services.AddCronScheduler();
services.AddModelCatalogRefresh();
services.AddYoloRuleStoreLoader();
```

**App 层**：在每个领域目录的 `XxxServiceCollectionExtensions.cs` 中通过 `AddHostedService<T>` 注册（如
`Services/Hooks/HookServiceCollectionExtensions.cs` 的 `AddHookServices`、
`Services/AutoDream/AutoDreamServiceCollectionExtensions.cs` 的 `AddAutoDreamServices`）。
组合根 `OneCodeApp.Create` 保留显式有序的 `AddXxx()` 调用列表，调用顺序即 HostedService 启动顺序
（由 `ServiceCollectionSnapshotTests.HostedServices_ResolveInRegistrationOrder` 锁定）。

---

## 7. 已知问题与改进方向

| 优先级 | 问题 | 建议 |
|--------|------|------|
| P1 | 缺少统一的 `IStartupTask` 抽象 | 将 YoloRuleStoreLoader、LspHostedService、HostStopSessionCloseService 等启动任务统一管理 |
| P2 | App 层后台服务注册散落多个文件 | 补 `AddAppBackgroundServices` 聚合方法，与 Automation 层对齐 |
| P2 | CronJobExecutor 系统提示词缓存永不失效 | 监听 skills/MCP 变更事件刷新缓存 |

前两项当前状态：启动任务各自实现 `IHostedService` 并按领域目录分散注册（`AddCronScheduler`、`AddModelCatalogRefresh`、
`AddYoloRuleStoreLoader`、`AddHookServices`、`AddLspServices`、`AddAutoDreamServices`、`AddPlanWorkflowServices`、`AddSkillServices`），
没有 `IStartupTask` 抽象与 `AddAppBackgroundServices` 聚合入口。
`CronJobExecutor` 的 `_cachedSystemPrompt` / `_cachedHarnessPrompt` 用 `??=` 缓存后不再失效：会话中途新增 skill 或 MCP 服务器，Cron 任务仍使用旧提示词。
