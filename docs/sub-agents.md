# 子代理派工体系（Sub-agents）

OneCode 的**确定性子代理派工**总览：派工入口、profile 能力矩阵、任务生命周期、DAG 语义，
以及与 MAF `BackgroundAgentsProvider` 的边界。

> **决策与证据**：为什么不用、也不能用 MAF BackgroundAgents 作为派工底层，见
> [子代理派工边界](./adr/0011-background-agents-delegation-boundary.md)（四条硬证据 + 接口级结论）。
> 本文只讲机制，不重复决策。**改机制前先读该 ADR。**

---

## 1. 派工入口（模型可见的工具面）

| 工具 | 作用 | 关键参数 | 注册形态 |
|---|---|---|---|
| `Agent` | 起一个子代理跑委派任务，返回其最终输出 | `prompt`（自包含）、`agent`（`general-purpose`/`Explore`/`Plan` 或自定义名，见 §5）、`description`、`runInBackground`、`maxTurns`（可选，只能收窄配置上限，见 §6） | `ToolRisk.Safe`、`Contextual`（keywords: agent/sub-agent/delegate）、`PlanAllowed` |
| `ParallelAgents` | 把一组任务编译成 DAG 并行/带依赖执行 | `tasks[]`（`id`/`prompt`/`agent`/`description`/`dependsOn`/`executionAccess`）、`injectUpstreamResults`（默认 true） | `ToolRisk.Safe`、`Contextual`、`PlanAllowed` |
| `Task` | 宿主后台任务的检查/控制（**不是派工工具**） | `action`（`get`/`list`/`stop`/`output`）、`taskId`、`maxLines` | `ToolRisk.Safe`、`Contextual`、`PlanAllowed` |

**同步 vs 后台**：`Agent` 默认同步阻塞直到子代理结束；`runInBackground=true` 立即返回 `taskId`，
按 `Task(action="output")` 取结果、`stop` 取消。后台任务绑定 **per-task 取消令牌**
（`ITaskService.GetTaskToken`）——ESC / 新输入不会取消它，只有 `stop` 或任务终态才取消。

**可发现性**：`Agent`/`ParallelAgents` 是 `Contextual` 加载。云端模型全量目录可见；
本地模型（<32K 上下文走 `SessionToolSet` 过滤）经三条激活链路进入工具集：
① 用户 prompt 关键词评分（初始选择）；② `ToolSearch`；③ 未知工具兜底（模型幻觉调用已注册未加载的
工具名时自动激活，下一轮生效）。

**守卫**：`ModePromptToolReferenceTests` 校验模式提示词点名的工具必须在 `AddToolServices` 注册表中，
且派工语义行不得出现宿主后台任务工具名（防「Task tool」式幽灵指引复发）。

---

## 2. Profile 能力矩阵（子代理形态）

`PipelineProfile` + `PipelineProfileBehavior`（`src/OneCode.Infrastructure/Agent/PipelineProfile.cs`）
是子代理能力的**单一真相源**——「Worker 有没有 LSP 上下文」只在这里回答一次。
`PipelineProfileBehavior.FromAgentType` 把工具参数字符串映射到 profile：`Explore`→Explore、
`Plan`→Plan、其余→Worker。

| 能力 | Full（主） | Worker | Explore / Plan |
|---|---|---|---|
| 状态机 / 三振恢复 | ✅ | ❌ | ❌ |
| 编辑前置检查与验证（EditPreChecks / Verification） | ✅ | ✅ | ❌ |
| 工具审批 | ✅ | ✅（继承父权限模式） | ✅（但工具全只读，不会触发） |
| 只读工具白名单 | ❌ | ❌ | ✅ |
| LSP 诊断 / Shell 环境 | ✅ | ❌ | ❌ |
| 工作记忆（FileMemory） | ✅ | ❌ | ❌ |
| Todo（自持清单） | ✅ | ✅ | ❌ |
| CodeAct 沙箱 / 编辑后验证 | ✅ | ✅ | ❌ |

只读白名单（Explore/Plan）：`Read`、`Grep`、`Glob`、`LS`、`Git`、`WebFetch`、`WebSearch`、`ToolSearch`、
`Lsp`——全为 `ToolRisk.ReadOnly`，按 `ToolPolicyDefaults.ForRisk`
永不进审批协议，这是「子代理免交互审批」自洽的根源。

**能力边界传播**：`AgentTool` 把 `ToolActivationContext.CurrentCapabilities`（当前模式的能力集）
作为 `ParentCapabilities` 传入，`ForkedAgentRunner` 与子代理请求的能力取**交集**——子代理可见工具
永远被压在父级边界内。`AllowSubAgents=false` 时 `ToolCapabilitySet.IsAllowed` 直接拒掉
`Agent`/`ParallelAgents`。

**审批 fail-closed**：非交互 fork 无法答复 MAF `ToolApprovalRequestContent`。`ForkedAgentRunner`
检测到挂起审批时返回结构化错误（`AgentProblemDetails`），绝不静默丢工具调用。

---

## 3. 执行链路与任务生命周期

```
AgentTool.RunAgentAsync
  └─ IAgentRunner（WorkerAgentService：TaskService 任务创建/终态/输出落账）
       └─ ForkedAgentRunner.RunForkedAgentAsync
            ├─ SubAgentPipelineFactory.BuildOptions（profile → 安全上下文 → 管道选项）
            ├─ AgentContextPipeline.BuildShared（子代理共享 context provider 集）
            ├─ AgentPipelineBuilder.BuildHarnessAgent（HarnessAgent + 中间件全链）
            └─ 独立 AgentSession 一次性 RunAsync（会话不保留，见 F5）
```

- **事务**：fork 默认自建 `EditTransaction`，成功即 commit；Goal 子目标路径由调用方传
  `SharedTransaction`（多子目标共用，提交/回滚由 Goal 控制面负责）。
- **记账**：子代理经 `PipelineSecurityContext` 共享 `ITokenLedger` 与 `MaxBudgetTokens`，
  预算熔断（`BudgetGuardRunMiddleware`）与用量追踪（`UsageTrackingRunMiddleware`）在子代理链上同样生效。
  用量按当前会话入账（fork 建管道时取 `ISessionIdProvider.CurrentSessionId`），`/status stats` 的
  会话统计包含子代理消耗；上下文提供器不消费该会话 ID（子代理转录保持 fork 本地）。
- **任务清单**：`TaskService` 生产实现**持久化**（conversation / BuildRun 作用域跨重启存活），
  TUI 经 `Task` 工具与任务侧栏可见；输出日志可增量取（`AppendTaskOutput` / `GetTaskOutput`）。
- **上下文 provider 释放**：每 run 构建的 provider（如 skills）经 `AgentContextProviderLease`
  由组装方在最后一次使用后释放。

---

## 4. DAG 语义（ParallelAgents）

`AgentTaskWorkflowCompiler`（MAF Workflows，InProc 执行环境）：

- **校验**（编译期，fail-fast）：任务 ID 非空/唯一、规范化后 executor ID 不撞、依赖存在、
  无重复依赖、无环（Kahn 拓扑排序）。
- **执行**：无依赖任务 fan-out 并发；多依赖任务经 fan-in barrier 汇聚；`ExecutionAccess.Write`
  任务按稳定拓扑序串行，`ReadOnly` 任务可并发（产品语义的「读并行、写串行」）。
- **上游注入**：`injectUpstreamResults=true` 时依赖任务的输出前拼进下游 prompt；
  上游失败 → 下游标记 `Blocked`（不执行、不算失败）。
- **确定性**：拓扑与 `cacheSafeParams`/能力集进入 definition hash，同输入同图。

---

## 5. 自定义子代理（.onecode/agents/*.md）

固定 specialist 角色不想每次重新描述时，用定义文件把代理固化下来：

- **定义文件**：`{cwd}/.onecode/agents/*.md`（项目级）与 `~/.onecode/agents/*.md`（用户级），
  项目级覆盖同名用户级（与 skills 的用户级→项目级覆盖语义一致）。
- **frontmatter 字段**：`name`（缺省取文件名）/ `description`（必填，注入 Agent 工具 description 保证可发现性）/
  `allowed_tools`（可选，与父级能力取交集——只减不增）/ `max_turns`（可选，轮数上限，见 §6）；
  正文 = instructions。
  `allowed_tools` 中的未知工具名在派工时显式拒绝（拼写错误可自诊断）；交集为空（全部拼错或与父级
  能力完全不相交）时 fork 拒绝启动而非静默零工具运行（`ForkedAgentRunner.RejectEmptyAllowedToolSet`）。
- **解析严格**：未知 frontmatter 字段、缺失 description、坏 YAML 显式报错（带文件路径 + 行号），
  不静默忽略；解析失败的定义文件会让派工入口直接报错，修复保存后自动恢复。
- **加载**：启动扫描 + 文件 mtime 签名变更惰性重扫（无 `FileSystemWatcher` 常驻监听）。
- **派工**：`Agent` / `ParallelAgents` 的 `agent` 参数接受自定义名；自定义代理 instructions 接管角色 overlay
  （harness 片段仍无条件注入）。第一版不支持按代理配模型 / profile。
- **嵌套派工上限 1**：所有子代理的工具面无条件摘除 `Agent` / `ParallelAgents`——主 agent 可派工，
  子代理不可再派工（`ForkedAgentRunner.FilterChildTools` + `ToolCapabilitySet.SubAgentDispatchTools`）。

Goal 模式的子目标执行不经 `ForkedAgentRunner`：子目标经 `GoalSubGoalExecutor` 构造 `MainAgentRunOptions`——
`system/harness` 底座 + `ModeInstructionProvider` 注入的 `system/goal` 提示词、按 `RequiredTools` 白名单裁剪工具（**fail-closed**：无匹配即抛错，
不回退全量）、共享事务、`MaxTurnsPerSubGoal`；进度经 `GoalContextProvider` 注入后续子目标上下文。

---

## 6. 轮数与墙钟预算（分层可配 + 优雅收尾）

简单调研跑满 50 轮是真实事故的形态。子代理预算按以下优先级解析（`ForkedAgentRunner` 是唯一解析点）：

| 优先级 | 来源 | 说明 |
|---|---|---|
| 1 | `Agent` 工具的 `maxTurns` 参数 | **只能收窄**下述各层给出的上限，不能抬高 |
| 2 | 自定义代理 frontmatter `max_turns` | `.onecode/agents/*.md`，`AgentDefinition.MaxTurns` |
| 3 | settings `agents.defaults.maxTurns` | `0` = 未配置（自动），可在 `/config` 修改 |
| 4 | profile 内置默认 | Explore / Plan 15，general-purpose / 自定义 50 |

- **轮数口径**：只按模型往返计数（`MaximumIterationsPerRequest` = 上限 + 1，末轮保留给收尾）；
  同一轮里的并行工具调用不重复计数。工具调用另有安全上限（轮数的 4 倍），只拦失控循环不参与正常判定。
- **80% 收尾提醒**：模型调用达到轮数上限的 80%（`TurnBudgetNudgeChatClient`，仅子代理管道装配）时，
  请求末尾注入一次"开始收尾总结"提醒——失控研究循环的根因是模型对预算零可见性，软提醒先于硬上限
  收敛多数循环；提醒只注入一次，确实不理会时由轮数上限兜底。
- **墙钟超时**：`agents.defaults.timeoutMinutes`（默认 10 分钟，≤0 不限制），到时与轮预算同路径收尾。
- **优雅收尾**：模型往返用掉预留的收尾迭代（或工具安全上限触发）即视为预算耗尽；若最后一次迭代没有文本，
  再补一次禁用工具的收尾调用，让子代理总结已完成工作与剩余事项；结果信封标记 `[PARTIAL]`
  （`maxTurnsReached=true`）。`[PARTIAL]` 只陈述事实，父代理应直接使用已有发现，不要重复派发同一任务。
  token 消耗继续计入父会话的 `MaxBudgetTokens`。
- **Explore 停止条件**：Explore 角色指令自带"先广后窄检索、覆盖率足够即总结、连续无新信息即收尾、
  WebFetch 只核实关键事实"约束，预算上限是兜底而非唯一防线。
- **Plan 与 Build 的调研边界**：所有只读探索（代码库定位与按需外部查询）在 PLAN 模式完成，并将确认的文件路径、
  符号签名、架构决定与已核实的外部事实/引用直接写入计划正文；同会话 BUILD 执行轮直接继承规划期对话与工具结果，
  跨上下文压缩与崩溃恢复则通过冻结的审批快照（计划正文 + 用户原话 + 澄清答案）保持上下文完整。计划步骤（`steps`）
  只定义具体的代码/文件变更与验证动作，不把只读检索拆为 BUILD 步骤。Explore 子代理仅用于跨模块代码库探索，
  不委托联网采集。

### 子代理实时进度（TUI）

`Agent` 工具运行期间，转录流上的工具行登记为实时进度行：头行 `Agent · [代理名] · 描述`
（ToolStart 摘要通道把参数收窄为 agent + description 紧凑 JSON，不含 prompt），其下活动行原位刷新
`运行中 · turn N · N 次工具调用 · 当前工具… · 已耗时 Xs`（`ForkedAgentRunner` 经 `SubagentProgressUpdate`
旁路上报，受控 Build attempt 内同样重建该旁路）。展开运行中的行可见任务目标 + 同一份实时统计
（进度快照随头行 tag 更新）；子代理完成/失败时活动行摘除，折叠行按结果重写、展开回到 Args/Result 形态。

---

## 7. 与 MAF BackgroundAgents 的能力映射（速查）

| OneCode | BackgroundAgentsProvider | 结论 |
|---|---|---|
| `Agent`/`ParallelAgents` 工具（确定性入口） | 6 个 `background_agents_*` 模型侧工具 | 不替换（双入口 + 门控绕过，见子代理派工边界 证据 1） |
| profile/能力集/TaskService 闸 | 无（预注册命名 agent 字典） | 产品闸无对应物 |
| DAG（依赖/barrier/上游注入/写串行） | 扁平任务列表 | 无对应物（证据 3） |
| 持久化任务 + per-task stop | 元数据可序列化、在途 `lost`、无取消 | 语义倒置（证据 4） |
| `Task(action=output)` 轮询 | `background_agents_wait_for_first_completion` | **借鉴**：F4 有界等待 |
| 每次全新 session（有意设计） | `background_agents_continue_task` 子会话续跑 | **借鉴**：F5 continue（仅迭代修正场景） |
| 会话结束清理（待补） | `ReleaseSessionAsync`（取消+等待+幂等+标 failed） | **借鉴**：F6 释放语义 |
| `AgentTool` background 的 `Task.Run` | `start_task` 的 `Task.Run` ExecutionContext 隔离 | 已有；F6 补重启态（`lost` 等价物） |

后续工作 F2-F6 的定义与验收标准见 [子代理派工边界「后续工作」](./adr/0011-background-agents-delegation-boundary.md)。

---

## 8. 改这块代码时的检查单

- [ ] 新派工形态先过 `PipelineProfileBehavior`（能力矩阵单一真相源），不要在调用点散落 if-profile。
- [ ] 子代理工具集必须经 `ToolCapabilitySet` 交集压下，不得旁路目录直塞 `ChatOptions.Tools`。
- [ ] 非交互路径的审批必须 fail-closed（`ForkedAgentRunner` 现有检测不得移除）。
- [ ] 新工具/改名同步 `ToolServiceCollectionExtensions`、`docs/commands.md`（如面向用户）与
      `ModePromptToolReferenceTests` 覆盖的模式提示词。
- [ ] 涉及「起不起子代理」的决定权变更，先读 [子代理派工边界](./adr/0011-background-agents-delegation-boundary.md)
      并走 ADR 修订流程。
