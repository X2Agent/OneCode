# ADR 0007: 四模式统一工作流运行时设计

**状态**: Accepted
**日期**: 2026-08-29
**关联**: [ADR 0003](./0003-m4-approval-event-streaming.md)、[ADR 0002](./0002-declarative-workflow-assessment.md)、[docs/plan/web-host-plan.md](../plan/web-host-plan.md)（复用资产清单）
**备注**: 代码中引用的 "ADR 0006 状态对象原则"（QueryStreamEngine/StreamingSession 组合契约）已补写为 [ADR 0006](./0006-query-stream-state-object.md)。

## 语境

OneCode 有四种工作模式（BUILD / PLAN / TEAM / GOAL），各自拥有独立的编排控制面，合计约 **10,700 行**（BuildMode 2,531 + PlanMode 1,893 + Coordinator 4,644 + GoalMode 1,644），Core 层还各配一套领域模型（`Core/Build`、`Core/PlanMode`、`Core/Goals`、`Core/Coordinator`）。计划状态存在三处管理（`PlanWorkflow` 聚合、`BuildRun.Plan` 副本、`TaskService` TaskItem 投影），改一处要同步多处。

### 现状结构对照（逐文件核实的实证）

| 构件 | BUILD | PLAN | TEAM | GOAL | 同构度 |
|---|---|---|---|---|---|
| 聚合根 + 强类型 Id | `BuildRun` | `PlanWorkflow` | `TeamRun` | `GoalRun` | 4/4 |
| 持久化 Store（乐观并发） | `IBuildRunStore` | `IPlanAggregateStore` | `ITeamRunStore` | `IGoalRunStore` | 4/4 |
| `WorkflowFencingToken` + `ClaimWorkflowAsync`/`SaveFencedAsync` | ✓ | ✗（lock 文件代替） | ✓ | ✓ | 3/4 |
| Host/Runtime/lease 回调 claim 三件套 | `ControlledBuildAttempt{Host,Runtime,Workflow}` | ✗ | `TeamTaskWorkflow{Host,Runtime,Compiler}` ×3 | `GoalWorkflow{Host,Runtime,Compiler}` | 3/4（Build≈Goal 逐方法同构） |
| 集中状态机转换表 | `BuildStateTransitionService`（17 态邻接表） | 无（内联+Validator） | `TeamRunStateMachine`（8×8 双轴） | 无（内联守卫） | 2/4 |
| 事件通道 | `durableStateObserver`→`BuildRunStateEvent`（QueryEvent） | `PlanCardPublisher` 专有总线 | `OrchestrationEvent` sink | `OrchestrationEvent` sink | **4 种并存** |
| 审批门机制 | 流内 AskAsync（非持久化门） | 聚合态 + TUI 决策面板 | MAF RequestPort（checkpoint 持久化门） | 无（预算自动暂停） | **4 种各异** |
| 预算维度 | MaxTurns | 无 | MaxTurns + 重试策略 | attempt/token/墙钟 四维三级警告 | 仅 Goal |
| 恢复机制 | `BuildResumePolicy` 纯函数 8 动作 | `PlanExecutionRecoveryService` 轮询 | 指纹对账 + 任务降级重跑 | DefinitionHash fail-closed + checkpoint | 4/4 机制各异 |

**共享平台层已存在**：`DurableWorkflowHost` + `IWorkflowRunRegistry`（fencing/lease/checkpoint 对账）+ `IOperationLedger`（三模式 runId 前缀）+ `IWorkspaceFingerprintProvider`——统一运行时的事实地基。

**计划状态三处管理**：`ApprovedPlanSnapshot`（PlanWorkflow 聚合，Approve 时冻结）→ `PlanAgentRunDispatcher.ToBuildPlan` 转成 `BuildRun.Plan` 副本（编进 DefinitionHash）→ `BuildTaskLinker`/`PlanExecutionTool` 两份平行投影写到 TaskItem。其中只有 `BuildRun.Plan` 副本与双 linker 投影有写语义；TUI 侧投影（`MapPlanCardPhase`/`ProjectSteps`/`BuildRunStateEvent.From` 等）均为纯只读。

### 问题

1. **维护成本**：任何横切变更（fencing、恢复、事件、预算）需要同步 3–4 处同构代码；Build 与 Goal 的 Host/Runtime 三件套已逐方法重复。
2. **行为漂移风险**：拓扑排序曾有 5 处实现、两族算法（Stage 1 已收敛至 `Core/Workflows/WorkflowTopology`）；状态转换、校验、指纹对账仍在 4 处各自演化。
3. **Plan 是隔离孤岛**：唯一未接入 fencing/registry 的模式，跨进程互斥依赖 lock 文件租约，语义与其余三模式不一致。
4. **事件通道碎片化**：同一"运行状态变化"有 4 种发布机制，TUI 侧需要 2 个 mapper + 1 个直订接线才能看全。

## 决策

控制面收敛为「**统一运行时骨架 + 模式策略配置（ModePolicy）**」，持久化 schema 保持各模式独立（兼容红线，见下）。

### 1. `Core/Workflows` 扩为共享内核

- `IWorkflowRunStore<TRun>`：泛型化 Load / Save(expectedVersion CAS) / `ClaimWorkflowAsync(fencingToken)` / `SaveFencedAsync` / `ListActiveAsync`——现有 Build/Goal/Team 三 store 的同构方法签名收编为一份；各模式文件布局与 JSON schema 不变。
- 共享词汇：`RunStepStatus`（pending/in_progress/completed/failed/skipped）+ 四模式枚举映射函数；`RunTerminalReason` 从 `Core/Build` 提升（GoalRun 已在复用）；`WorkflowTopology`（Stage 1 已落地：`DepthFirstOrder` + `KahnOrder`）；fencing 校验 helper。
- Plan 的 CommandId 幂等（`LastProcessedCommandId`）外推为内核可选能力——这是 Plan 最值得输出的资产。

### 2. `App/Services/Runtime`：骨架 + 策略

- `ModeWorkflowRuntime` 骨架（以 Goal 已验证形态泛化）：Bind（fencing 校验）→ Save（Fenced）→ ledger `ReconcileRunAsync`（世代回滚）→ 先持久化 commit 再内存 commit（S-04 顺序）→ 事件发射 → DefinitionHash 锚定。
- `ModePolicy` 每模式一份配置：
  - **审批门 `IApprovalGate` 三实现，语义保留不强行统一**：`InlineAskGate`（Build 现状：流内 AskAsync，崩溃后 resume 重问）、`AggregateApprovalGate`（Plan 现状：聚合 `AwaitingApproval` 态 + TUI 决策面板）、`RequestPortGate`（Team 现状：MAF RequestPort，挂起存活于 checkpoint）。
  - **预算维度开关**：Goal 全开（attempt/token/墙钟 + `GoalBudgetAccountant` 三级警告）；Team/Build 仅 turns；Plan 无。token 预算（`maxBudgetTokens`）由 `BudgetGuardRunMiddleware` 在所有模式统一生效（与模式预算正交）。
  - **质量门管道**：以 Team 的 `ITeamQualityGateValidator` 8-validator 管道为底子泛化；Build 的 `BuildValidationRun`、Goal 的 `GoalCompletionService` 门集合、Plan 的 evidence 校验适配为 validator 集合。
  - **恢复策略函数**：Build 的 `BuildResumePolicy` 纯函数模式推广；指纹漂移 fail-closed（Build/Team/Goal 已一致）。

### 3. MAF 编译器不强行合一

Team DAG（fan-out/fan-in/barrier）、Goal 串行子目标循环、Build 受控 attempt 三种拓扑形状不同，`WorkflowBuilder` 编译器各自保留；但 Host/Runtime/lease-claim 骨架（`RunNextAsync` generation 递增、lease 回调 claim 聚合、`Events.OfType<WorkflowRuntimeEvent.Output>()` 抽取）泛化为共享基类——Build 与 Goal 已逐方法同构，直接受益。

### 4. 事件通道统一到 `OrchestrationEvent`

- `OrchestrationEvent`（Core/Coordinator）提升为唯一的领域事件总线（考虑随共享内核迁至 `Core/Workflows`）。
- Build 的 `durableStateObserver → BuildRunStateEvent` 与 Plan 的 `PlanCardPublisher` 双事件改造为 OrchestrationEvent 发射器。
- `TuiEvent` 保持渲染层契约**不动**（30+ record、`TeamRunSnapshot.Apply`、`TranscriptEventPresenter` 消费面零改动）；`TuiEventMapper` 收敛为唯一映射层，`TuiHostConfigurator` 对 PlanCardPublisher 的直订接线退役。

### 5. 计划状态单源

- `ApprovedPlanSnapshot` 是计划内容的唯一定义源；`BuildRun.Plan` 降为 DefinitionHash 锚定的派生缓存（`PlansMatch` / `MatchesApprovedPlan` 双向一致性校验保留，恢复时仍以校验为准）。
- TaskItem 投影统一为单一 linker：合并 `BuildTaskLinker` 与 `PlanExecutionTool.ReconcileLinkedBuildTasks/ProjectLinkedBuildTask` 两份平行实现（同一 `BuildPlanTaskId` 映射键，同一投影函数）。
- TUI/上下文的纯只读投影（`PlanExecutionContextProvider`、`BuildRunStateEvent.From` 等）并入统一投影层，无行为风险。

### 6. Plan 接入 fencing/registry

`PlanAggregateStore` 引入 `WorkflowFencingToken` + `ClaimWorkflowAsync`/`SaveFencedAsync`：信封 `SchemaVersion=2` 读取分支（v1 旧聚合可读、无 fencing 字段按 0 处理；v2 起写 fence），不识别的 schema 一律 fail-closed——与其余三模式对齐。

## 持久化兼容红线

1. **字段只加不删不改型**：各 store 未启用 `JsonUnmappedMemberHandling`，STJ 默认忽略未知字段——新增字段向后兼容；删除/改型破坏旧快照 resume。
2. **可选参数 + 运行时回退**先例：`GoalBudgetSnapshot.AccumulatedElapsed/LastActivityAt`（Fix-7）。
3. **SchemaVersion bump 处 fail-closed**：`WorkflowRunEnvelope`/`PlanAggregateStore` 信封模式。
4. **测试锚定**：`JsonStore_*` 用例（BuildRunCoordinatorTests L1194-1524）、`WorkflowRunRegistryTests`、`TeamRunTests`（TrySaveAsync）、`GoalRunStoreTests` 必须在每阶段保持全绿。

## 分阶段迁移路线图（每阶段全测试绿、行为不变）

| 阶段 | 内容 | 验证锚 |
|---|---|---|
| **Stage 1（已完成）** | `WorkingMode` 下沉至 `OneCode.App.Modes`；`WorkflowTopology` 收敛 5 处拓扑实现（净删 ~130 行）；本 ADR | WorkingModeTests / ModeDirectKeybindingTests / 各模式测试 |
| **Stage 2** | `IWorkflowRunStore<TRun>` 内核替换 Build/Goal/Team 三 store（文件格式字节级不变）；Plan fencing 接入（SchemaVersion=2） | JsonStore_* / WorkflowRunRegistryTests / GoalRunStoreTests / TeamRunTests |
| **Stage 3** | `ModeWorkflowRuntime` + `ModePolicy` 骨架落地；先迁 Goal（GoalRunStoreTests 5 + GoalWorkflowRuntimeTests 11 + GoalWorkflowTests 5 锚定），再迁 Build（BuildRunCoordinatorTests 51 锚定） | 上述测试平移不改断言 |
| **Stage 4** | Plan/Team 控制面并入（AggregateApprovalGate / RequestPortGate 适配）；事件通道统一收尾（PlanCardPublisher、BuildRunStateEvent → OrchestrationEvent 发射器）；删除四套重复构件；预期控制面 10.7k → 约 6k 行 | MafM4ApprovalEventTests / TeamApprovalWorkflowResumeTests / 全量回归 |

每阶段独立可交付、可回滚；阶段内允许"新骨架 + 旧实现并存一个模式"的过渡态，但禁止跨模式半迁移（同一模式的控制面必须整体切换）。

## 非目标

- 不改 MAF checkpoint 自身格式与 `DurableWorkflowHost` 语义。
- 不改 `TuiEvent` 渲染契约、会话 jsonl schema、各模式聚合的既有字段。
- 不把四模式审批门强行统一为单一机制（三种持久化语义各有价值：耐重启的 RequestPort、可审计的聚合门、零状态的流内门）。
- 不合并 TEAM 的双轴状态机（Phase × Status）到单轴——DAG 编排的维度是领域现实。

## 影响

- 正面：横切变更单点化；Plan 补齐 fencing 与其他模式对齐；事件通道单一化降低 TUI 接线成本；控制面代码量预期净减约 4.5k 行。
- 代价：Stage 2–4 需要约三个独立工作周期；过渡态期间新旧骨架并存要求严格的测试锚纪律；`ModePolicy` 配置面本身有学习成本（以 ADR 表格与代码注释约束）。
