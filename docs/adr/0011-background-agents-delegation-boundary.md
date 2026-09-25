# 子代理派工边界：不迁移 MAF BackgroundAgents

**状态**: Accepted
**日期**: 2026-09-21
**关联**: [MAF 集成边界与禁止清单](./0007-maf-integration-boundaries.md)（§4 BackgroundAgents）、
[MAF 后台响应评估](./0009-background-responses-assessment.md)（姊妹能力，同为「后台」但不同层）、
[代理循环能力边界](./0010-agent-loop-boundaries.md)（LoopAgent 能力边界）、
[MAF C# 接入指引](../maf/integration-guide.md) §10/§12/§13.2、[子代理派工体系](../sub-agents.md)

## 语境

### 1. 讨论缘起

2026-09-21，围绕「MAF BackgroundAgents 与 OneCode 子代理派工体系的关系」发生三轮同源讨论：
① 是否整体迁移到 BackgroundAgents 并删除自定义实现；② MAF 提供该能力的定位是什么、对本项目有没有用；
③ `Agent` / `ParallelAgents` 的底层实现能否切换为 BackgroundAgents。结论需要决策记录固化，
防止第四次从零讨论——本仓库已有同型教训：边界只存在于计划文档中时，判断会反复漂移
（见 [MAF 集成边界](./0007-maf-integration-boundaries.md) 的四次误判表）。

### 2. 功能定位（MAF 委派栈的中间层）

MAF 提供三层「子代理委派」入口，BackgroundAgents 占中间层：

| 层 | 形态 | 拓扑 | 生命周期归属 | 适用 |
|---|---|---|---|---|
| 1 | `AIAgent.AsAIFunction()` | 单次同步调用 | 调用方代码 | 子任务短、可阻塞父回合 |
| 2 | **BackgroundAgents** | 运行时由**父模型**决定（扁平任务） | **框架**（起/等/取/续/清/释放） | 要并发、不想写轮询样板、拓扑事先不可知 |
| 3 | Workflows（含 Declarative） | 编译期确定 | 框架（可 checkpoint / durable） | 要确定性、可恢复、复杂编排 |

**它是脚手架，不是编排内核**：目标用户是「想让对话代理本人当编排者、又不愿手写任务追踪」的应用开发者。
与「后台响应」（[后台响应评估](./0009-background-responses-assessment.md)）的区别：后者把**同一次请求**
交给模型服务端异步执行并轮询延续令牌，不涉及第二个 agent；BackgroundAgents 则是把工作派给**子代理**。

### 3. 机制与契约（源码级）

位置：`agent-framework/dotnet/src/Microsoft.Agents.AI/Harness/BackgroundAgents/`
（在 `Microsoft.Agents.AI` 包的 `Harness/**` 子目录，不在 `.Harness` 包内）。

- **挂载**：`ChatClientAgentOptions.AIContextProviders` 或 `HarnessAgentOptions.BackgroundAgents`；
  子代理 `Name` 必须非空且大小写不敏感唯一，否则构造抛 `ArgumentException`。
- **六个模型侧工具**：`background_agents_start_task`（起任务 + 建专用子会话，返回整数 ID）、
  `background_agents_wait_for_first_completion`（等首个终止；`WaitTimeout` 默认 5 分钟、上限
  `uint.MaxValue-1` ms，到期即返回不杀任务）、`background_agents_get_task_results`、
  `background_agents_get_all_tasks`、`background_agents_continue_task`（终态后在原子会话续跑）、
  `background_agents_clear_completed_task`（删终态元数据 + 释放子会话）。
- **状态机**：`running` → `completed` / `failed` / `lost`（`lost` = 进程重启或会话还原后，在途工作与
  子会话句柄无法跨边界保留）。
- **双状态**：`BackgroundAgentState`（可序列化元数据，存父会话 `StateBag`）与
  `BackgroundAgentRuntimeState`（在途 `Task` / 子会话 / 每任务 `CancellationTokenSource`，全 `[JsonIgnore]`），
  所有变更在 `SyncRoot` 下与宿主 release 竞态安全。
- **ExecutionContext 隔离**：`start_task` 用 `Task.Run(...)` 派生子 ExecutionContext——
  `AIAgent.RunAsync` 同步设置静态 `AsyncLocal CurrentRunContext`，不隔离会破坏父 agent
  同一 FICC 批次的后续工具调用。
- **宿主侧 API**：`ReleaseSessionAsync(session, cancelRunning=true, timeout=30s, ct)`（取消并等待在途任务、
  仍在跑的标记 `failed`、幂等、并发安全）与 `GetIncompleteTasks(session)`。
- **自动等待**：`LoopAgent + BackgroundTaskCompletionLoopEvaluator`（LoopAgent 的能力边界与项目用法见
  [代理循环边界](./0010-agent-loop-boundaries.md)）。
- **设计取舍**：只认构造时预注册的命名 `AIAgent` 字典，不组合宿主侧任何闸；**审批不转发**；
  在途工作不跨重启；**无取消工具**（只有 clear 终态与整会话 release）；相关 API 全带
  `[Experimental]`（诊断 ID `MAAI001`）。该能力在 .NET / Python 两侧先后落地，.NET 侧发布更早：
  .NET 于 `dotnet-1.4.0` 引入 SubAgents、`dotnet-1.6.2` 改名 BackgroundAgents；Python 侧至 `python-1.7.0` 才提供。

### 4. 版本证据（OneCode pinned 包 1.22.0）

按 [MAF 集成边界](./0007-maf-integration-boundaries.md)「不得用本地 checkout 为已发布包行为作证」的要求，
做双层取证：

1. **DLL 字符串验证**（`D:\NuGet\Packages\microsoft.agents.ai\1.22.0\lib\net10.0\Microsoft.Agents.AI.dll`）：
   `BackgroundAgentsProvider` / `BackgroundAgentRuntimeState` / `GetIncompleteTasks` /
   `ReleaseSessionAsync` / `CreateTools` / `StartTrackedRun` / `BackgroundTaskCompletionLoopEvaluator` /
   `BackgroundAgentsProviderOptions` 均在；**`StartTaskAsync` 不存在**（无编程式起任务入口）。
2. **tag 源码比对**：`dotnet-1.22.0` tag 的 `BackgroundAgentsProvider.cs` 与本地 HEAD 在该目录
   `Harness/BackgroundAgents/**` 无差异，六个工具名与四成员公共面一致。

> Microsoft Learn 后台代理文档的 .NET zone 写「主机端后台智能体会话版本目前在 .NET 中不可用」，与 pinned 1.22.0 不符
> （`ReleaseSessionAsync` / `GetIncompleteTasks` 均已存在）。以本地源码与 pinned 包为准。

### 5. OneCode 现状（并非「四模式都是单主代理」）

| 模式 | 编排形态 | 派工能力 |
|---|---|---|
| Build | 单主代理（`MainAgentRunner` → `AgentPipelineBuilder.BuildHarnessAgent` → `HarnessAgent`）；受控构建走 `ControlledBuildAttemptWorkflow`（MAF Workflow 幂等单元） | `Agent` 工具（→ `IAgentRunner` → `WorkerAgentService` → `ForkedAgentRunner`，profile Worker/Explore/Plan）；`ParallelAgents` 工具（`AgentTaskWorkflowCompiler` 编译 DAG，MAF InProc fan-out/fan-in） |
| Plan | 单主代理 + Plan 白名单（`ToolCapabilityResolver`，`AllowSubAgents=true`） | 同上；`plan.prompt` 已有 Explore/Plan 子代理指引 |
| Team | **多代理**：MAF Workflows（GroupChat / Magentic / ParallelDag） | 成员经 `TeamAgentFactory` 以 TeamMember profile 构建；审批经 `TeamWorkflowRunner` 桥接 |
| Goal | **checkpointed 工作流**（plan→step→router→completion），每步一次主代理 run + `RequiredTools` 白名单裁剪（fail-closed） | 子目标级裁剪；进度经 `GoalContextProvider` 注入 |

派工体系已有的产品闸（均无 MAF 对应物）：`PipelineProfile` / `PipelineProfileBehavior`
（Worker/Explore/Plan/TeamMember 能力矩阵与只读白名单）、`ToolCapabilitySet`
（不可变工具可见边界，含 `AllowSubAgents` 闸）、`TaskService`（持久化任务清单 + per-task 取消 +
TUI 呈现）、`ForkedAgentRunner` 的 fail-closed 审批检测。

## 决策

### 1. 不挂载：任何路径均不启用 BackgroundAgentsProvider

不设置 `HarnessAgentOptions.BackgroundAgents`，也不经 `AIContextProviders` 挂载
`BackgroundAgentsProvider`——Full 主路径、Worker、Team、Goal 与受控 Build 全部适用。
本条重申并补强 [MAF 集成边界](./0007-maf-integration-boundaries.md)，四条硬证据：

**证据 1 — 工具门禁被绕过。** 产品工具可见性的唯一执行点是 `ToolCatalog` + `ToolCapabilitySet`：
`ToolAssembler.AssembleTools` 按 `AllowedToolNames` 过滤目录，`SessionToolSet` 按能力边界激活，
`ToolCapabilitySet.IsAllowed` 以 `AllowSubAgents` 整体禁用 `Agent`/`ParallelAgents`。
而 `BackgroundAgentsProvider.ProvideAIContextAsync` 直接往 `AIContext.Tools` 里造工具，
**不经目录、不经能力集**。精确现状：

- 普通交互主路径的 `MainAgentRunOptions.IsToolAllowed` 未设置；显式设置它的有两处——受控 Build 路径
  （`ControlledBuildAttemptRuntime`）与子代理/团队成员 profile 管道（`SubAgentPipelineFactory.BuildRoleOverrides`，
  按 `AllowedTools` 白名单）；另有一条**隐式**路径：`AgentPipelineOptionsFactory` 在 profile 的
  `ReadOnlyToolWhitelist` 非空（仅 Explore/Plan 等只读 profile，`PipelineProfile.ReadOnlyToolWhitelist`
  绑定 `AgentCapability.ReadOnlyTools`）且未显式传入时，按白名单自动构造。
  主路径 → 无运行时名字闸，provider 工具将完全不受模式边界约束；
- 受控 Build 路径为 `approvedSet ∪ ReadOnlyTools` → 执行时被拒，但 schema 仍可见，
  模型幻觉调用产生噪声轮次。（子代理/团队成员管道的白名单与只读 profile 的隐式白名单同理：
  都只管执行期名字闸，不改变 provider 工具直注 `AIContext.Tools`、不经产品目录这一根本问题。）

**证据 2 — 审批不转发导致写代理静默失效。** `ToolApprovalAgent` 源码
（`Harness/ToolApproval/ToolApprovalAgent.cs` 行 183-188；pinned 包与本地 checkout 同处 `dotnet-1.22.0` 基线，
行号一律以此为准——该文件在 `1.21.0` 下同一逻辑位于 176-185 行）：审批请求未自动批准时，把带 pending
`ToolApprovalRequestContent` 的响应**返回调用方、工具不执行**。OneCode Worker profile 保留
`ToolApproval` 且继承父权限模式（`SubAgentPipelineFactory.BuildSecurityContext`），Default 模式下
写工具（`ToolRisk.Safe → Conditional`）触发 Ask。BackgroundAgents 的 `start_task` 只取子代理最终文本：
**编辑静默不发生、任务仍标记 completed**，父代理收到一段「我需要批准……」的文本。现有
`ForkedAgentRunner` 显式检测挂起审批并 fail-closed 报结构化错误。仅只读 Explore/Plan profile
与该约束自洽（`ToolPolicyDefaults.ForRisk`：`ReadOnly → ToolApprovalMode.Never`，只读工具永不进审批协议）。

**证据 3 — DAG / fan-in / 上游注入无对应物。** `background_agents_*` 是扁平任务列表：
无依赖边、无 fan-in barrier、无「上游失败→本任务 blocked」、无 `injectUpstreamResults`、
无 `ExecutionAccess` 写串行、无拓扑校验（环/重名/未知依赖）与 definition hash。
删除 `ParallelAgentsTool` 即删掉这些，或把确定性拓扑推给父 LLM 用 wait 循环手拼。

**证据 4 — 生命周期与恢复语义倒置。** `ITaskService` 生产实现**持久化**任务状态
（conversation / BuildRun 作用域跨重启存活），`GetTaskToken` 提供 per-task 停止；
BackgroundAgents 在途工作重启即 `lost`（设计如此）且无取消工具，清理只有整会话 `ReleaseSessionAsync`。
「ESC/新输入不取消后台任务」这一产品明确设计也没有对应物。

### 2. 不用作 `Agent` / `ParallelAgents` 的底层实现（接口级结论）

`BackgroundAgentsProvider` 的完整公共面只有四个成员：构造函数、`StateKeys`、
`GetIncompleteTasks(AgentSession?)`、`ReleaseSessionAsync(...)`（另有一个 protected
`ProvideAIContextAsync`）；tag 源码与 DLL 取证一致，且**不存在** `StartTaskAsync` 之类的
编程式入口。`background_agents_start_task` 仅在 **private** `CreateTools` 中创建——
**任务启动的唯一通路是「挂载它的 agent 的模型调工具」。**

`AgentTool` / `ParallelAgentsTool` 是确定性代码路径：模型调用产品工具后由产品代码执行，
中间不存在「再让模型调一次 `background_agents_*`」的位置——除非把产品工具退化为壳，即 §决策 1 的双入口。
`ParallelAgents` 更不可能：DAG 语义在 provider 没有任何对应物，而它连编程式 start 都没有。

三条歪路明令禁止：

| 歪路 | 为何更差 |
|---|---|
| 产品工具变壳 + 主 agent 挂 provider | 双入口（§决策 1 四证据全数适用）；派工决定权交还模型 |
| 反射 private `CreateTools` / `StartTrackedRun` | 依赖 sealed 实验类私有实现，升级即碎；违反「不留兼容层」规范 |
| 自建等价 provider「复刻」生命周期 | 代码一行没少（还要补产品闸），却丢掉 MAF 已处理的 `SyncRoot` 竞态、ExecutionContext 隔离、`lost` 标记——纯负优化 |

**正确框架映射**：OneCode 的并行派工**已经在用 MAF 原生层**——Workflows
（`ParallelAgents` 的 InProc fan-out/fan-in、Team 的 GroupChat/Magentic、Goal 的 checkpointed 工作流）。
「切换底层」在 MAF 当前 API 下的答案是：provider 不是产品工具的可替换后端（没有可调用后端），
Workflows 才是，而且已经切过了。

### 3. 保留并完善产品确定性派工体系

`Agent` / `ParallelAgents` + `PipelineProfile` + `ToolCapabilitySet` + `TaskService` 保留。
**「删除自定义」的净核算约等于零**：若整体迁移，可直接删除的是 `AgentTool`(144 行)、
`ParallelAgentsTool`(101)、`AgentTaskWorkflow`(601)、`ForkedAgentRunner`(286)、
`WorkerAgentService`(101) 共约 1234 行；但 `PipelineProfile` / `SubAgentPipelineFactory` /
`TaskService` / `ToolCapabilitySet` **不可删**（闸与生命周期必须活在某个地方），
而 provider 是 sealed 且工具直注，唯一加固点是 `PermissionAndLimitMiddleware` 的
`IsToolAllowed` 名字闸——等于把删掉的闸再建一遍，同时丢掉确定性与 DAG。

### 4. 借鉴 provider 的四个生命周期模式（有证据的净收益）

| provider 模式 | OneCode 落点（见「后续工作」） |
|---|---|
| `wait_for_first_completion`：有界等待、超时即返回不杀任务 | F4 有界等待原语（默认 ≤60s 可配置） |
| `continue_task`：终态任务保留并复用子会话 | F5 continue（仅「同一后台任务的迭代修正」；默认仍 fresh-spawn） |
| `ReleaseSessionAsync`：取消 + 等待 + 幂等 + 仍在跑标 failed | F6 会话释放语义 |
| `start_task` 的 `Task.Run` ExecutionContext 隔离 + 元数据序列化 / `lost` 标记 | F6 后台执行隔离与重启态（`AgentTool` background 已用 `Task.Run`，需补重启态） |

### 5. 重评触发条件

出现以下任一条时**先立项评估**（门禁 / 审批 / DAG 三关），不得以代码注释绕过本 ADR 直接挂载：

1. 上游暴露编程式任务 API（如 `provider.StartTaskAsync(...)`）或 .NET harness 侧释放配套补全
   → 重评 F4/F5/F6 能否改用框架能力；
2. 出现**刻意不带产品闸**的轻量代理面。注意：`docs/plan/web-host-plan.md` 的 Web Host 明确复用
   `QueryStreamEngine` / 权限 / Build 门禁，**不构成触发**；
3. 产品要求「一次委派周期内按发现增量加任务」的自适应 fan-out（编译期 DAG 表达不了）
   → 与「扩展 `ParallelAgents` 增加动态相」二选一比较后立项。

## 后续工作

缺口修复清单（均不在本 ADR 实施范围，各自独立立项；门禁：`dotnet build src/OneCode.slnx`
无新增警告 + `dotnet test src/OneCode.slnx` 全绿）：

| ID | 事项 | 一句话验收 | 状态 |
|---|---|---|---|
| F1 | `build.prompt` 幽灵工具名 `Task`→`Agent`、委派指引写实、子代理结果注入防护 | 提示词点名的工具均在 `AddToolServices` 注册表；派工语义行不出现宿主后台任务工具名（`ModePromptToolReferenceTests` 守卫，双重反证） | **已完成**（2026-09-21） |
| F2 | `ForkedAgentRunner` 接 `OrchestrationEventSink` / `FileChangeCallback` | 子代理工具调用带 agent 标签实时可见（行为测试） | 待立项 |
| F3 | `Agent` 工具可发现性（本地模型路径） | 调研类 prompt 首轮激活 `Agent`；激活顺序单调追加 | 待立项 |
| F4 | 有界等待原语（借鉴 `wait_for_first_completion`） | 超时返回运行中清单 / 终态返回结果 / 取消传播 | 待立项 |
| F5 | continue 语义（借鉴 `continue_task`） | 子会话上下文保留；清理后句柄释放；默认仍 fresh | 待立项 |
| F6 | 并发上限 + 会话释放（借鉴 `ReleaseSessionAsync`） | 上限生效；关闭时取消并等待；重启后终态标记 + 输出留存 | 待立项 |
| F7 | 子代理结果注入防护覆盖 Build/Plan 主代理 | 注入指令不触发派工/批准（prompt 断言 + fail-closed 回归） | **已完成**（随 F1 落地；`plan.prompt` 原已将 "agent research reports" 列为不可信） |

## 后果

- **正面**：派工决定权与产品闸留在确定性侧（可测、可审计、可恢复）；子代理生命周期获得 MAF 已验证的
  四条语义作为实现参照；实验性 API（`MAAI001`）不进入依赖面，升级面不扩大；三轮同类讨论就此终结。
- **负面**：等待 / 继续 / 释放三个原语需自建并长期维护（F4-F6）；
  `AgentTaskWorkflowCompiler` / `GoalWorkflowCompiler` / `ControlledBuildAttemptWorkflowCompiler`
  三套 WorkflowBuilder 装配的模式重复仍未收敛（另案，与本 ADR 无关）。
- **中性**：并行派工的框架原生层（Workflows）不变；若上游按 §决策 5 提供编程式入口，
  F4-F6 的取舍需重评，届时修订或新立 ADR。

## 引用

- MAF 源码（`agent-framework/`，版本关系见 [integration-guide §1.1](../maf/integration-guide.md)；
  本地 checkout `dotnet-1.22.0-6-g669c8b95e` 与 pinned 包同处 `dotnet-1.22.0` 基线，下述路径中
  `BackgroundAgents/`、`Loop/` 在该基线内无差异，`ToolApproval/` 已被 `#8432` 重写、`AgentMode/` 被 `#8458` 扩展，
  行号引用一律以 `dotnet-1.22.0` 为准）：
  `dotnet/src/Microsoft.Agents.AI/Harness/BackgroundAgents/`（6 文件）、
  `Harness/Loop/BackgroundTaskCompletionLoopEvaluator.cs`、
  `Harness/ToolApproval/ToolApprovalAgent.cs`、`Microsoft.Agents.AI.Harness/HarnessAgentOptions.cs`。
- pinned 包 DLL：`D:\NuGet\Packages\microsoft.agents.ai\1.22.0\lib\net10.0\Microsoft.Agents.AI.dll`
  （§4 版本证据）。
- 产品源码：`src/OneCode.App/Tools/AgentTool.cs`、`Tools/ParallelAgentsTool.cs`、
  `Services/Agent/ForkedAgentRunner.cs`、`Services/Agent/AgentTaskWorkflow.cs`、
  `Services/Coordinator/WorkerAgentService.cs`、`Services/Agent/SubAgentPipelineFactory.cs`、
  `src/OneCode.Infrastructure/Agent/PipelineProfile.cs`、`src/OneCode.App/Query/ToolCapabilityResolver.cs`、
  `src/OneCode.Infrastructure/Middleware/PermissionAndLimitMiddleware.cs`、
  `src/OneCode.Tests/ModePromptToolReferenceTests.cs`（F1 守卫）。
- 关联决策：[MAF 集成边界](./0007-maf-integration-boundaries.md)（本 ADR 补强其论据，禁令不变）、
  [权限与工具审批边界](./0001-permission-vs-toolapproval-vs-filter.md)（Permission/Filter 是唯一 Allow/Deny 门）、
  [声明式工作流评估](./0002-declarative-workflow-assessment.md)（禁止 Declarative）、
  [后台响应评估](./0009-background-responses-assessment.md)（后台响应，姊妹能力）、
  [代理循环边界](./0010-agent-loop-boundaries.md)（LoopAgent 边界）。
- 机制总览：[子代理派工体系](../sub-agents.md)（与本 ADR 同步新增）。
