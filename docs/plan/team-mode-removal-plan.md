# Team 模式砍除与协作能力收敛计划

> 状态：**已评审通过，待执行（未动代码）**。本文档是唯一执行依据，执行时按 §3 顺序推进。
> 范围：①删 Team 工作模式与 TeamRun 控制面；②多视角评审降级为 `/review` 并行只读子代理；
> ③新增子代理自定义（`.onecode/agents/*.md`）。BUILD / PLAN / GOAL 模式不受影响。

---

## 1. 决策依据（评估结论）

多角色协作分三种形态，价值不同：

| 形态 | 内容 | OneCode 现状 |
|---|---|---|
| A 独立视角并行 | 多视角 review、多方案对比；价值来自上下文隔离 + 视角独立 | 需保留（Team 的 code-review 是唯一贴合场景） |
| B 主 agent 动态派工 | 按需拆子任务给子代理，保住主线上下文预算 | **已具备**：`Agent` / `ParallelAgents` 工具 |
| C 模拟人类团队分工 | 固定成员角色 + GroupChat/Magentic 编排 + 任务状态机 | Team 模式押注的形态，实践收益最低 |

砍除 Team 模式的四条理由：

1. **与现有能力高度重叠**：澄清（PLAN 对话）、计划审批（PLAN SubmitPlan 门）、执行（BUILD +
   Agent/ParallelAgents 派工）、质量门（hook 门禁）在主管线均有对应物，Team 把它们硬编码成状态机，
   多花约 6000 行实现。
2. **三拓扑两假一窄**：ParallelDag 名为扇出实为单成员串行薄封装（`TeamWorkflowRunner.cs` W3-B 注释
   自认 never Concurrent true-parallel）；GroupChat 成本最高且靠注入协作协议 + 共识提前终止启发式
   打补丁（已知空转病理）；Magentic ≈ 主 agent 用 Agent 工具派工，仅委派决策权换了持有者。
   `ParallelAgents` 工具反而是真 DAG 并行——Team 最像"多角色协作"的能力，工具层已用更简单方式做到。
3. **重机制的并发前提不成立**：ParallelDag 刻意不并行，GroupChat/Magentic 轮转发言（同一时刻一个
   agent）——运行时实际串行。EditTransaction / Operation Ledger / fencing token / 工作区指纹这套
   防多成员并发写冲突的防御纵深，为假想的并发形态而建。
4. **自定义体验粗糙且方向错位**：用户"自定义团队"的真实诉求是"固定几个 specialist 不想每次重新
   描述"，正确形态是按代理定义文件（Claude Code 验证过的轻量做法），而非 team.yaml 编排拓扑。
   现有 team.yaml 无 validate、未知字段静默忽略（`IgnoreUnmatchedProperties`）、成员不能配模型 /
   profile、无项目级、不能点名成员。

Team 模式真正独有、替代不了的能力——TeamRun 聚合的跨进程可恢复任务图 + 强制审批门 + 事务回滚 +
交付报告——仅在"长时、可中断、无人值守大型任务"场景有真实价值，非当前产品主线（TUI 极致化单路线，
见 ADR 0013）。该决策不代表"多代理编排永远不做"；若未来押注无人值守长任务，再按收敛形态重建。

## 2. 甄别结论：共享底座保留清单（防误伤）

以下文件虽被 Team 引用或物理位于 Coordinator 相关目录，但经使用方核查**全部保留**：

| 文件 | 共用方 |
|---|---|
| `Core/Workflows/`（WorkflowRunStore / WorkflowRunModels / WorkflowTopology / WorkflowDefinitionHash） | `IBuildRunStore`、`IGoalRunStore` 都建在它上面 |
| `App/Services/Runtime/`（ModeWorkflowHost / RequestPortGate / WorkflowQualityGateRunner） | GoalMode `GoalWorkflowHost`、BuildMode `ControlledBuildAttemptWorkflow` |
| `App/Services/Streaming/OrchestrationEventBus.cs` | TodoProjectionService、PlanCardPublisher、TuiHostConfigurator |
| `App/Services/ClarificationInteractionService.cs` | BuildMode `BuildRunGate` |
| `Core/Coordinator/OrchestrationEvent.cs` | 同上（事件总线载体，只删 Team 专属 record） |
| `Core/Config/ModeBudgetSettings.cs` | MaxTurns 为 Build/Team 共用，仅改注释 |
| `App/Services/Coordinator/WorkerAgentService.cs` | Agent 工具的 IAgentRunner（被 `Core/Tasks/TaskService` 引用），**需迁出目录** |

## 3. 执行清单

### 3.0 前置

1. 权限模式退出 plan mode（写操作当前被权限层拦截）。
2. checkpoint 提交全部在制改动（沿用 "Save uncommitted changes" 惯例，工作树 272 文件），
   使本计划各提交干净可回滚。
3. 全程直接在 main 提交（项目惯例），删除类改动分主题提交，不与功能新增混流。

### 3.1 整文件删除（Team 专属）

**App/Services/Coordinator/（28/29 文件，仅留 WorkerAgentService 迁出）**
TeamAgentFactory、TeamAgentPipelineDependencies、TeamAgentToolSources、TeamApprovalWorkflow、
TeamApprovalWorkflowHost、TeamCapabilityProfile、TeamClarificationResponses、TeamClarificationWorkflow、
TeamConfigLoader、TeamCoordinatorModels、TeamFileChangeObserver、TeamOrchestrationService(.Gates)、
TeamRegistry、TeamRequirementService、TeamResultAggregator、TeamRunApplicationService、TeamRunErrors、
TeamRunFinalizer、TeamRunGuards、TeamRunStateMachine、TeamServiceCollectionExtensions、TeamTaskProgress、
TeamTaskWorkflow、TeamTaskWorkflowHost、TeamTaskWorkflowRuntime、TeamWorkflowRunner、TeamWorkflowTopology

**其他实现**
- `Core/Coordinator/`：ITeamOrchestrationService.cs、TeamRun.cs（OrchestrationEvent.cs 保留）
- `Infrastructure/Teams/JsonTeamRunStore.cs`
- `App/Commands/TeamCommand.cs` + `AddCommands()` 注册项
- `App/prompts/teams/` 三个内置 YAML（code-review / research / impl）
- `App/prompts/system/team.prompt`
- TUI：TeamSidebarView.cs、ChatBlockRenderers.TeamSidebar.cs、ReplShell.TeamSidebar.cs、TeamRunSnapshot.cs

**测试**：MafTeamAndWorkflowTests、ParallelDagTeamTests、TeamTaskWorkflowTests、TeamTaskWorkflowRuntimeTests、
TeamRunTests、TeamRunErrorsTests、TeamRunRecoveryTests、TeamApprovalWorkflowTests、
TeamApprovalWorkflowResumeTests、TeamClarificationWorkflowResumeTests、TeamToolApprovalBridgeTests、
TeamFileChangeObserverTests、TeamSidebarTests、MagenticReproTests（15 文件约 3300 行）

### 3.2 外科手术编辑（保留文件、摘除 Team 分支）

| 文件 | 动作 |
|---|---|
| `App/Modes/WorkingMode.cs` | 枚举删 `Team`；循环切序更新 |
| `App/Services/WorkingModeBridge.cs` | 删 `PermissionMode.Team` 档位映射与 Team 分支 |
| `Core/Permissions/PermissionMode.cs`、`PermissionProfiles.cs`、`PermissionCheckHelpers.cs` | 删 Team 档位与分支 |
| `Infrastructure/Agent/PipelineProfile.cs` | 删 `TeamMember` 档（PipelineProfileBehavior 同步） |
| `App/Services/Streaming/QueryStreamService.cs` | 删 TEAM 分发块（其错误文案引用不存在的 `/team register`，幽灵引用随之消灭） |
| `App/Services/Streaming/OrchestrationStreamService.cs` | 删 `StreamTeamAsync` 与 Team 事件投影；`StreamGoalAsync` 保留 |
| `Core/Coordinator/OrchestrationEvent.cs` | 删 4 个 Team 专属 record：TeamTaskProgress、TeamClarificationRequest、TeamPlanApprovalRequest、TeamUserResponse |
| `Core/Keybindings/KeybindingDefaults.cs` | 删 Alt+3 直达 TEAM、Shift+Tab `chat:cycleTeam` |
| `Core/Config/ModeBudgetSettings.cs` | 注释去除 Team 语义（字段保留，Build 仍用） |
| TUI 长尾 | ChatInputView(.Keys)、ChatTranscriptView.Rendering、OneCodeToplevel(.Events/.Query/.cs)、AgentStatusBar、SessionContextBar、SidebarViewBase、TranscriptEventPresenter、TuiContext、TuiEvent、TuiEventMapper、TuiPalette、PermissionPromptTypes |
| Agent 管线长尾 | AgentContextPipeline、AgentWorkflowEventProcessor、ApprovalBroker、CompactionStrategyFactory、GoalAgentModels、MainAgentContracts、MainAgentRunner、MainModeContextProviderBuilder、SubAgentPipelineFactory、PromptComposer |
| 其余长尾 | ClaudeCodeApp、AppStartupService、QueryStreamEngine、TuiContextFactory、BuildResumePolicy、CheckpointCommand、ResumeCommand、PermissionsCommand、Core：AgentCapability/ICommand/FileChange/ApprovalDecision/ApprovalRequest；Infrastructure：AgentPipelineBuilder、AgentPipelineOptionsFactory、OneCodeHarnessDefaults、PromptTooLongRecoveryRunMiddleware、PermissionAndLimitMiddleware、DurableFile、FencedLedgerTransaction |
| 测试长尾 | WorkingModeTests、ModeDirectKeybindingTests、ModePromptToolReferenceTests、ServiceCollectionSnapshotTests（DI 快照重生成）、TestAgents、AgentContextPipelineTests、AgentPipelineBuilderTests、ChatBlockRenderersTests、ChatTranscriptViewTests、SessionContextBarTests、TranscriptEventPresenterTests、TranscriptWidthAdaptiveTests、CheckpointResumeWorkflowTests、MafM4ApprovalEventTests、MainAgentRunnerTests、ResumeCommandTests、PermissionCheckerBoundaryTests、HarnessInstructionsWiringTests、FileMemoryProfilePolicyTests、WorkflowKernelVocabularyTests |

### 3.3 WorkerAgentService 迁移与 DI 修复

- `WorkerAgentService.cs` 从 `App/Services/Coordinator/` 迁至 `App/Services/Agent/`（归属 Agent 派工域）。
- `TeamServiceCollectionExtensions.AddTeamServices` 整体删除；WorkerAgentService 注册迁入
  Agent 侧注册扩展（ToolServiceCollectionExtensions 或 AddToolServices 既有链）。
- ClaudeCodeApp / TuiHostConfigurator / ChatSessionDependencies 等组合根同步摘除 Team 依赖注入。
- `ServiceCollectionSnapshotTests` DI 快照基线重新生成（历史基线不允许手改，走重生成流程）。

### 3.4 build + test 迭代至全绿

- `dotnet build src/OneCode.slnx` 无新增警告；`dotnet test src/OneCode.slnx` 全绿
  （FileIgnoreTests 本机必失败为已知存量，不算新增）。

### 3.5 `/review` 并行多视角（形态 A 保留）

- 查现有 `/review` 命令（QueryStreamService 注释提及 command-produced prompt /review）：在原命令内
  实现并行 3 视角（security / performance / maintainability）**只读**子代理 + 结果聚合去重；不存在则新增。
- 复用 `Agent`/`ParallelAgents` 派工设施与 `ReadOnlyAgentTools` 只读白名单；三视角 prompt
  文件化放 `prompts/review/`（进三层覆盖体系），不新建任何编排状态机、不建持久化聚合。
- 新增守卫测试：视角派发数量与只读工具边界、聚合去重。

### 3.6 子代理自定义（"自定义团队"的正解）

- **定义文件**：`{cwd}/.onecode/agents/*.md`（项目级）与 `~/.onecode/agents/*.md`（用户级），
  frontmatter 字段 `name` / `description` / `allowed_tools`，正文 = instructions（system prompt）。
  项目级覆盖同名用户级；与内置三形态（general-purpose / Explore / Plan）重名时内置优先。
- **加载**：启动扫描注册 + 变更后惰性重扫；解析错误**显式报错**（文件名 + 行号），
  吸取 team.yaml `IgnoreUnmatchedProperties` 静默忽略的教训。
- **派工**：`Agent` / `ParallelAgents` 工具的 `agent` 参数扩展接受自定义名；profile 默认映射 Worker 档，
  `allowed_tools` 与父级能力取交集（复用既有 `ToolCapabilitySet.IsAllowed` 边界传播）。
- **可发现性**：自定义代理清单注入 Agent 工具 description（本地小上下文模型经既有 ToolSearch 链路可见）。
- **第一版不做**：按代理模型配置（当前全项目主模型）、按代理 profile 指定（固定映射）。
- 新增守卫测试：加载/覆盖/重名/非法 frontmatter 报错、派工走 Worker 档、能力交集。

### 3.7 文档全文清扫

- 删除 `docs/team-modes.md`。
- 改写（不留勘误痕迹）：`docs/adr/0007-maf-integration-boundaries.md`、`docs/adr/0011-background-agents-delegation-boundary.md`
  中 Team 相关段落改为现状描述；`docs/maf/integration-guide.md` 装配对照去 Team 化。
- 同步：README_CN.md、AGENTS.md（根 + src/ + App）、docs/commands.md（删 /team，补 /review 语义）、
  docs/sub-agents.md（§5 Team 部分替换为子代理自定义机制）、docs/settings.md（删 PermissionMode.Team 档）、
  docs/keybindings.md（删 Alt+3 / cycleTeam）、docs/compact-thresholds.md、docs/hooks.md、
  docs/memory-overview.md、docs/testing/manual-checklist.md（删 Team 手测项，补 /review 与 agents 定义）。
- 清扫命令（须零命中）：
  `rg -i "TeamOrchestration|TeamRun|WorkingMode\.Team|PermissionMode\.Team|PipelineProfile\.TeamMember|/team\b|TEAM 模式|team\.yaml|cycleTeam|TeamMember|TeamSidebar" src/ docs/ README_CN.md AGENTS.md`
  （排除 bin/obj 与本计划文档自身。）

## 4. 验收标准

1. build 无新增警告；test 全绿（FileIgnoreTests 存量失败除外）。
2. §3.7 清扫 rg 零命中幽灵引用。
3. 手测：Tab/Shift+Tab 模式循环不再出现 TEAM；`/review` 三视角并行输出且聚合；自定义
   `.onecode/agents/*.md` 后 Agent 工具可按名派工且工具边界正确；BUILD/PLAN/GOAL 回归正常。

## 5. 风险与回滚

- 所有删除在 checkpoint 提交之后进行，`git revert` / `git reset --hard <checkpoint>` 即可整体回滚。
- 最大风险点是共享底座误伤——以 §2 保留清单为准绳；任何文件删除前先 rg 其类型名确认无 Team 外使用方。
- 次风险：DI 快照与守卫测试（ModePromptToolReferenceTests / HarnessInstructionsWiringTests）对
  工具注册面敏感，摘除 Team 工具源（TeamAgentToolSources）后按失败信息逐项更新断言，不得删除断言本身。
