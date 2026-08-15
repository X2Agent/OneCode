# ADR 0001: Permission Middleware vs ToolApproval vs FunctionInvocationFilter 职责边界

**状态**: Accepted
**日期**: 2026-07-09
**关联**: 生产级重构计划 §3.0（PERM 核实结论）、AGENTS.md §6（中间件约定）

## 语境

OneCode 的 MAF (Microsoft.Agents.AI) 管道中存在三种函数调用拦截机制，职责容易混淆：

1. **Permission Middleware** — `AgentPipelineBuilder` 中通过 `.Use()` 注册的委托式中间件（`CheckPermissionAndExecuteAsync`）
2. **ToolApproval** — MAF 内置的 `UseToolApproval` 中间件，处理 `ToolApprovalRequestContent` 协议
3. **FunctionInvocationFilter** — MAF 提供的 `IFunctionInvocationFilter` 接口，通过 DI 注册

历史上出现过 ApprovalHandler 垫片与 Permission Middleware 决策重叠的问题（PERM-1.5 已清理），需要在 ADR 层面固化三者边界，避免回归。

## 决策

### 三者职责定义

| 机制 | 职责 | 决策权 | 位置 | 适用场景 |
|------|------|--------|------|----------|
| Permission Middleware | Allow/Deny 策略决策 | **有**（最终 Allow/Deny） | 管道中游，ToolApproval 之前 | 基于 `PermissionProfile`/`StrategyRouter` 的安全决策 |
| ToolApproval | MAF 协议层审批 | **有**（Ask → AutoRule/用户审批） | 管道中游，Permission 之后 | 处理 `ToolApprovalRequestContent`、AutoApprovalRules 匹配、IApprovalUi 交互 |
| 观测性中间件（.Use()） | 延迟/指标采集 | **无**（只读） | 管道末段，ToolApproval 之后 | 日志、指标采集、延迟统计等无决策权的观测 |

> **MAF 1.13 限制**：原计划使用 `IFunctionInvocationFilter` 接口（DI 注册，独立于管道），但 MAF 1.13 / Microsoft.Extensions.AI 10.7.0 不包含此接口。MAF-M2 改用 `.Use()` 委托式中间件实现等价的观测性拦截，语义一致（无决策权，只观测）。

### 数据流

```
工具调用请求
  → SafetyInvariantMiddleware（安全不变量校验，fail-closed）
  → ContractMiddleware（契约校验，fail-closed）
  → Permission Middleware（Allow/Deny 决策）
      ├─ Allow  → 放行到 next
      ├─ Deny   → 返回 ToolResult.Error
      └─ Ask    → 放行到 next（交给 ToolApproval 层）
  → ToolApproval（AutoApprovalRules / IApprovalUi）
      ├─ 规则匹配 → 自动放行
      └─ 不匹配   → ToolApprovalRequestContent → IApprovalUi → 续跑
  → 实际工具执行
  → [观测性中间件可观测但不干预]
```

### 约束

1. **Permission Middleware 是唯一的安全决策权威**：Allow/Deny 只在此层产生，ToolApproval 不做安全决策（只做协议层审批交互）
2. **ToolApproval 不重复 Permission 逻辑**：Permission 已 Deny 的请求不会到达 ToolApproval 层
3. **观测性中间件无决策权**：只能观测（记录日志/指标），不能阻止或修改工具调用
4. **禁止同一关注点双重注册**：已在 `.Use()` 中间件处理的关注点，不得重复注册（AGENTS.md §6 已约定）

### 新增观测性中间件的准入条件

新增 `.Use()` 观测性中间件必须在 PR 中说明：
- 为何需要独立的观测层（如：全局指标采集、延迟统计），而非复用 `UseOpenTelemetry` 的 span
- 不与现有管道中间件产生关注点重叠（特别是 Permission/ToolApproval 的决策逻辑）

## 影响

- PERM-1.5 删除 ApprovalHandler 垫片的决策有 ADR 背书
- 未来新增拦截机制时有明确的归属判断依据
- MAF-M2（`tool_latency_metrics` 中间件）符合本 ADR：它是无决策权的观测性拦截器，用 `.Use()` 中间件实现（MAF 1.13 不含 `IFunctionInvocationFilter` 接口）

## 现状更新（2026-08-15）

「Permission 只做安全决策、ToolApproval 专管协议审批」的两独立环节边界，已演进为**合并式拦截器**：`PermissionAndLimitMiddleware`（`OneCode.Infrastructure/Middleware/PermissionAndLimitMiddleware.cs`）在一个 `.Use()` 中间件内依次完成：

1. `IsToolAllowed` 白名单过滤（超限只失败当前调用，保留批次完整性）
2. 工具调用计数 + `MaxToolCalls` 上限（被拒绝的调用不计入）
3. 权限检查（`PermissionChecker.CheckAsync`，Allow/Deny/Ask/Passthrough 四路决策）
4. 审批路由：Ask/Passthrough → MAF `ToolApprovalAgent` 或 inline `ApprovalHandler`（`IApprovalBroker` 抽象，见 ADR 0003 现状注记）

观测性中间件（`RunMiddleware/` 下的 BudgetGuard / PromptTooLongRecovery / UsageTracking）仍为无决策权的 `.Use()` 拦截器，与本 ADR 约束一致。
