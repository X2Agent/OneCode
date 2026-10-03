# Query 流式编排组合契约与状态对象原则

**状态**: Accepted（追认——代码与注释先行，本文档补写）
**日期**: 2026-08-30
**关联**: [M4 完全事件驱动审批](./0003-m4-approval-event-streaming.md)

## 语境

查询层同时承担 Build 门禁/MAF run 驱动的流式编排与事件消化循环。其中消化循环的十几个相互依赖的累加器（文本、轮次、四类 token 计数、CallId 去重集、工具批次收集器、next-prompt 标签解析）以局部变量形式钉死在一个 ~460 行的 `async` 迭代器方法里——C# 的异步迭代器无法与辅助方法共享局部变量，导致该方法不可单测、不可拆分，任何事件语义调整都要在唯一一个巨型方法内完成。

## 决策

1. **对外契约接口化**：`IConversationRunner` + `ICacheSafeParamsProvider` 供 TUI 与后台消费者（Cron / AutoDream）使用；全部流式编排——Build 门禁前置、MAF agent run 驱动、事件消化、终结记账——收敛在 `QueryStreamEngine`。
2. **实现以接口注册进 DI**：`QueryStreamEngine` 由组合根工厂组装（`ChatSessionDependencies` / `ChatObservabilityDependencies` 打包依赖），以两个接口暴露；headless 触发器依赖接口而非具体类，配合 `ToolCatalog` 惰性构建保持构造图为 DAG（工具实例反向依赖本接口的消费者，具体类型互引会导致构造期 DI 无限递归）。
3. **可变流式状态收敛为状态对象**：上述累加器提升为 `StreamingSession`，使 `StreamingSession.Digest` 保持为 `(update, session) → events` 的纯映射，可独立单测；`QueryStreamEngine` 只负责编排（创建 session、驱动 MAF run、分发事件、记账终结）。
4. **CallId 去重属于状态对象职责**：MAF 可能跨 `AgentResponseUpdate` 边界重放同一 `FunctionCallContent`/`FunctionResultContent`（轮次历史回放），由 `StreamingSession` 内的 `_emittedToolCallIds` 保证同一工具调用只产生一次 ToolStart/ToolDone 事件。

## 后果

- 消化语义可单测：`StreamingSessionTests` 直接对 `Digest` 做表驱动验证，无需网络或 TUI。
- 契约稳定：`IConversationRunner` / `ICacheSafeParamsProvider` 的公共面不随编排内部重构（如已引入的 `BuildPreambleRunner`/`ToolAssembler`/`HookDispatcher`/`TranscriptPersistence` 等辅助类与 partial 拆分）而变化。
- 组合面收敛：引擎的依赖经两个打包 record 传递，DI 注册保持一条工厂 + 两个接口别名；新增协作组件均遵循同一组合根模式在引擎内组装。

## 引用

- 代码注释锚点：`QueryStreamEngine.cs`、`StreamingSession.cs`（这些文件的类级注释阐述了本 ADR 的编排契约与状态对象原则）
- 测试：`src/OneCode.Tests/StreamingSessionTests.cs`
