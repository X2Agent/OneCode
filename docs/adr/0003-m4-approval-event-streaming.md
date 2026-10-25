# M4 审批交互端口

**状态**: Accepted
**日期**: 2026-07-10
**关联**: 生产级重构计划 §9.2 M4、[UI 契约单层化](./0017-ui-contract-single-layer.md)（审批不进事件流、交互收敛为 Core 端口的最终形态）
**修订**: R3 — 审批通道从「事件流内回传」重构为「交互端口投递」

## 语境

Sprint 2 实现的 PERM-1.5~1.8 流式审批链存在 UX 限制：
- 审批请求被 `MainAgentRunner` 截留后**同步调用** `IApprovalUi`，阻塞流式管道
- 审批提示与流式输出是**两条独立通道**（`writer.WriteAsync(evt)` vs `IApprovalUi`），无法在渲染层自然融合
- 无法支持富内联审批 UX（如 diff 旁的批准按钮）

MAF 1.13 的 `ToolApprovalAgentOptions` 仅暴露 `AutoApprovalRules`，**无 UX 回调机制**。"纯 MAF 流式审批"无法脱离外部编排，只能改进 UX 层。

### 原型阶段的问题

R1 原型（2026-07-09）采用"双路径并行"策略：事件推送用于 UX 增强，实际决策仍通过 `IApprovalUi` 获取。这导致：
- `IApprovalUi` 抽象成为永久兼容负担，无法删除
- 两条决策通道并存，逻辑分叉难以维护

R2 曾把审批请求放进运行事件流（`ApprovalRequestEvent` 携带 `TaskCompletionSource` 回传），使事件流无法被非 TUI 前端消费；该形态已由 [UI 契约单层化](./0017-ui-contract-single-layer.md) 取代为交互端口。

## 决策

审批决策经**交互端口**完成，不进运行事件流：

1. `ApprovalDecision` 是 `OneCode.Core.Permissions` 的独立公共枚举（取值见下文「统一决策枚举」）。
2. `IApprovalBroker`（`src/OneCode.App/Services/Agent/ApprovalBroker.cs`）持有 run 级放行状态与 fail-closed 分支；「怎么问用户」外置为 `IApprovalPromptSink`（`src/OneCode.Core/Permissions/IApprovalPromptSink.cs`）。
3. App 侧交互式实现 `InteractionApprovalPromptSink`（`src/OneCode.App/Services/Interaction/InteractionApprovalPromptSink.cs`）构造 `ApprovalInteractionRequest`（`TaskCompletionSource<ApprovalDecision>` 只存在于交互请求 record 内部），经 `IInteractionDispatcher.TrySend` 投递到前端；无接收者时 fail-closed `Deny`。
4. 前端只实现 `IInteractionDispatcher`（TUI：`TuiInteractionDispatcher`；WebUI：`WebInteractionDispatcher`），把请求渲染为审批卡片并经 `ResponseSource.TrySetResult` 回传决策。
5. 无头宿主（cron / daemon）不注册交互 dispatcher：审批请求投递即拒绝，工具是否允许由权限检查层（`PermissionMode` + `unattended`）决定。

### 决策映射

`MainAgentRunner.HandleToolApprovalAsync` 经 `IApprovalBroker.RequestAsync`（`MainAgentRunner` 构造 `ApprovalBroker.ForQuery(...)`）获取决策，映射为 MAF 响应：
`AllowOnce` → `CreateResponse(true)`、`AllowAlways` → `CreateAlwaysApproveToolResponse`、`AllowAllConversation` → `CreateResponse(true)`（并置位 run 级全放行标志）、`Deny` → `CreateResponse(false)`。

### 统一决策枚举

`ApprovalDecision` 从 `IApprovalUi` 的内部枚举提升为 `OneCode.Core.Permissions` 命名空间的独立公共枚举：
- `AllowOnce` — 允许本次执行
- `AllowAlways` — 允许后续所有执行（MAF 会记住该工具）
- `AllowAllConversation` — 本次对话全部允许：当前调用照常批准，同一 run 内后续审批由 `ApprovalBroker` 自动放行；跨 run 的会话级放行由 `PermissionModeProvider` 运行时覆盖（BypassPermissions）承担
- `Deny` — 拒绝执行

## 影响

### 现役类型

| 类型 | 位置 | 职责 |
|------|------|------|
| `IApprovalPromptSink` | `src/OneCode.Core/Permissions/IApprovalPromptSink.cs` | 「怎么问用户」端口 |
| `IApprovalBroker` | `src/OneCode.Core/Permissions/IApprovalBroker.cs` | run 级放行状态与审批门 |
| `ApprovalInteractionRequest` | `src/OneCode.App/Services/Interaction/InteractionRequest.cs` | 交互请求 + ResponseSource 回调（仅进程内，不进运行事件流） |
| `InteractionApprovalPromptSink` | `src/OneCode.App/Services/Interaction/InteractionApprovalPromptSink.cs` | App 侧交互式审批实现（投递 + 等待 + 取消） |
| `ApprovalDecision` | `src/OneCode.Core/Permissions/ApprovalDecision.cs` | 独立公共枚举（AllowOnce/AllowAlways/AllowAllConversation/Deny） |
| 危险工具标记 | `ToolApprovalMarker` + `ToolMetadataRegistry.RequiresApprovalBoundary` | 管道构建期标记需审批边界的工具；判别用 `ToolNames.FileWriteTools`/`ReadOnlyTools` + `IsReadOnlyShell`/`IsDestructiveShell` 运行时分类 |

### 已删除类型

| 类型 | 原位置 | 后继 |
|------|------|------|
| `IApprovalUi` | `Permissions/IApprovalUi.cs`（已删除） | `IApprovalPromptSink` 端口 |
| `TuiPermissionUi` | `Tui/TuiPermissionUi.cs`（已删除） | TUI 审批改由交互端口的请求驱动 |
| `ConsolePermissionUi` | `Tui/ConsolePermissionUi.cs`（已删除） | headless 路径改为依赖 `PermissionMode` 策略 |
| `ApprovalRequestEvent` | `Query/QueryEventTypes.cs`（类型已删除） | 审批不进事件流，改走 `IApprovalPromptSink` 端口 |

### 修改文件

| 文件 | 变更 |
|------|------|
| `MainAgentRunner.Approval.cs` | `HandleToolApprovalAsync` 经 `IApprovalBroker` + `IApprovalPromptSink` 获取决策 |
| `MainAgentRunner.cs` | `BuildChatOptions` 不再包装危险工具；该职责集中于 `AgentPipelineBuilder` 的 `ToolApprovalMarker` |
| `OneCodeToplevel.Events.cs` | `DispatchEvent` 消费 `QueryEvent` 渲染事件；审批请求由 `TuiInteractionDispatcher` 投递 |
| `CronJobExecutor.cs` | 移除 `canUseTool` + `ReadOnlyHandler`，改用 headless 无人值守模式（`workingMode: WorkingMode.Build` + `unattended: true`，交互 dispatcher 无接收者） |
| `QueryStreamEngine.cs` / `IConversationRunner.cs` / `QueryStreamService.cs` | 移除 `canUseTool` 参数；事件流只承载渲染事件 |
| 原 `ServiceCollectionExtensions.Business.cs`（已解散，现位于各领域注册类，如 `src/OneCode.App/Services/Agent/AgentServiceCollectionExtensions.cs`） | 移除 `IApprovalUi` DI 注册 |
| `PipelineSecurityContext` / `AgentPipelineOptionsFactory` | 移除 `ApprovalUi` 字段 |
| `MainAgentRunner.Pipeline.cs` / `MainAgentContracts.cs` / `ForkedAgentRunner.cs` | 移除 `ApprovalUi` / `ApprovalHandler` 字段与传递 |
| `GoalAgentModels.cs` / `GoalSubGoalExecutor.cs` / `OrchestrationStreamService.cs` | 移除 `ApprovalHandler` 传递 |

### 约束

1. **审批不进事件流**：`QueryEvent` 只承载渲染事件；审批请求经 `IApprovalPromptSink` 端口投递，事件流不携带等待句柄（见 [UI 契约单层化](./0017-ui-contract-single-layer.md)）。
2. **无固定超时**：仅依赖 `ct` 取消——对 UI 异常/内部取消 fail-closed `Deny`，对调用方取消选择传播。
3. **ResponseSource 一次性设置**：`TaskCompletionSource` 只能设置一次结果，重复设置会被忽略。
4. **Cron 路径无审批 UI**：headless cron 以 `WorkingMode.Build` + `unattended: true` 运行，交互 dispatcher 无接收者，审批请求即拒绝；工具是否允许由权限检查层决定。
5. **危险工具判别为运行时分类**：`ToolNames.FileWriteTools` / `ReadOnlyTools` + `IsReadOnlyShell` / `IsDestructiveShell` 分类器 + `AutoAllowFileWriteAndShell`，标记经 `ToolApprovalMarker` 在管道构建期完成；只读工具不在危险集合。

## 测试

- `ApprovalBrokerCancellationTests` / `ApprovalBrokerEscalationTests`：`ApprovalBroker` 与 `IApprovalPromptSink` 替身的取消传播与决策升级。
- `OneCodeToplevelApprovalTests`：TUI 审批卡片端到端渲染与决策回传。

## 反思

R1 原型的"双路径并行"是典型的兼容性陷阱：为了不破坏现有代码而保留旧抽象，导致两套机制并存、维护成本翻倍。R2 移除 `IApprovalUi` 收敛为单一决策通道；R3 进一步把「审批在事件流内回传」改为交互端口，使 `QueryEvent` 事件流可被任意前端消费（见 [UI 契约单层化](./0017-ui-contract-single-layer.md)）：
- 审批 / 提问 / 预算四类交互统一走 Core 端口 + `IInteractionDispatcher` 投递
- `TaskCompletionSource` 只存在于 App 的交互请求 record 内部
- 危险工具判别收敛为 `PermissionCheckHelpers` 运行时分类 + 管道构建期 `ToolApprovalMarker` 标记，遵循"白名单统一来源"约定