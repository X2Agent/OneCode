# Query 流式编排组合契约与状态对象原则

**状态**: Accepted（追认——代码与注释先行，本文档补写）
**日期**: 2026-08-30
**关联**: [M4 完全事件驱动审批](./0003-m4-approval-event-streaming.md)

## 语境

`ChatService` 一度同时承担三重职责：对外契约门面、Build 门禁/MAF run 驱动的流式编排、以及事件消化循环本身。其中消化循环的十几个相互依赖的累加器（文本、轮次、四类 token 计数、CallId 去重集、工具批次收集器、next-prompt 标签解析）以局部变量形式钉死在一个 ~460 行的 `async` 迭代器方法里——C# 的异步迭代器无法与辅助方法共享局部变量，导致该方法不可单测、不可拆分，任何事件语义调整都要在唯一一个巨型方法内完成。

## 决策

1. **ChatService 只做门面**：保留公共查询契约（`IConversationRunner` + `ICacheSafeParamsProvider`）供 TUI 与后台消费者（Cron / AutoDream）使用；全部流式编排——Build 门禁前置、MAF agent run 驱动、事件消化、终结记账——委托给 `QueryStreamEngine`。
2. **组合根组装，不进 DI**：`QueryStreamEngine` 在 `ChatService` 构造函数内组装（与 `BuildRunGate` 相同的组合根模式），不单独注册 DI，也绝不反向引用 `ChatService`。`IConversationRunner` 接口用于断开 `ChatService → ToolCatalog → CronTool → CronSchedulerService → ICronJobExecutor → ChatService` 的循环依赖（headless 触发器依赖接口而非具体类）。
3. **可变流式状态收敛为状态对象**：上述累加器提升为 `StreamingSession`，使 `StreamingSession.Digest` 保持为 `(update, session) → events` 的纯映射，可独立单测；`QueryStreamEngine` 只负责编排（创建 session、驱动 MAF run、分发事件、记账终结）。
4. **CallId 去重属于状态对象职责**：MAF 可能跨 `AgentResponseUpdate` 边界重放同一 `FunctionCallContent`/`FunctionResultContent`（轮次历史回放），由 `StreamingSession` 内的 `_emittedToolCallIds` 保证同一工具调用只产生一次 ToolStart/ToolDone 事件。

## 后果

- 消化语义可单测：`StreamingSessionTests` 直接对 `Digest` 做表驱动验证，无需网络或 TUI。
- 门面稳定：`ChatService` 的公共面不随编排内部重构（如后续 partial 拆分、`BuildPreambleRunner`/`ToolAssembler`/`HookDispatcher`/`TranscriptPersistence` 等辅助类的引入）而变化。
- 组合面收敛：引擎不注册 DI，避免了 8+ 参数服务注册面扩大；新增协作组件均遵循同一组合根模式在引擎内组装。

## 引用

- 代码注释锚点：`ChatService.cs`、`QueryStreamEngine.cs`、`StreamingSession.cs`（这些文件的类级注释阐述了本 ADR 的编排契约与状态对象原则）
- 测试：`src/OneCode.Tests/StreamingSessionTests.cs`
