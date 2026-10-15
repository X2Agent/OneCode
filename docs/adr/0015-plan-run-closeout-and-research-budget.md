# Plan run 收口与提交封条

**状态**: Accepted
**日期**: 2026-10-13
**关联**: [代理循环能力边界](./0010-agent-loop-boundaries.md)、[崩溃恢复边界](./0014-crash-recovery-boundary.md)、
[子代理派工体系](../sub-agents.md)、[MAF 接入指引](../maf/integration-guide.md)

---

## 语境

Plan 模式的审批入口只在一个时刻开放：`SubmitPlan` 成功后工作流停在 `FinalizingPlanRun`，
**run 结束**时收口漏斗把它推进到 `AwaitingApproval` 并弹出审批选择器。实测出现过一次死锁：
`SubmitPlan` 已成功，但模型继续调研、反复重提计划，run 迟迟不结束，工作流永远停在
`FinalizingPlanRun`，用户最终只能手动停止——此时计划已落盘却永远无法审批。

同一轮日志暴露了两个相关缺陷：

1. 提交之后的行为完全依赖提示词，模型不听从时没有任何结构性兜底；
2. 收口只接在「正常结束」一条路径上，输出被 hook 拦截、run 异常、进程崩溃都会绕过它。

## 决策

**交付物提交即封口，run 只能以纯文本收尾；收口只有一个入口。**

1. **提交封条（硬闸）**：`SubmitPlan` 成功后在 run 级封条落封，`PermissionAndLimitMiddleware` 见封即拒绝
   本 run 的一切后续工具调用（含重复的 `SubmitPlan`），且在权限询问之前判定。封条是引用对象
   （工具内对 `AsyncLocal` 的写入不回流到外层中间件，对同一引用对象的写入两侧都可见），由引擎在每个
   Plan run 起点放入、run 结束复位。工具返回文本同时给出终局指令，但不依赖模型听从。
2. **提交幂等**：状态机在 `FinalizingPlanRun` 下收到内容哈希相同的提交，返回既有 revision 且不写入；
   内容不同说明计划确实变了，仍按非法状态拒绝。幂等判据落在状态机而非工具层，哈希算法与 revision 落盘同源。
3. **收口单一入口**：run 以任意终态离场（正常结束、输出被 hook 拦截、运行异常、取消）都经
   `BuildRunGate.CloseOutPlanRunAsync`，由 `PlanRunCloser` 推进到待审批或置为已取消。
   「异常」按「已完成」处理：计划已落盘，收尾失败不应让用户失去审批机会；转录协议是否完整由
   `ProtocolValid` 判定。取消保持取消语义，且工具批次落盘先于取消写。
4. **孤儿对账**：旧进程遗留的 `FinalizingPlanRun` 纳入启动恢复扫描，经同一个 `PlanRunCloser` 补做收口。
   孤儿判据是「最后更新早于本进程启动时刻」——`FinalizingPlanRun` 在进程内是活跃 run 的正常中间态，
   不能被扫描抢先收口。
5. **运行期禁止切模式**：agent run 活跃期间 Tab 不循环工作模式；模式决定工具集与权限，
   运行中切换会让工作流与活跃 run 脱节。
6. **Plan 与 Build 的调研边界**：所有只读探索（代码定位与按需外部检索）在 Plan 阶段完成并沉淀入计划正文，
   计划步骤（`steps`）只定义具体的代码/文件变更与验证动作，不把检索本身拆为 Build 步骤，避免跨阶段重复搜索。

## 否决的方案

- **用取消 run 实现快速收口**：取消路径会把工作流置为 `Cancelled`，用户彻底失去审批入口，
  且把正常路径记成取消语义。
- **只靠提示词约束提交后的行为**：这正是出事的原因。
- **在 Plan 阶段按工具类别设置硬编码次数软闸并把剩余检索推给 Build 步骤**：实测证明每轮注入配额余量会诱发模型把额度用满，
  而“超限后把采集写成 Build 步骤”会导致 Build 阶段为了勾选步骤而重复执行 Plan 阶段已完成的检索。

## 后果

- **连带退役**：`BuildPlan.RequireExplicitTaskCompletion`（任务须被执行协议显式完成的校验，
  含 `PlansMatch` 字段比较与执行时校验）随本次重构移除。步骤进度改由待办清单单一事实源承担——
  `PlanTodoSeeder` 预置、模型经 `todos_*` 记账、`TodoMutationObserver` 边界刷新、
  `PlanStepProjector` 投影；收口漏斗保证 run 终态必然流转，不再依赖执行协议自证完成。
- 新增 Plan 终态路径时必须经 `CloseOutPlanRunAsync`，不得另写工作流终态入口。
- 封条只用于「交付物已提交」类 run；其他 run 不挂探针，行为不变。
