# Team 模式砍除与协作能力收敛计划

> 状态：**已评审通过（2026-10-04 按代码逐项复核修订），执行中**。本文档是唯一执行依据。
> 范围：①补齐协作替代能力；②删 Team 工作模式与 TeamRun 控制面；③新增子代理自定义（`.onecode/agents/*.md`）。
> BUILD / PLAN / GOAL 模式不受影响。

---

## 1. 评估结论

### 1.1 核心判据：14 次运行，0 次成功

`~/.onecode/team-runs/` 下 14 个 run 聚合根的终态分布：

| `TeamRunStatus` | 枚举值 | 数量 |
|---|---|---|
| Running（卡死残留） | 1 | 4 |
| Cancelled | 7 | 10 |
| **Succeeded** | 4 | **0** |
| Failed | 5 | 0 |
| RolledBack | 6 | 0 |

任务级内容全部为：`"Summary": "(no output)"`、`"Failure.Detail": "Team workflow completed without any agent response."`、`ErrorFingerprint` 恒为同一个 `429d9ba9e02bc61f`；`GateResults: []`、`TransactionCommitted: false`、`Delivery: null`。

即**这套编排框架从未端到端跑通过一次**。`MagenticReproTests.cs:110-116` 的注释自认该症状：

> 固化 MAF 1.19.0 ChatProtocol 语义：单成员 SequentialWorkflowBuilder 用 RunStreamingAsync 直接投递消息不会触发 agent（executor 只累积对话，等 TurnToken），表现为零 LLM 调用、turns=0、`(no output)`——**这正是 TeamWorkflowRunner 曾在线上踩到的 ParallelDag 静默失败**。

磁盘上 14 条 `(no output)` 就是这段注释的物证残留。

### 1.2 病根：把「机制完备性」误当「产品价值」

6000 行实现中，工程量集中在**控制面的完备性**——跨进程世代恢复、fencing token 双向对账、提交顺序防崩溃不一致（`S-04`）、C2 工作区指纹对账后降级重跑、可挂起审批门、任务状态机。这些机制本身质量很高、测试覆盖很厚。

而「让 agent 真的跑起来」反倒依赖一个测试文件里承认的 bug workaround。**控制面越完备，越掩盖了执行面从未通电的事实。**

### 1.3 三种协作形态的取舍

| 形态 | 内容 | OneCode 现状 |
|---|---|---|
| A 独立视角并行 | 多视角 review、多方案对比；价值来自上下文隔离 + 视角独立 | **需保留**（Team 的 code-review 是唯一贴合场景） |
| B 主 agent 动态派工 | 按需拆子任务给子代理，保住主线上下文预算 | **已具备**：`Agent` / `ParallelAgents` 工具 |
| C 模拟人类团队分工 | 固定成员角色 + GroupChat/Magentic 编排 + 任务状态机 | Team 模式押注的形态，实证收益为零（§1.1） |

### 1.4 砍除 Team 模式的四条理由

1. **从未通电**：14 个 run 无一收敛，任务级全 `(no output)`（§1.1）。这是最强判据——它不依赖读者认同任何架构品味，只依赖可复验的磁盘事实。

2. **三拓扑两假一窄**：
   - *ParallelDag* 名为扇出实为单成员串行薄封装，代码四处自认 `never Concurrent true-parallel`（`TeamWorkflowRunner.cs:88-91,120,139`），`docs/adr/0007` §2 另有正式禁令。
   - *GroupChat* 靠加载期注入「禁止附和、无贡献回 `[PASS]`」协作协议（`TeamConfigLoader.cs:70-82`）+ 「最后一条无实质发言」启发式提前终止（`TeamWorkflowRunner.cs:303-317`）打补丁，是给 MAF 空转病理止血的自我否定。
   - *Magentic* ≈ 主 agent 用 `Agent` 工具派工，仅换了委派决策持有者；且内部 plan review 被**无条件自动批准**（`TeamWorkflowRunner.cs:156-158`）。
   - `ParallelAgents` 反而是真 DAG 并行（MAF `AddFanOutEdge` / `AddFanInBarrierEdge`，`AgentTaskWorkflow.cs:118,129`），带编译期 Kahn 查环、读并行/写拓扑序串行、上游结果注入。**Team 最像「多角色协作」的能力，工具层已用更简单方式做到。**

3. **复杂度税，而非幻影并发**：
   > ⚠️ 本条修正了初版论证。初版从「运行时实际串行」推出 `EditTransaction` / fencing token / 工作区指纹是「为假想的并发形态而建」——**该论证不成立**：`EditTransaction` 不是 Team 独有（`AgentPipelineBuilder.cs:398` 中间件、`ForkedAgentRunner.cs:66-67,179` 每 fork 自建并 commit、Goal 子目标共享事务），删 Team 不带走它；且 `TeamTaskWorkflow.cs:331-333` 自述同 superstep 兄弟 Executor **确实会并发启动**（「read-only task backoff does not block sibling Executors」），「运行时实际串行」是过度断言。

   正确论证是**复杂度税**：这套纵深防御是为「多成员共享同一 run 级事务 + 可挂起审批门 + 跨进程世代恢复」这一组场景付的税，而该场景在当前产品形态下从不出现——§1.1 即实证。

4. **自定义体验粗糙且方向错位**：磁盘上出现过自建团队 `feature-impl`，说明「固定几个 specialist 不想每次重新描述」是**真诉求**；但正确形态是按代理定义文件（30 行 frontmatter 解析），而非 team.yaml 编排拓扑。现有 team.yaml 无 validate、未知字段静默忽略（`IgnoreUnmatchedProperties`）、成员不能配模型/profile、无项目级、不能点名成员。

### 1.5 唯一不可替代能力的归属

TeamRun 聚合的**跨进程可恢复任务图 + 可挂起强制审批门 + 事务回滚 + 交付报告**，仅在「长时、可中断、无人值守大型任务」场景有真实价值。该场景已由官方路线押给别处：

- `docs/adr/0013` 决策 1「TUI 是唯一交互前端」、决策 4「定时任务与后台执行进无头守护进程」；
- `docs/plan/tui-roadmap.md` P2 的 `onecode daemon` + cron + OS 通知（全文零 Team 命中）；
- 自主长时循环归 GOAL 模式（复用同一套 `Core/Workflows/` 底座）；
- `docs/adr/0011` 「决策 §5 重评触发条件」第 2 条（L185-188）明确：tui-roadmap 的后台会话/attach/守护进程复用既有 QueryStreamEngine / 权限 / Build 门禁，**不构成**重新评估触发条件。

且审批门有一处自证失效：Team 唯一的「强制审批门」连自己 Magentic 内部的 plan review 都没护住（无条件自动批准）。子代理侧只有 fail-closed 报错（`ForkedAgentRunner.cs:144-164`），PLAN 侧 `SubmitPlan` 门 gate 的是主 agent 计划而非子代理写操作——但需如实标注：**可挂起且跨重启续答**这一点上，Team 仍是全项目唯一实现。

本决策不代表「多代理编排永远不做」；若未来押注无人值守长任务，再按收敛形态重建。

---

## 2. 甄别结论：共享底座保留清单（防误伤）

以下文件虽被 Team 引用或物理位于 Coordinator 相关目录，但经使用方核查（全仓 rg 类型名）**保留**：

| 文件 | 共用方 | 备注 |
|---|---|---|
| `Core/Workflows/`（WorkflowRunStore / WorkflowRunModels / WorkflowTopology / WorkflowDefinitionHash） | `IBuildRunStore`、`IGoalRunStore` 都建在它上面 | |
| `App/Services/Runtime/ModeWorkflowHost.cs` | GoalMode `GoalWorkflowHost` 等 | 仅改 L14 doc 注释中的 Team 引用 |
| `App/Services/Runtime/BuildResumePolicy.cs` | Build/Resume | 仅改 L40 Team 注释 |
| `App/Services/Streaming/OrchestrationEventBus.cs` | TodoProjectionService、PlanCardPublisher、TuiHostConfigurator | |
| `App/Services/ClarificationInteractionService.cs` | BuildMode `BuildRunGate`；注册在 `ToolServiceCollectionExtensions.cs:87`，与 AddTeamServices 无关 | |
| `Core/Coordinator/OrchestrationEvent.cs` | 事件总线载体 | 只删 5 个 Team 专属 record（含 `AgentCoordination`）；`AgentMessage` 虽注释标「Team 专有」但 `AgentWorkflowEventProcessor.cs:83,114`（共享管线）在发射，**保留**、仅改注释 |
| `Core/Config/ModeBudgetSettings.cs` | MaxTurns 为 Build/Team 共用，仅改注释 | |
| `Infrastructure/Tools/GenericVerificationProvider` | `EditGuardMiddleware.cs:21`、`AgentPipelineBuilder.cs:108`、`AgentPipelineOptionsFactory.cs:24`、`GoalServiceCollectionExtensions.cs:34,41`（引用的是 `IVerificationProvider` 接口） | |
| `App/Services/Agent/ForkedAgentRunner.cs` | **Agent 工具 → IAgentRunner 全部子代理链路**（生产唯一消费方 `WorkerAgentService`） | |
| `App/Services/Coordinator/WorkerAgentService.cs` | Agent 工具的 IAgentRunner，**需迁出目录**至 `App/Services/Agent/` | |
| `App/Services/Agent/AgentWorkflowEventProcessor.cs` | 共享管线事件处理器 | 仅改 L91/102 日志文案；`AgentMessage` 发射保留 |

**初版曾误判、现已核实为 Team 专属（随 Team 删除）**：

| 文件 | 核实证据 |
|---|---|
| `App/Services/Runtime/RequestPortGate.cs` | 唯一消费方 `TeamOrchestrationService(.Gates)`；GoalWorkflowHost 用的是 `ModeWorkflowHost` |
| `App/Services/Runtime/WorkflowQualityGateRunner.cs`（内含 8 × `IWorkflowQualityGateValidator` 与 `DeliveryReportBuilder`，非独立文件） | 生产消费方仅 Team 链路（TeamRunApplicationService / TeamRunFinalizer / TeamRunStateMachine / TeamRequirementService / TeamRun / Team 交付投影）；CreatePlanTool 的 `PlanContentQualityGate` 是文件内独立静态类，与此无关 |
| `App/Services/ClarificationQuestionGenerator.cs` | 唯一消费方 `TeamRequirementService`；Build 澄清链用的是 `IClarificationInteractionService`（另一服务） |

### 2.1 `AddTeamServices` 注册拆分

`TeamServiceCollectionExtensions.cs`（82 行）单个 `AddTeamServices` 共 **34 项注册 = 5 共享 + 29 Team 专属**。

**5 项共享注册全部迁入既有 `AgentServiceCollectionExtensions.AddAgentRuntimeServices`**（组合根 `ClaudeCodeApp.cs:77` 已调用，无需新建扩展类）：

| 注册项 | 迁移理由 |
|---|---|
| `IVerificationProvider` → `GenericVerificationProvider` | 全仓唯一注册点；Build/Goal 质量验证与子代理编辑守卫依赖 |
| `ForkedAgentRuntimeDependencies` | 子代理执行引擎依赖束 |
| `ForkedAgentRunner` | 子代理执行引擎 |
| `WorkerAgentService` | Agent 工具执行器 |
| `IAgentRunner` → `WorkerAgentService` | AgentTool / ParallelAgents DAG executor 的服务契约（全仓唯一注册点；注意 ForkedAgentRunner 也实现该接口但未注册） |

其余 29 项为 Team 专属（17 个 `Team*` 类型 + `ITeamRunStore→JsonTeamRunStore` + `IClarificationQuestionGenerator→ClarificationQuestionGenerator` + `RequestPortGate` + 8 validator + `WorkflowQualityGateRunner` + `DeliveryReportBuilder`），随阶段 C 一并删除。

---

## 3. 替代方案（删除后由什么承接）

| 原有 Team 能力 | 承接者 | 状态 |
|---|---|---|
| 三视角并行只读 review | `/review` 扩展为并行三视角 + 聚合去重 | **需开发**（§5.1），`/review` 主体已存在 |
| 固定 specialist 角色（自定义团队） | `.onecode/agents/*.md` frontmatter 定义 | **需开发**（§5.2） |
| 动态派工 / 真并行 DAG | `Agent` / `ParallelAgents` | 已具备 |
| 上下文隔离（子代理独立会话） | `ForkedAgentRunner` 独立 session | 已具备 |
| 读并行 / 写串行冲突规避 | `ParallelAgents` 拓扑序串行 + 共享 `EditTransaction` | 已具备 |
| 交付报告 / 质量门 | **不承接**：经核查为 Team 专属（§2），Team 外无任何消费方，从未对 Team 外暴露过能力；未来押注无人值守长任务时按收敛形态重建（§1.5） | 随 Team 删除 |
| 跨进程恢复 + 可挂起审批门 | 无直接承接 | **不承接**：由 daemon / GOAL 路线覆盖，非当前主线（§1.5） |

**净能力变化**：失去「多成员共享 run 级事务 + 可挂起审批门」这一组从未被实际使用的编排能力；获得更快更准的三视角评审、可自定义的专家角色、更强的子代理嵌套安全。**不以功能回退为代价。**

---

## 4. 对现有功能的影响面

| 受影响面 | 影响 | 处置 |
|---|---|---|
| BUILD / PLAN / GOAL 三模式 | **零影响** | 回归测试须全绿 |
| `/review` 命令 | 参数语义全部保留，新增并行能力 | 扩展实现 |
| `/team` 命令、`Alt+3`、`Shift+Tab` | 移除 | 用户可见变更，须同步 docs/keybindings.md |
| Tab 模式循环 | 由 4 档变 3 档 | `CycleMode()` 取模改 3 |
| `PermissionMode.Team` 档位 | 移除 | settings.md 同步 |
| `PipelineProfile.TeamMember` 档 | 移除 | docs/sub-agents.md 能力矩阵同步 |
| 右侧 Team 侧边栏（TUI） | 移除 | Plan 侧边栏逻辑不受影响 |
| 子代理派工 | **能力增强**（自定义角色 + 嵌套深度守卫） | 新增测试守护 |
| DI 容器 | 减少 29 项注册 | `ServiceRegistrationSnapshot.approved.txt` 走重生成流程 |
| 启动性能 | 略快（少 29 项注册 + 8 个单例 validator） | — |

---

## 5. 执行清单

**四阶段，顺序不可颠倒**（阶段 A 必须先于阶段 C，理由见 §7）。

### 5.0 前置

1. 权限模式退出 plan mode。
2. checkpoint 提交全部在制改动，使各阶段提交干净可回滚。
3. 全程直接在 main 提交，删除类改动分主题提交，不与功能新增混流。

### 阶段 A：先补能力（避免中间态回退）

> 初版计划把补能力排在删除之后，中途停手会造成 code-review 从「有但串行慢」变「无」。本计划将补能力提前，使任一提交点均可用。

#### 5.1 `/review` 并行多视角（形态 A 承接）

`src/OneCode.App/Commands/ReviewCommand.cs` 已存在（390 行），已有 `--focus security|crashes|performance|style` 四个专用 prompt（`FocusToPromptName` L51-57，映射 `prompts/system/review-*.prompt`）、细粒度工具白名单（L59-69）、`--severity/--blame/--no-edit/--base`、LSP diagnostics 聚合、`ReviewCacheService` 增量缓存。README_CN.md:193 亦已把 `/review --staged` 作为代码审查推荐入口。

正确做法：**在现有命令内把「单视角串行」扩展为「三视角并行 + 聚合去重」**，复用既有参数语义、缓存与上下文聚合。

- 复用 `AgentTaskWorkflowCompiler`（与 `ParallelAgentsTool` 同一 DAG 底座）+ 三个只读分支 + 一个聚合任务；写任务拓扑序串行。
- 三视角复用既有 `review-security` / `review-performance` / `review-style` prompt（`prompts/system/*.prompt`），仅新增一份聚合 prompt（进三层覆盖体系）。
- 新增守卫测试：视角派发数量为 3、全部只读工具边界（无 `Edit`/`Write`/非 git `Bash`）、视角 prompt 隔离、聚合去重。

#### 5.2 子代理自定义（「自定义团队」的正解）

- **定义文件**：`{cwd}/.onecode/agents/*.md`（项目级）与 `~/.onecode/agents/*.md`（用户级），frontmatter 字段 `name` / `description` / `allowed_tools`，正文 = instructions。项目级覆盖同名用户级——对齐 `SkillCatalog.cs:27-29`（用户级→项目级双目录组合）与 `SkillDiscovery.cs:27-29`（「later directories override earlier ones」）的既有覆盖语义，不发明新规则。解析手法复用 `SkillFrontmatterParser`。
- **解析错误必须显式报错并带文件名 + 行号**，吸取 `team.yaml` `IgnoreUnmatchedProperties` 静默忽略的教训。
- 加载：启动扫描 + 目录 mtime 变更惰性重扫（不引入 `FileSystemWatcher` 常驻监听）。
- 派工：`Agent`（参数名 `agent`，默认 `"general-purpose"`）/ `ParallelAgents` 的 `agent` 参数扩展接受自定义名；`allowed_tools` 与父级能力取交集（复用 `ToolCapabilitySet.Intersect` L37-50，只减不增）；自定义代理清单注入 Agent 工具 description 以保证可发现性。
- **第一版不做**：按代理配模型（全项目主模型一致）、按代理指定 profile。

#### 5.3 嵌套派工深度上限

`ForkedAgentRunner` 生产唯一消费方是 `WorkerAgentService`（`IAgentRunner` 单例），全部执行路径都是主 agent 派工的子代理（GOAL 子目标执行器不经它——GoalMode 目录零 `IAgentRunner` 引用）。因此**不新增 Depth 字段、不引入 AsyncLocal 环境流**：

- `ForkedAgentRunner.RunForkedAgentAsync` 工具过滤（L71-73）追加排除 `Agent` / `ParallelAgents`（无条件——所有 fork 都是子代理），即实现「派工深度上限 1」。
- 上限硬编码，**不新增配置项**（过度参数化反模式）。
- 新增守卫测试（`ForkedAgentRunnerTests`）：子代理工具集无派工工具、其余工具保留。

### 阶段 B：拆分 DI 注册（见 §2.1）

1. 5 项共享注册迁入既有 `AgentServiceCollectionExtensions.AddAgentRuntimeServices`：`IVerificationProvider`、`ForkedAgentRuntimeDependencies`、`ForkedAgentRunner`、`WorkerAgentService`、`IAgentRunner`。
2. `WorkerAgentService.cs` 从 `App/Services/Coordinator/` 迁至 `App/Services/Agent/`（命名空间同步）。
3. `TeamServiceCollectionExtensions.cs` 删除（共享项迁走后仅剩 Team 专属项）。
4. `ClaudeCodeApp.cs:86` 摘除 `.AddTeamServices()`；测试组合根 `ServiceCollectionSnapshotTests.cs:174` 同步。
5. 重生成 `ServiceRegistrationSnapshot.approved.txt`。

### 阶段 C：删除 Team 实现

#### 5.4 整文件删除

**`App/Services/Coordinator/`（28 个 Team* 文件）**：TeamAgentFactory、TeamAgentPipelineDependencies、TeamAgentToolSources、TeamApprovalWorkflow、TeamApprovalWorkflowHost、TeamCapabilityProfile、TeamClarificationResponses、TeamClarificationWorkflow、TeamConfigLoader、TeamCoordinatorModels、TeamFileChangeObserver、TeamOrchestrationService.Gates、TeamOrchestrationService、TeamRegistry、TeamRequirementService、TeamResultAggregator、TeamRunApplicationService、TeamRunErrors、TeamRunFinalizer、TeamRunGuards、TeamRunStateMachine、TeamServiceCollectionExtensions（阶段 B 已删）、TeamTaskProgress、TeamTaskWorkflow、TeamTaskWorkflowHost、TeamTaskWorkflowRuntime、TeamWorkflowRunner、TeamWorkflowTopology。`WorkerAgentService.cs` 阶段 B 已迁走；目录清空后删除。

> **删除前逐个 rg 类型名确认无 Team 外使用方**——这是本次最高危项（§2.1 即由此发现）。

**其他**：
- `Core/Coordinator/`：`TeamRun.cs`、`ITeamOrchestrationService.cs`（`OrchestrationEvent.cs` 保留）
- `App/Services/Runtime/`：`RequestPortGate.cs`、`WorkflowQualityGateRunner.cs`（含 8 validator、`DeliveryReportBuilder`、`QualityGateStatus`）
- `App/Services/ClarificationQuestionGenerator.cs`（接口与实现同文件）
- `Infrastructure/Teams/`：整个目录（`JsonTeamRunStore.cs` 291 行）
- `App/Commands/TeamCommand.cs` + `AddCommands()` 注册项（`CommandServiceCollectionExtensions.cs:59`）
- `App/prompts/teams/` 三个内置 YAML（code-review 45 / research 48 / impl 53）
- `App/prompts/system/team.prompt`（38 行）
- TUI：`TeamSidebarView.cs`、`ChatBlockRenderers.TeamSidebar.cs`、`ReplShell.TeamSidebar.cs`、`TeamRunSnapshot.cs`
- **测试**（15 文件约 3470 行）：MafTeamAndWorkflowTests、ParallelDagTeamTests、TeamTaskWorkflowTests、TeamTaskWorkflowRuntimeTests、TeamRunTests（单文件含 TeamRunStateMachineTests / TeamRequirementServiceTests / WorkflowQualityGateRunnerTests / JsonTeamRunStoreTests / TeamRunApplicationServiceTests 5 个类）、TeamRunErrorsTests、TeamRunRecoveryTests、TeamApprovalWorkflowTests、TeamApprovalWorkflowResumeTests、TeamClarificationWorkflowResumeTests、TeamToolApprovalBridgeTests、TeamFileChangeObserverTests、TeamSidebarTests、MagenticReproTests、ClarificationQuestionGeneratorTests
- **保留**：`TestSupport/WorkflowRecoveryProbe.cs`——已核实为通用 MAF 恢复探针（自建 DeterministicAgent 工作流，不构造任何 Team 类型），被 CrossProcessCheckpointRecoveryTests / RunLeaseFencingTests / CheckpointStoreFaultInjectionTests 三个非 Team 测试使用。

> **MagenticReproTests 处置说明**：该文件固化了 MAF ChatProtocol 的真实语义（单成员 Sequential 直接投递消息不触发 agent、需两段式启动 + TurnToken），是**跨模式有效的 MAF 行为知识**。将其中的 MAF 语义测试迁入 `AgentTaskWorkflow` 相关测试文件保留，仅删除 Team 专属部分。

#### 5.5 外科手术（保留文件、摘除 Team 分支）

> 原则：能用整文件删的一律整文件删；仅当摘除 Team 分支后剩余逻辑仍自洽且简单时才保留文件（用户规则：开发阶段、不留兼容层、不留死代码）。

| 文件 | 动作 |
|---|---|
| `App/Modes/WorkingMode.cs` | 枚举删 `Team` 与 `ModeTag` Team 分支；`CycleMode()` 取模 4→3（无数值持久化，删枚举成员安全） |
| `App/Services/WorkingModeBridge.cs` | 删 `PermissionMode.Team` 映射与 Team 分支 |
| `Core/Permissions/PermissionMode.cs` / `PermissionProfiles.cs` | 删 Team 档位、Team 审批文案与 `DestructiveShellPolicy.Ask` 注释 |
| `Core/Permissions/PermissionCheckHelpers.cs` | 无代码分支，仅改 doc 注释（L100/107/121） |
| `Infrastructure/Agent/PipelineProfile.cs` | 删 `TeamMember` 档（`GetRoleInstruction` 对其本就返回 null，无需改签名） |
| `App/Services/Streaming/QueryStreamService.cs` | 删 TEAM 分发块（L80 错误文案 `Use /team register to add a team` 是幽灵引用——`/team` 无 register 子命令——随之消灭） |
| `App/Services/Streaming/OrchestrationStreamService.cs` | 删 `StreamTeamAsync`（L169-246）、`StreamResumeTeamAsync`（L361）与 Team 事件投影（`TuiTeamDelivery`/`TuiTeamProgress`/`BuildTeamFailureSummary`/L262 `QualityGateStatus`）；`StreamGoalAsync` 保留 |
| `Core/Coordinator/OrchestrationEvent.cs` | 删 5 个 Team 专属 record：TeamTaskProgress / TeamClarificationRequest / TeamPlanApprovalRequest / TeamUserResponse / **AgentCoordination**；`AgentMessage` 保留、改「Team 专有」注释 |
| `Core/Keybindings/KeybindingDefaults.cs` | 删 `ActionAppModeTeam`(L55) + `alt+3`(L245) + `ActionChatCycleTeam`(L80) + `shift+tab`(L239) + 两处描述文案(L143/L161) |
| `Core/Config/ModeBudgetSettings.cs` | 仅改注释（MaxTurns Build 仍用） |
| TUI 长尾 | `ChatInputView(.Keys)`（CycleTeamRequested 事件 L105、Keys L218-220）、`OneCodeToplevel(.Events/.Query/.cs)`（OnCycleTeamRequested L105,199-215；`.Events` L172 质量门投影随 Team 投影删）、`TuiContext`（L38,109）、`TuiContextFactory`（在 `App/Services/`，L96-111）、`AgentStatusBar`（L34-35,160-168,347-360,370）、`SessionContextBar`（L48,58-60,284）、`SidebarViewBase`、`TranscriptEventPresenter`（L43-48 只删 TuiAgentCoordination 分支，TuiAgentMessage 保留）、`TuiEvent`（删 Team 组与 `TuiAgentCoordination`，`TuiAgentMessage` 保留）、`TuiEventMapper`、`TuiPalette`、`PermissionPromptTypes`、`ChatTranscriptView.Rendering` |
| Agent 管线长尾 | `AgentContextPipeline`、`AgentWorkflowEventProcessor`（L91/102 日志文案去 Team 化，`AgentMessage` 发射保留）、`ApprovalBroker`（`ForTeam` 工厂）、`CompactionStrategyFactory`、`GoalAgentModels`、`MainAgentContracts`、`MainAgentRunner`、`MainModeContextProviderBuilder`、`SubAgentPipelineFactory`、`PromptComposer`（在 `App/Services/` 根） |
| 其余长尾 | `ClaudeCodeApp`、`AppStartupService`（`RegisterBuiltinTeamsAsync` 整方法）、`QueryStreamEngine`（L528 注释）、`BuildResumePolicy`（L40 注释）、`CheckpointCommand`、`ResumeCommand`（`ITeamRunStore` 参数 + `WorkflowResumeKind.Team`）、`PermissionsCommand`（`["team"]` 映射）；Core：`AgentCapability`/`ICommand`/`FileChange`/`ApprovalDecision`/`ApprovalRequest`；Infrastructure：`AgentPipelineBuilder`、`AgentPipelineOptionsFactory`、`OneCodeHarnessDefaults`、`PromptTooLongRecoveryRunMiddleware`、`PermissionAndLimitMiddleware`、`DurableFile`、`FencedLedgerTransaction`；`ModeWorkflowHost.cs` L14 注释、`AutoDreamService.cs` L300 注释 |
| 测试长尾 | `WorkingModeTests`、`ModeDirectKeybindingTests`、`ModePromptToolReferenceTests`、`ServiceCollectionSnapshotTests`（DI 快照重生成）、`TestAgents`、`AgentContextPipelineTests`、`AgentPipelineBuilderTests`、`ChatBlockRenderersTests`、`ChatTranscriptViewTests`、`SessionContextBarTests`、`TranscriptEventPresenterTests`、`TranscriptWidthAdaptiveTests`、`CheckpointResumeWorkflowTests`、`MafM4ApprovalEventTests`、`MainAgentRunnerTests`、`ResumeCommandTests`、`PermissionCheckerBoundaryTests`、`HarnessInstructionsWiringTests`、`FileMemoryProfilePolicyTests`、`WorkflowKernelVocabularyTests` |

> 长尾断言更新原则：按失败信息逐项更新断言，**不得删除断言本身**。

### 阶段 D：文档清扫

#### 5.6 文档与 ADR（实际 28 份 Markdown 含 Team，全清单）

- 删除 `docs/team-modes.md`。
- **ADR 状态标注**（顶部加一行状态标注指向本次移除，正文保留决策语境；ADR 中纯现状描述的段落改正为现状）：`docs/adr/0001`、`0002`、`0003`、`0004`、`0007`、`0009`、`0011`、`0012`、`0013`（9 份）。
- **现状改写**：`README.md`（L7 四模式）、`README_CN.md`（11 处）、根 `AGENTS.md`、`src/OneCode.App/AGENTS.md`、`src/OneCode.Infrastructure/AGENTS.md`、`src/OneCode.Tests/AGENTS.md`（4 份 AGENTS.md；`src/AGENTS.md` 零命中不动）、`src/OneCode.App/Tui/DESIGN.md`、`docs/commands.md`（删 L449 /team 小节 + L469/L1130 别名，补 `/review` 三视角并行语义）、`docs/sub-agents.md`（§2 能力矩阵删 TeamMember 列，§5 重写为子代理自定义机制 + 文件格式与覆盖规则）、`docs/settings.md`（L105 team 档 + L108/L111 派生说明）、`docs/keybindings.md`（L156-157 Shift+Tab 行与 Alt+1..4 行处置说明）、`docs/compact-thresholds.md`（L24）、`docs/hooks.md`（L1042）、`docs/memory-overview.md`（L33/L65）、`docs/testing/manual-checklist.md`（删 Team 手测项，补 `/review` 三视角与自定义代理两项）、`docs/maf/integration-guide.md`（L656/710/748）。
- `docs/plan/keybindings-full-keyboard-plan.md`、`docs/plan/ollama-small-model-abc-plan.md` 按现状改正 Team 引用。
- 执行完成后本文档（执行计划）自身删除——变更历史交给 git。

#### 5.7 清扫验证（须零命中）

```bash
rg -i "TeamOrchestration|TeamRun|WorkingMode\.Team|PermissionMode\.Team|PipelineProfile\.TeamMember|/team\b|TEAM 模式|team\.yaml|cycleTeam|TeamMember|TeamSidebar|AddTeamServices|JsonTeamRunStore|ITeamRunStore|TeamOrchestrationMode|TeamRunStateMachine|TeamTaskWorkflow|TeamApprovalWorkflow|TeamClarificationWorkflow|team-runs|prompts/teams|AgentCoordination|RequestPortGate|ClarificationQuestionGenerator|WorkflowQualityGate|DeliveryReportBuilder|QualityGateStatus|RegisterBuiltinTeams" src/ docs/ README_CN.md README.md AGENTS.md
```

> 追加的 7 个符号来自 §2 复核新判定的 Team 专属类型。`WorkflowQualityGate` 等具体前缀避免误报 CreatePlanTool 的 `PlanContentQualityGate`（保留项）。排除 `bin`/`obj`。

#### 5.8 用户级数据

`~/.onecode/team-runs/` 下 14 个存量 run JSON 属本地开发数据，代码删除不影响；代码中不再存在 `team-runs` 路径引用后即成为孤儿数据，不做迁移。

---

## 6. 验收标准

1. `dotnet build src/OneCode.slnx` 无新增警告；`dotnet test src/OneCode.slnx` 全绿（`FileIgnoreTests` 本机必失败为已知存量，不算新增）。
2. §5.7 清扫 rg 零命中幽灵引用。
3. 手测：Tab/Shift+Tab 模式循环不再出现 TEAM；`/review` 三视角并行输出且聚合去重；自定义 `.onecode/agents/*.md` 后 Agent 工具可按名派工且工具边界正确；子代理不可递归派发；BUILD/PLAN/GOAL 回归正常；`/resume` 只余 Goal。

---

## 7. 风险与回滚

- 所有删除在 checkpoint 提交之后进行，`git revert` / `git reset --hard <checkpoint>` 即可整体回滚。
- **最高风险：`AddTeamServices` 混合注册误伤**（§2.1）——以 §2 保留清单为准绳；任何文件删除前先 rg 其类型名确认无 Team 外使用方。经复核，共享注册仅 5 项且全部迁入 `AddAgentRuntimeServices`；质量门/RequestPortGate/ClarificationQuestionGenerator 已核实为 Team 专属，不存在「迁一半留一半」的中间态。
- 次风险：DI 快照与守卫测试（`ModePromptToolReferenceTests` / `HarnessInstructionsWiringTests`）对工具注册面敏感，摘除 `TeamAgentToolSources` 后按失败信息逐项更新断言，不得删除断言本身。
- 阶段顺序风险：若阶段 C 先于阶段 A 执行，会造成能力回退（code-review 三视角丢失）。**阶段顺序不可颠倒。**
