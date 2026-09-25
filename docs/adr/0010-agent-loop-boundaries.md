# 代理循环能力边界：GOAL 子目标循环与 /loop 运行时有界循环

**状态**: Accepted
**日期**: 2026-09-21
**关联**: [MAF 集成边界与禁止清单](./0007-maf-integration-boundaries.md)、
[结构化请求 + 文本降级](./0008-structured-output-text-fallback.md)、
[MAF C# 接入指引 §10/§12](../maf/integration-guide.md)、
[/loop 命令参考](../commands.md#loop)、[配置键 loop.maxIterations](../settings.md)

## 语境

Microsoft.Agents.AI 提供 `LoopAgent`（`DelegatingAIAgent`）：每次迭代是一次完整 inner agent run，
由一组有序 `LoopEvaluator` 决定是否重跑；`MaxIterations` 是全局硬上限，评估器无法突破。

2026-09-21 核对本地源码（`agent-framework/dotnet/src/Microsoft.Agents.AI/Harness/Loop/**`）与
官方样例（`agent-framework/dotnet/samples/02-agents/Harness/Harness_Step05_Loop/Program.cs`）后，发现产品侧存在
**同一用户意图的两套循环实现**，且二者性质完全不同：

| 实现 | 形态 | 上限 | 判据 |
|---|---|---|---|
| GOAL 模式子目标循环 | MAF `LoopAgent` + `DelegateLoopEvaluator`（硬门禁 → AI judge） | `GoalLoopDefaults.MaxAttemptsPerSubGoal = 3` | 确定性门禁 + LLM judge |
| `/loop` "技能" | 一段**提示词模板**（`BundledSkills.CreateLoopSkill`） | **无运行时强制**（由模型自行解释"最大迭代次数"） | **模型自评**——让产生输出的同一个模型判定是否匹配期望结果 |

第二套的三个缺陷都不是风格问题：上限不可强制（兜底只有 agent 的 `maxTurns` 工具调用计数，与循环次数不是一回事）、
判据是自证、无预算/进度/证据留存。同时它与 GOAL 子目标循环职责重叠，违反
[src/AGENTS.md 重构总则](../../src/AGENTS.md)（简洁优先 / 不保留死代码）所反对的双轨。

另一个待闭环项来自 [结构化输出文本降级](./0008-structured-output-text-fallback.md)：`GoalSubGoalJudge`
（VERDICT marker 手搓 judge，历史上"Kept custom (not AIJudgeLoopEvaluator)"的决策未复核）。
核对源码后确认该决策**成立**，但其判定规则与框架相反——见决策 4。

## 决策

### 1. GOAL 模式子目标循环：保留现状，不重构

继续用 MAF `LoopAgent` + `DelegateLoopEvaluator`，`MaxIterations` 与
`GoalLoopDefaults.MaxAttemptsPerSubGoal` 保持 1:1。四条现有设计均有源码级依据，禁止"顺手改掉"：

1. **不自写 `while (RunStreaming)`** —— 承 [MAF 集成边界](./0007-maf-integration-boundaries.md) 禁止清单。
2. **用 `DelegateLoopEvaluator` 而非 `AIJudgeLoopEvaluator`** —— 成立。`AIJudgeLoopEvaluator` 的 judge
   只收到 `context.InitialMessages` + `context.LastResponse.Text`，**拿不到**构建/测试结果、变更文件、
   工具错误、LSP 诊断。GOAL 的完成条件是这些确定性证据，自研 judge 读
   `GoalSubGoalAssessment.FormatEvidenceForJudge(evidence)` 是能力升级而非重复实现。
3. **`FreshContextPerIteration = true`** —— 对编码任务正确：失败尝试的污染历史不带入下一轮；代价仅是每轮重放
   原始 prompt + 聚合反馈日志（3 轮以内可接受）。
4. **审批不会卡死循环** —— 对应官方"审批 heuristics + loop"范式：`AgentPipelineAssembly.BuildMainRoleOverrides`
   在 `PermissionMode.GoalAuto` 下强制挂审批中间件 + GoalAuto 自动放行规则（破坏性 shell 仍 Deny，但 Deny 在执行期
   中间件即被拒，不会变成 pending request——`AutoApprovalRulesFactory` 仅 Allow 才自动批准）。

### 2. `/loop` 从提示词技能下沉为运行时有界循环

删除 `BundledSkills.CreateLoopSkill`，改为 `LoopCommand` + `IterativeLoopService`
（`src/OneCode.App/Services/Loop/`）。语义：

- **上限由框架强制**：`LoopAgentOptions.MaxIterations` ← 配置 `loop.maxIterations`（默认 3，
  `/loop --max <n>` 可逐次覆盖），评估器无法突破。
- **判据只认确定性证据**：`--check "<命令>"` 退出码 0 即为本轮通过；未提供时退回
  `IVerificationProvider`（构建/测试）。**两者都不可用时 fail-closed**——循环不可能通过，
  跑满上限后判失败。没有可判定证据的循环不准宣称完成。
- **反馈注入真实证据**：未通过时把上一轮实际输出（截断 800 字符）+ 检查命令的退出码与 stdout/stderr
  一起作为 `LoopEvaluation.Continue(feedback)` 回注下一轮。
- **fresh context 不得被历史 provider 抵消**：`FreshContextPerIteration` 只重置 **LoopAgent 自己的会话**，
  它管不到挂在 agent 上的 `ChatHistoryProvider`——后者由 `ChatClientAgent` 在**每次服务调用**上重新查询，
  会把整份 transcript 重新注入每一轮，使 fresh context 形同虚设。因此循环路径的
  `MainAgentRunOptions.ConversationId` 必须为 `null`（不挂 `TranscriptChatHistoryProvider`）。
  GOAL 子目标循环天然如此；`/loop` 曾传入会话 id，已修正。
- **不引入 GOAL 的重机制**：不接 workspace 隔离、operation ledger、step receipt 与三级预算。
  `/loop` 用 `EditTransaction` 收纳本轮改动，未通过则整体回滚。
- **复用不复制**：判定原语与证据格式化复用 `GoalSubGoalAssessment`；组装方式复用 `LoopAgent` +
  `DelegateLoopEvaluator` + `LoopContext.AdditionalProperties` 承载 per-run 状态。

**上限语义（须在文档与 UI 中一致表达）**：`LoopAgent` 的 `MaxIterations` 限制的是 **agent 调用次数**，
且达到上限时**先停后判**——最后一轮不会进入评估器。因此 `--max 3` = 3 次调用 + 最多 2 次完整
"检查 → 重试"。产品层**不做 +1 补偿**：那会让配置值与框架选项脱钩，也让 GOAL 与 `/loop` 语义分叉。

### 3. 权限门禁：循环不得静默放权

`/loop` 以 `MainAgentRunOptions.SuppressToolApproval = true` 运行（不挂交互审批 broker——
否则首轮 pending approval 就会让 `LoopAgent` 停下，整个循环空转）。为避免把"跳过人机审批"变成
隐性的权限放宽，`LoopCommand` 在入口**fail-closed**：当前权限模式必须是
`goalAuto` / `dontAsk` / `auto` / `bypassPermissions` 之一，否则直接报错并说明原因。
权限政策本身仍由 `PermissionChecker` 强制执行，命令只是拒绝在不匹配的模式下启动。

### 4. `GoalSubGoalJudge` 保留自研，但判定规则向框架对齐（闭环结构化输出文本降级未覆贴项）

**保留自研**：理由是决策 1.2 —— judge 必须读确定性证据，`AIJudgeLoopEvaluator` 做不到。

**修正判定规则**：原实现 `verdict.Contains("VERDICT: DONE") → 完成` 是 **fail-open**；
框架 `AIJudgeLoopEvaluator` 用的是 `!contains(MORE) && contains(DONE)`，歧义时 MORE 胜出、循环继续。
原规则下，judge 输出同时含两个标记或格式漂移时，**未完成的子目标会被静默放行为已完成**。
现改为 MORE 优先，`EvaluateFinalGoalAsync` 同步对齐。守卫测试：
`RunAsync_JudgeVerdictAmbiguous_TreatsAsIncompleteInsteadOfAccepting`。

### 5. fresh context 必须回显上一轮输出

官方 `CompletionMarkerLoopEvaluator` 用 `{last_response}` 占位符明确指出：fresh context 下
agent 对上一轮产出**零记忆**，不回显就会重复已完成的动作。本轮为 GOAL 子目标循环与
`/loop` 的 `BuildFeedback` 都补上"## 上一轮已完成的工作"（截断后）段。守卫测试：
`RunAsync_DeterministicGateFails_ReinvokesWithGateFeedbackAndPriorOutputNeverCallingJudge` 等。

## 边界与禁令

1. **不给主 Harness 挂 `LoopEvaluators`**。主对话是交互式的，循环会把审批吞进自主迭代；
   MAF 集成边界亦禁止用主 Harness `LoopEvaluators` 替代 `StateMachineMiddleware` 恢复闸。
2. **不用 `TodoCompletionLoopEvaluator` / `BackgroundTaskCompletionLoopEvaluator` 替换 GOAL 现状**。
   GOAL 的完成条件是确定性门禁 + 证据 judge，与"还有未完成 todo"/"后台任务在跑"语义不同。
3. **不留"提示词版 + 运行时版"双轨 `/loop`**。接入运行时即删除提示词技能（已执行）；
   后续如再引入循环类能力，同样只保留一条运行路径。
4. **`/loop` 不得在无确定性判据时宣称完成**。新增检查来源必须能回答"凭什么算通过"，
   否则宁可以 fail-closed 失败。
5. **`MaxIterations` 与配置 1:1，禁止 +1 补偿**（见决策 2）。澄清：`IterativeLoopService.Summarize` 对未完成场景
   做的 `Iterations + 1` 不是补偿——它只是把**未进入评估器的最后一轮**计入报告的 attempts（上限强停发生在评估之前），
   不改 `MaxIterations` 装配值。禁止的是后者，报告层如实计数不受限。
6. **per-run 循环状态只能存活于 `LoopContext.AdditionalProperties`**，不得放 evaluator 实例字段
   （框架 LoopContext 注释的硬要求：evaluator 可能被并发 run 共享）。GOAL 侧键
   `GoalSubGoalLoop.RunStateKey`，`/loop` 侧键 `IterativeLoopService.RunStateKey`。
7. **循环路径不得挂 agent 级 `ChatHistoryProvider`**（即 `MainAgentRunOptions.ConversationId` 必须为
   `null`）。`FreshContextPerIteration` 重置的是 LoopAgent 的会话，不是历史 provider；挂上历史 provider
   会让每轮重新看到整份 transcript，循环退化为"带着全部历史重试"。守卫测试：
   `IterativeLoopServiceTests.RunAsync_DoesNotMountChatHistoryProvider`。

## 后果

- `/loop` 从一个不可保证的提示词约定变成有界、可验证、进度可见的循环；不收敛时回滚改动而不是把工作区留在半改状态。
- GOAL 侧捎带修掉一处 fail-open 判定缺陷（会静默放行未完成子目标）与 fresh context 失忆问题。
- `LoopAgent` / `LoopEvaluator` 是实验性 API（`MAAI001`），此前的循环语义**零测试覆盖**
  （`GoalWorkflowRuntimeTests` 用 `FakeStepExecutionService` 整条替换掉子目标执行路径）。
  本轮新增 `GoalSubGoalLoopTests`（10 例）与 `IterativeLoopServiceTests`（9 例）锚定循环语义，
  并对 fail-open 判定与"无证据判通过"两道守卫做了反证。
- 成本：`/loop` 新增一条命令（Builtin 22 → 23，总数 44 → 45）与一个配置键；
  DI 注册面快照随之更新（`ServiceRegistrationSnapshot.approved.txt`：/loop 贡献 2 条新增 + 序号重排）。
- 未覆贴：`/loop` 目前只支持单个 `--check` 命令作为判据，多阶段门禁（如"先编译再测试"）
  需由该命令自行串联；`WorkflowResumeKind` 式的循环断点恢复未做（`/loop` 取消后不可续跑）。

## 源码取证索引

> 版本说明：MAF 侧行数/成员以 `dotnet-1.22.0`（pinned 包；本地 checkout `dotnet-1.22.0-6-g669c8b95e` 在其上无差异）为准。
> `Harness/Loop/**` 在 `1.21.0..HEAD` 无差异，可直接按路径查；引用 `ToolApproval/**` 的结论见
> [子代理派工边界](./0011-background-agents-delegation-boundary.md) 证据 2 与 [MAF 集成边界](./0007-maf-integration-boundaries.md)。

| 主题 | 位置 |
|---|---|
| `LoopAgent` 装配、上限先停后判、pending approval 早停、聚合反馈日志 | `agent-framework/dotnet/src/Microsoft.Agents.AI/Harness/Loop/LoopAgent.cs` |
| 上限语义（调用次数）、`FreshContextPerIteration`、`OnBehalfOfAuthorName` / `ExcludeOnBehalfOfMessages` | 同目录 `LoopAgentOptions.cs` |
| per-run 状态必须放 `AdditionalProperties` | 同目录 `LoopContext.cs` |
| 多评估器有序、首个要求重跑者胜出、`Stop` 不是否决权 | `LoopAgent.cs` `EvaluateAndBuildNextAsync` |
| MORE 优先的 fail-safe 判定（决策 4 的对齐基准） | 同目录 `AIJudgeLoopEvaluator.cs` |
| `{last_response}` 回显（决策 5 的依据） | 同目录 `CompletionMarkerLoopEvaluator.cs` |
| GOAL 子目标循环装配 | `src/OneCode.App/Services/Agent/GoalSubGoalLoop.cs` |
| judge 判定与反馈提取 | `src/OneCode.App/Services/Agent/GoalSubGoalJudge.cs` |
| 确定性硬门禁 | `src/OneCode.App/Services/Agent/GoalSubGoalHardGate.cs` |
| GoalAuto 审批放行（决策 1.4） | `src/OneCode.App/Services/Agent/AgentPipelineAssembly.cs` |
| `/loop` 运行时循环 | `src/OneCode.App/Services/Loop/IterativeLoopService.cs` |
| `/loop` 参数解析与权限门 | `src/OneCode.App/Commands/LoopCommand.cs` |
| 命令结果 → dispatch 路由 | `src/OneCode.Core/Commands/ICommand.cs`、`src/OneCode.App/Services/SlashCommandPipeline.cs`、`src/OneCode.App/Tui/OneCodeToplevel.Dispatch.cs` |
| 循环守卫测试 | `src/OneCode.Tests/GoalSubGoalLoopTests.cs`、`src/OneCode.Tests/IterativeLoopServiceTests.cs`、`src/OneCode.Tests/LoopCommandTests.cs` |
