# Permission Middleware vs ToolApproval vs FunctionInvocationFilter 职责边界

**状态**: Accepted
**日期**: 2026-07-09
**关联**: 生产级重构计划 §3.0（PERM 核实结论）、AGENTS.md「不许重复造轮子」（§6，库选型表）

## 语境

OneCode 的 MAF (Microsoft.Agents.AI) 管道中存在三种函数调用拦截机制，职责容易混淆：

1. **Permission Middleware** — `AgentPipelineBuilder` 中通过 `.Use()` 注册的委托式中间件（`CheckPermissionAndExecuteAsync`）
2. **ToolApproval** — MAF 内置的 `UseToolApproval` 中间件，处理 `ToolApprovalRequestContent` 协议
3. **FunctionInvocationFilter** — 曾考虑用类型化 Filter 接口通过 DI 注册；**该接口在 MAF 1.22 / Microsoft.Extensions.AI 10.10.0 中不存在**，此路不通（详见「决策」后的说明）

历史上出现过 ApprovalHandler 垫片与 Permission Middleware 决策重叠的问题（PERM-1.5 已清理），需要在 ADR 层面固化三者边界，避免回归。

## 决策

### 三者职责定义

| 机制 | 职责 | 决策权 | 位置 | 适用场景 |
|------|------|--------|------|----------|
| Permission Middleware | Allow/Deny 策略决策 | **有**（最终 Allow/Deny） | 管道中游，ToolApproval 之前 | 基于 `PermissionProfiles` / `IPermissionChecker` 的安全决策 |
| ToolApproval | MAF 协议层审批 | **有**（Ask → AutoRule/用户审批） | 管道中游，Permission 之后 | 处理 `ToolApprovalRequestContent`、AutoApprovalRules 匹配、`ApprovalBroker` 事件桥交互 |
| 观测性中间件（.Use()） | 预算/用量/超长恢复观测 | **无工具调用决策权** | Agent Run 级最外层（包裹 function calling 管道），非工具管道末段 | 日志、指标采集、token 预算短路、PromptTooLong 历史截断重试等 |

> **`IFunctionInvocationFilter` 不可用**：该接口在 MAF 1.13 / Microsoft.Extensions.AI 10.7.0 中不存在，OneCode pinned 的 10.10.0 亦不含。观测性拦截因此用 `.Use()` 委托式中间件实现，语义一致（无工具调用决策权，只观测 run 级行为）。

### 数据流

```
工具调用请求（按 AgentPipelineBuilder 源码注册序；`.Use()` 先注册者最外层）
  → SafetyInvariantMiddleware（安全不变量校验，fail-closed）
  → [HookMiddleware / ToolCallEventMiddleware（按挂载条件）]
  → Permission Middleware（PermissionAndLimitMiddleware：白名单过滤 + Allow/Deny/Ask 决策 + MaxToolCalls 计数）
      ├─ Allow  → 放行到 next
      ├─ Deny   → 返回 ToolResult.Error
      └─ Ask    → 断言该工具带 ApprovalRequiredAIFunction 标记
                      ├─ 有标记 → 放行到 next（交给 ToolApproval 层）
                      └─ 无标记 → fail-closed 返回 ToolResult.Error（无审批依据，不执行）
  → [StateMachineMiddleware（失败追踪恢复闸，仅 Full）]
  → [EditTransactionMiddleware（编辑事务收纳，按启用条件）]
  → EditGuardMiddleware（FileEdit 契约前置校验 + 编辑后验证，契约失败 fail-closed）
  → [ResultBudget / ResultUnwrap（工具结果预算与解包）]
  → ToolApproval（AutoApprovalRules / ApprovalBroker 事件桥）
      ├─ 规则匹配 → 自动放行
      └─ 不匹配   → ToolApprovalRequestContent → ApprovalBroker 事件桥（Main 流式拆分 / Team 工作流审批桥）→ 续跑
  → 实际工具执行
（图外：Agent Run 级最外层还有 BudgetGuard / UsageTracking / PromptTooLongRecovery 三个 .Use() 观测中间件，
 见「当前实现形态」。）
```

### 约束

1. **Permission Middleware 是唯一的安全决策权威**：Allow/Deny 只在此层产生，ToolApproval 不做安全决策（只做协议层审批交互）
2. **ToolApproval 不重复 Permission 逻辑**：Permission 已 Deny 的请求不会到达 ToolApproval 层
3. **观测性中间件无工具调用决策权**：不能对工具调用做 Allow/Deny 或改写其参数/结果；其 run 级行为（token 预算短路、PromptTooLong 历史截断重试）不触及以上边界
4. **禁止同一关注点双重注册**：已在 `.Use()` 中间件处理的关注点，不得重复注册（AGENTS.md「不许重复造轮子」已约定）
5. **Ask 只在审批边界内成立**：权限层的 Ask 本身不产生任何用户交互，只有工具带 `ApprovalRequiredAIFunction` 标记时 MAF 才会把调用换成 `ToolApprovalRequestContent`。因此 Ask 分支必须断言标记存在，缺失时 fail-closed 拒绝——放行等于把 Ask 静默降级成「照常执行」

### 新增观测性中间件的准入条件

新增 `.Use()` 观测性中间件必须在 PR 中说明：
- 为何需要独立的观测层（如：全局指标采集、延迟统计），而非复用 `UseOpenTelemetry` 的 span
- 不与现有管道中间件产生关注点重叠（特别是 Permission/ToolApproval 的决策逻辑）

## 影响

- PERM-1.5 删除 ApprovalHandler 垫片的决策有 ADR 背书
- 未来新增拦截机制时有明确的归属判断依据
- 观测性 `.Use()` 中间件符合本 ADR：即 `OneCode.Infrastructure/Agent/RunMiddleware/` 下的 BudgetGuard / PromptTooLongRecovery / UsageTracking 三个类

## 当前实现形态

**Permission 与 ToolApproval 合并为一道拦截器**：`PermissionAndLimitMiddleware`（`OneCode.Infrastructure/Middleware/PermissionAndLimitMiddleware.cs`）在一个 `.Use()` 中间件内依次完成：

1. `IsToolAllowed` 白名单过滤（超限只失败当前调用，保留批次完整性）
2. 权限检查（`PermissionChecker.CheckAsync`，Allow/Deny/Ask 三路决策）
3. 工具调用计数 + `MaxToolCalls` 上限（`ExecuteWithLimitAsync` 包裹在权限通过之后，被拒绝的调用不计入）
4. 审批路由：Ask → MAF 审批协议（单通道，标记工具产生审批请求；Main 由流式审批拆分、Team 由工作流审批桥呈现；无标记 fail-closed 拒绝）

**审批标记有两个施加点，缺一会让 Ask 静默降级为「照常执行」**（标记类型：MAF 的 `ApprovalRequiredAIFunction`；判定统一走 `ToolMetadataRegistry.RequiresApprovalBoundary`，即 `ApprovalMode != Never`）：

- **装配期**：`ToolApprovalMarker` 包装产品工具目录（`AgentPipelineBuilder` 组装 chat options 时）。
- **请求期**：`ToolApprovalMarkingContextProvider`（`AIContextProvider`）包装 provider 在装配之后才追加的工具——`todos_*` 五工具与 `file_memory_*` 七工具（`HarnessProviderTools` 是这两份名单的唯一来源）。MAF 的 provider 链是替换语义（`InvokingAsync` 的返回值即下一环的输入），所以标记 provider **必须是最后追加的一个**，否则它看不到 Harness provider 已注入的工具。

两个施加点都受 `EnableToolApproval` 门控：没有 `ToolApprovalAgent`、也没有交互审批桥的路径（如 AutoDream）不标记——标记它们会让框架把同一批次的每个函数调用都换成无人能解析的审批请求。

**审批边界的第二类自动批准例外**：`AutoApprovalRulesFactory` 的自动批准集合除「权限检查器判定 Allow」外，还含按名匹配 `todos_*` / `file_memory_*` 的规则（`HarnessProviderTools.AutoApprovalRule`）。这两类工具不属于权限检查器的任何工具分类（非只读、非文件写入、非 Shell），在默认规则集下规则评估会落到 `Ask`；但它们是 agent 自查清单与工作记忆，弹窗只会打断流程。产品判定：注册为 `Risk = Safe` + `ApprovalMode = Conditional`（仍带边界，不绕过协议），由按名规则静默放行。按名放行沿用框架的撞名前提（MCP 工具带 `mcp__{server}__` 前缀，产品工具名来自 `HarnessProviderTools` 的封闭名单）：新增工具前必须核对这两份名单，撞名即等于绕过人机审批边界。

观测性中间件（`OneCode.Infrastructure/Agent/RunMiddleware/` 下的 BudgetGuard / PromptTooLongRecovery / UsageTracking）是 **Agent Run 级最外层**的 `.Use()` 拦截器（注册于全部工具中间件之前）：对工具调用无 Allow/Deny 决策权，与本 ADR 约束一致；其中 BudgetGuard 可对超预算 run 整体短路、PromptTooLongRecovery 可截断消息历史并重试，二者均不介入工具级安全决策。

**AutoApprovalRules 与 Permission 共用同一确定性源**，不用 `PermissionProfile.AutoApprove*` 平行旗标编码同一意图：

- **确定性单一源**：AutoApprovalRulesFactory 复用与 PermissionAndLimitMiddleware 同一 `IPermissionChecker`（`CreateFromChecker`）；无 checker 的调用方才回退 `PermissionProfiles.Check`（fallback，非第二权威）；仅当结果为 Allow 时自动批准。
- **不含 YOLO 双跑**：YOLO 只有一份实现（即 checker 本身，含 Auto 模式规则）；审批层 AutoApprovalRules 与 PermissionAndLimitMiddleware 共用同一 checker，不存在平行旗标。
- **产品定义**：MAF 自动批集合 = 确定性 Permission Allow 集合（含 Default/Plan 下只读工具）+ 按名放行的 provider 注入工具（`todos_*` / `file_memory_*`，见上文「第二类自动批准例外」）。
- **仍保留**：Permission 为唯一 Allow/Deny 安全门；Ask 一律进入 MAF 审批协议（Main 流式拆分 / Team 工作流审批桥）；EnableVerification 仍在 PermissionProfile。

**编辑生命周期只有一道 `EditGuardMiddleware`**（`OneCode.Infrastructure/Middleware/EditGuardMiddleware.cs`），替代了早期的 `ContractMiddleware` + `VerificationMiddleware` 两道结构；其内部分两段：

1. **Pre**：`FileEditContract` 前置契约校验（Edit 目标文件必须存在），失败 fail-closed 返回 `ToolResult.Error` + 恢复指导；
2. **Post**：可选的编辑后验证（`IVerificationProvider`，按 `VerificationOptions.Threshold` 防抖触发编译/类型检查），
   失败时把错误回注工具结果，由 `StateMachineMiddleware` 统一做状态转移。

`FileEditContract.ValidatePostConditionsAsync`（弱后置「文件仍存在」校验）不保留，避免与强校验重复。`VerificationOptions` 位于 `Middleware/VerificationOptions.cs`。

本 ADR 的归属约束不受影响：EditGuard 只做「编辑契约 + 编辑后质量」，**安全决策仍唯一属于 Permission 层**，
路径 scope 检查仍由 `PermissionCheckHelpers.ValidatePath` 负责。

