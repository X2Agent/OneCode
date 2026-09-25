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
| `Agent` | 起一个子代理跑委派任务，返回其最终输出 | `prompt`（自包含）、`agent`（`general-purpose`/`Explore`/`Plan`）、`description`、`runInBackground` | `ToolRisk.Safe`、`Contextual`（keywords: agent/sub-agent/delegate）、`PlanAllowed` |
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

| 能力 | Full（主） | Worker | TeamMember | Explore / Plan |
|---|---|---|---|---|
| 状态机 / 三振恢复 | ✅ | ❌ | ❌ | ❌ |
| 行为契约（改后必验） | ✅ | ✅ | ✅ | ❌ |
| 工具审批 | ✅ | ✅（继承父权限模式） | ✅（固定 `PermissionMode.Team`，经 Team 工作流桥接） | ✅（但工具全只读，不会触发） |
| 只读工具白名单 | ❌ | ❌ | ❌ | ✅ |
| LSP 诊断 / Shell 环境 | ✅ | ❌ | ✅ | ❌ |
| 工作记忆（FileMemory） | ✅ | ❌ | ❌ | ❌ |
| Todo（自持清单） | ✅ | ✅ | ✅ | ❌ |
| CodeAct 沙箱 / 编辑后验证 | ✅ | ✅ | ❌ | ❌ |

只读白名单（Explore/Plan）：`Read`、`Grep`、`Glob`、`LS`、`WebFetch`、`WebSearch`、`ToolSearch`、
`FindReferences`、`SymbolSearch`——全为 `ToolRisk.ReadOnly`，按 `ToolPolicyDefaults.ForRisk`
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

## 5. Team / Goal 的派工（同一套原件的不同编排）

- **Team**：成员经 `TeamAgentFactory` 以 TeamMember profile 构建；`AllowedTools` =
  成员声明 ∩ 任务要求（`ReadOnly` 工具策略时取只读白名单）；编排由 MAF Workflows 承担
  （GroupChat 轮询 / Magentic orchestrator 委派 / ParallelDag 单成员 Sequential）；
  成员审批请求作为工作流外部请求经 `TeamWorkflowRunner` 桥接到产品审批事件。
- **Goal**：子目标经 `GoalSubGoalExecutor` 构造 `MainAgentRunOptions`——
  `system/goal-subgoal` 提示词、按 `RequiredTools` 白名单裁剪工具（**fail-closed**：无匹配即抛错，
  不回退全量）、共享事务、`MaxTurnsPerSubGoal`；进度经 `GoalContextProvider` 注入后续子目标上下文。

---

## 6. 与 MAF BackgroundAgents 的能力映射（速查）

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

## 7. 改这块代码时的检查单

- [ ] 新派工形态先过 `PipelineProfileBehavior`（能力矩阵单一真相源），不要在调用点散落 if-profile。
- [ ] 子代理工具集必须经 `ToolCapabilitySet` 交集压下，不得旁路目录直塞 `ChatOptions.Tools`。
- [ ] 非交互路径的审批必须 fail-closed（`ForkedAgentRunner` 现有检测不得移除）。
- [ ] 新工具/改名同步 `ToolServiceCollectionExtensions`、`docs/commands.md`（如面向用户）与
      `ModePromptToolReferenceTests` 覆盖的模式提示词。
- [ ] 涉及「起不起子代理」的决定权变更，先读 [子代理派工边界](./adr/0011-background-agents-delegation-boundary.md)
      并走 ADR 修订流程。
