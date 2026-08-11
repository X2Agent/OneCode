# ADR 0003: M4 完全事件驱动审批

**状态**: Accepted
**日期**: 2026-07-10
**关联**: 生产级重构计划 §9.2 M4（纯 MAF 流式审批 UX 原型）
**修订**: R2 — 从"双路径并行原型"彻底重构为"完全事件驱动"

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
- Team 路径与 Main 路径的审批机制不一致（Team 用 `ApprovalHandler` 委托，Main 用 `IApprovalUi`）

## 决策

### 完全事件驱动方案

彻底移除 `IApprovalUi` 抽象，所有审批请求统一通过事件流推送，TUI 消费事件后自主渲染并回传决策。

#### Main 路径

1. `MainAgentRunner` 收集到 `ToolApprovalRequestContent` 时，向 channel 推送 `ApprovalRequestEvent`（携带 RequestId、ToolName、ToolInput + `TaskCompletionSource<ApprovalDecision>`）
2. TUI 通过 `TuiEventMapper.MapQueryEventToTuiEvent` 映射为 `TuiApprovalRequest`
3. TUI 渲染审批组件，用户决策通过 `TuiApprovalRequest.ResponseSource.TrySetResult(decision)` 回传
4. `MainAgentRunner.HandleToolApprovalAsync` await `ApprovalRequestEvent.ResponseSource.Task` 获取决策
5. 决策映射为 MAF 响应：`AllowOnce` → `CreateResponse(true)`、`AllowAlways` → `CreateAlwaysApproveToolResponse`、`Deny` → `CreateResponse(false)`

#### Team 路径

MAF workflow manager 无法处理 `ToolApprovalRequestContent`（与 Main 路径的 `ChatClientAgent` 不同），Team 路径必须使用 inline `ApprovalHandler` 委托。但该委托不再调用 `IApprovalUi`，而是：

1. `TeamEventMapper.CreateEventHandler` 构造 `OrchestrationEvent.ApprovalRequest` 并通过 `eventSink` 推送
2. `TuiEventMapper.MapOrchestrationEventToTuiEvent` 映射为 `TuiApprovalRequest`，桥接 `ResponseSource`
3. TUI 决策通过 `TuiApprovalRequest.ResponseSource` 回传到 `OrchestrationEvent.ApprovalRequest.ResponseSource`
4. `CreateEventHandler` await `ResponseSource.Task` 获取决策，30 秒超时 fail-safe Deny
5. `EnableToolApproval` 始终为 `false`（Team 路径不使用 MAF 的 ToolApproval 机制）

### 统一决策枚举

`ApprovalDecision` 从 `IApprovalUi` 的内部枚举提升为 `OneCode.Core.Permissions` 命名空间的独立公共枚举：
- `AllowOnce` — 允许本次执行
- `AllowAlways` — 允许后续所有执行（MAF 会记住该工具）
- `Deny` — 拒绝执行

## 影响

### 新增类型

| 类型 | 位置 | 职责 |
|------|------|------|
| `ApprovalRequestEvent` | `Query/QueryEventTypes.cs` | QueryEvent 子类型，Main 路径审批请求载体 + ResponseSource |
| `TuiApprovalRequest` | `Tui/TuiEvent.cs` | TuiEvent 子类型，TUI 层审批请求 + ResponseSource 回调 |
| `OrchestrationEvent.ApprovalRequest` | `Coordinator/OrchestrationEvent.cs` | Team 路径审批请求载体 + ResponseSource |
| `ApprovalDecision` | `Permissions/ApprovalDecision.cs` | 独立公共枚举（AllowOnce/AllowAlways/Deny） |
| `PermissionCheckHelpers.ApprovalRequiredTools` | `Permissions/PermissionCheckHelpers.cs` | 需包装为 `ApprovalRequiredAIFunction` 的危险工具名单 |

### 删除类型

| 类型 | 原位置 | 删除原因 |
|------|------|------|
| `IApprovalUi` | `Permissions/IApprovalUi.cs` | 抽象被完全事件流替代 |
| `TuiPermissionUi` | `Tui/TuiPermissionUi.cs` | TUI 审批改为消费 `TuiApprovalRequest` 事件 |
| `ConsolePermissionUi` | `Tui/ConsolePermissionUi.cs` | headless 路径改为依赖 `PermissionMode` 策略 |

### 修改文件

| 文件 | 变更 |
|------|------|
| `MainAgentRunner.Approval.cs` | `HandleToolApprovalAsync` 改为纯事件驱动（推送事件 + await ResponseSource） |
| `MainAgentRunner.cs` | `BuildChatOptions` 使用 `PermissionCheckHelpers.ApprovalRequiredTools` 包装危险工具 |
| `TuiEventMapper.cs` | 新增 Main 路径 `ApprovalRequestEvent` → `TuiApprovalRequest` 映射 + Team 路径 `OrchestrationEvent.ApprovalRequest` → `TuiApprovalRequest` 映射，均桥接 ResponseSource |
| `TeamEventMapper.cs` | 重写 `CreateApprovalHandler`，移除 `IApprovalUi` 参数，改为 `CreateEventHandler` 推送 `OrchestrationEvent.ApprovalRequest` |
| `TeamAgentFactory.cs` / `TeamOrchestrationService.cs` | 移除 `IApprovalUi` 构造参数 |
| `CodeAssistantToplevel.Events.cs` | `DispatchEvent` 消费 `TuiApprovalRequest`，`HandleApprovalRequestAsync` 异步处理 |
| `CronJobExecutor.cs` | 移除 `canUseTool` + `ReadOnlyHandler`，改用 `workingMode: WorkingMode.Plan` 实现只读策略 |
| `ChatService.cs` / `IConversationRunner.cs` / `QueryStreamService.cs` | 移除 `canUseTool` 参数，新增 `ApprovalRequestEvent` 透传 |
| `ServiceCollectionExtensions.Business.cs` / `ServiceCollectionExtensions.Advanced.cs` | 移除 `IApprovalUi` DI 注册与 Team 路径注入 |
| `PipelineSecurityContext` / `AgentPipelineOptionsFactory` | 移除 `ApprovalUi` 字段 |
| `MainAgentRunner.Pipeline.cs` / `MainAgentContracts.cs` / `ForkedAgentRunner.cs` | 移除 `ApprovalUi` / `ApprovalHandler` 字段与传递 |
| `GoalAgentModels.cs` / `GoalSubGoalExecutor.cs` / `OrchestrationStreamService.cs` | 移除 `ApprovalHandler` 传递 |

### 约束

1. **所有路径统一事件驱动**：Main 路径（流式 + 非流式）与 Team 路径均通过事件推送审批请求，无同步阻塞调用
2. **事件即决策通道**：TUI 必须通过 `ResponseSource` 回传决策，否则 `MainAgentRunner` 会无限等待（Main 路径）或 30 秒超时 Deny（Team 路径）
3. **ResponseSource 一次性设置**：`TaskCompletionSource` 只能设置一次结果，重复设置会被忽略
4. **Cron 路径无审批 UI**：headless cron 运行依赖 `WorkingMode.Plan` 在权限检查层直接 Deny 写入工具，不触发审批事件
5. **ApprovalRequiredTools 统一来源**：危险工具名单定义在 `PermissionCheckHelpers.ApprovalRequiredTools`，包含文件写入工具 + Shell 工具，只读工具（含 WebFetch）不在其中

## 测试

5 个 M4 单测覆盖：
- 事件映射正确性（`ApprovalRequestEvent` → `TuiApprovalRequest`）
- 回调桥接（AllowAlways / Deny 决策回传）
- 异常传播（TUI 设置异常 → 原始事件收到异常）
- 初始状态验证（ResponseSource 初始化为未完成）

全量 1468 测试通过，0 回归（较 R1 原型减少 1 个，因合并了 `BuildChatOptions` 的两个冗余测试为单个）。

## 反思

R1 原型的"双路径并行"是典型的兼容性陷阱：为了不破坏现有代码而保留旧抽象，导致两套机制并存、维护成本翻倍。R2 彻底重构后：
- 删除 3 个文件（`IApprovalUi` / `TuiPermissionUi` / `ConsolePermissionUi`）
- 统一了 Main 路径与 Team 路径的审批机制（均为事件驱动）
- `ApprovalDecision` 提升为公共枚举，消除对 `IApprovalUi` 的耦合
- `ApprovalRequiredTools` 统一到 `PermissionCheckHelpers`，遵循"白名单统一来源"约定
