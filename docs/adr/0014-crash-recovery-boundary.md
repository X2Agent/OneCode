# 崩溃恢复边界：回滚仅依赖进程内 EditTransaction

**状态**: Accepted
**日期**: 2026-10-12
**关联**: [代理循环能力边界](./0010-agent-loop-boundaries.md)（`/loop` 的 EditTransaction 回滚）、
[子代理派工体系](../sub-agents.md)（fork 自建事务 / Goal SharedTransaction）

---

## 语境

GOAL / BUILD / `/loop` 的改动收纳与回滚由 `EditTransactionMiddleware` 建立的
`EditTransaction` 承担：写入文件前先在内存中快照原内容（`Dictionary<string, byte[]>`），
Agent 正常完成时 `Commit()` 丢弃快照，异常 / 取消 / 门控拒绝 / 预算熔断时
`Dispose()` 恢复快照——回滚语义完整，但**只存在于进程存活期间**。

需要决策记录明确「进程崩溃后是否需要跨进程持久化 intent 自动回滚」的架构边界，防止后续
从零重讨论或误以为漏掉恢复能力。

## 决策

**不维护跨进程持久化的操作账本。** 回滚只在进程存活期间有效：

- 进程内异常路径（工具失败、审批拒绝、预算熔断、用户取消、loop 判据不通过）→ 自动回滚，行为不变。
- 进程崩溃 / 被强杀 → **不存在持久化 intent，不做自动恢复**。工作树停留在崩溃前最后状态，
  由用户以 git（`git status` / `git stash` / `git checkout`）自行处置。
- **受控 BUILD attempt 的有意例外**：attempt 内只有用户显式取消才回滚；校验/记账失败、
  轮数或预算耗尽、代理异常一律解除自动回滚保留产物（`EditTransaction.PreserveForManualReconciliation`）。
  这是对上一条的有意收窄——几十轮的产物不因机器记账问题被整体丢弃。产物落盘后工作树即事实源，
  善后（回滚/保留）由用户以 git 处置，与「进程崩溃交 git」同一原则，不在应用内建设显式回滚通道。

理由：

1. **价值/复杂度不成比例**：崩溃后「自动回滚半成品改动」并不总是用户想要的结果——
   用户可能恰恰想保留已落盘的部分改动继续手工处理；自动回滚反而制造数据丢失。
2. **git 已是最好的持久化恢复工具**：工作目录本身在 git 仓库内，崩溃恢复交给版本控制，
   不在应用内重复建设。
3. **账本链路自身即风险源**：落盘账本引入 fence 文件、跨进程锁、半写入恢复等新失效模式，
   其自身的可靠性负担超过被保护路径。

## 后果

- `EditTransaction` 保持纯内存快照实现，无持久化义务（无落盘快照、无跨进程恢复入口）；
  `EditTransactionMiddleware` / `GoalWorkflowRuntime` / `ControlledBuildAttemptRuntime` /
  `BuildRunGate` 等编排层不出现账本接线。
- 文档与提示词不得承诺「崩溃后自动恢复」；用户文档以「异常取消自动回滚，进程崩溃后用 git 处置」为准。
- 若未来确需崩溃恢复（如长时 BUILD 的无人值守场景），须先推翻本决策：以新决策记录评估
  「恢复粒度、intent 存储位置、与 git 的交互、账本自身失效模式」四项后再引入实现。