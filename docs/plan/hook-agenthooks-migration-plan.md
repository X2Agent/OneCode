# OneCode Hook 彻底迁移 MAF AgentHooks 重构计划

> **状态**：计划（未实施）。依据 OneCode Hook 模块源码 + MAF `Microsoft.Agents.AI` /
> `Microsoft.Agents.AI.AgentHooks` 源码核实（含 ADR-0035）制定。
> **关联**：[ADR 0005](../adr/0005-hook-module-design.md)、[hooks.md](../hooks.md)、
> [旧重构计划（自有内核修复路线，已废弃）](./hook-module-refactoring-plan.md)。
> **决策已冻结**：1. MAF 固定 8 个拦截点够用；2. `hooks.json` 保留，作为编译成标准
> `IInterceptor` 的用户侧 DSL；3. 旧事件直接删，无兼容层、无过渡期；
> 4. 只要标准协议，代价可接受。

## 0. 一句话方案

```text
hooks.json (DSL, 只写 8 种标准 event) + notification-providers.json(渠道声明保留)
        │   HookCompiler(校验 + 编译成 IInterceptor)
        ▼
AgentHooksOptions → AsAIAgentWithAgentHooks 工厂拥有三缝装配
  AgentHooksAgent(agent_startup/input/output/agent_shutdown, 流式缓冲, 历史门控)
    → 函数中间件(pre/post_tool_call)
      → ChatClientAgent → AgentHooksChatClient(pre/post_model_call)
宿主只留：信任预门 + Stop 纠偏续跑 + 会话聚合 + 管线重建 + 可观测
```

可删除：`HookExecutionService`、`HookRegistry`、`HookResultAggregator`、
`HookMiddleware`（被函数缝取代）、`GlobHookMatcher`（逻辑并入编译器）、
旧 5 事件的全部触发点与文档。新增：编译器 + 三桥接拦截器 + 管线重排 +
重建机制 + 管线级测试。P0 脏活（超时杀进程、取消透传、输出上限、脱敏、
JSON 合法性）在桥内重做，只是换接口形状。

## 1. 为什么采用 MAF AgentHooks（判定封存）

MAF 提供了 AgentHooks（`agent-framework/dotnet/src/Microsoft.Agents.AI.AgentHooks`，
设计见 `agent-framework/docs/decisions/0035-dotnet-agent-hooks-enforcement.md`），
是对 **AGENT-HOOKS-0.1** 拦截协议（`ResponsibleAI.AgentHooks 0.1.0-alpha.5`）的实现。

- 它解决的是**策略拦截**（裁决、fail-closed、历史持久门控、transform 写回、
  流式缓冲、RecordSink 统一审计）；OneCode 旧 Hook 解决的是**用户扩展注入**。
- 本次判定是**后者服从前者**：用户扩展收敛到 8 个标准拦截点，以标准协议为准，
  旧的直接删。不是语义相同，而是接受标准语义、承担破坏性变更代价。
- `AsAIAgentWithAgentHooks` 工厂以“不可分割整体”装配三层，且显式拒绝
  `UseProvidedChatClientAsIs`、调用方自带 `ChatClientFactory`、
  已含 `FunctionInvokingChatClient` 的 chat client（loud fail）。
  因此 `AgentPipelineBuilder` 必须交出装配权，按缝外/缝内重排。
- 依赖代价已接受：alpha 后缀 + `<IsAotCompatible>false</IsAotCompatible>`
  （反射 STJ wire 编解码）+ 外部包 + 本地源码与 MAF `1.21.0` 版本对应待证实
  （阶段 0 spike 验证）。

## 2. 事件映射终表（11 → 8，旧的直接删）

标准 8 点：`agent_startup / input / output / agent_shutdown / pre_model_call /
post_model_call / pre_tool_call / post_tool_call`。

| 旧事件 | 去向 | 说明 |
|---|---|---|
| `PreToolUse` | `pre_tool_call` | 直接迁。exit 2 → deny，走工具错误载荷、循环继续（保批次） |
| `PostToolUse` | `post_tool_call` | 迁，但桥内**显式丢弃 transform**，保持纯审计语义 |
| `UserPromptSubmit` | `input` | 迁。“拒本轮输入”= input deny；“改写输入”= transform，**默认关闭**，需显式开启 + 安全评审 |
| `Stop` | `output` | 语义反转：output deny 终结整轮 + 丢历史。纠偏续跑搬到外层 catch `InterceptionBlockedException` 有限重跑，durable Build 上限保留 |
| `SessionStart` / `SessionEnd` | `agent_startup` / `agent_shutdown` | 迁。per-run 与会话边界错位由宿主拼（多 run 聚合）；`SessionEnd` 关闭总预算保留在宿主 |
| `StopFailure` | **删** | 无对应点，不保留近似；触发点、文档、测试同步删 |
| `PreCompact` / `PostCompact` | **删** | 标准无概念；`CompactService` / `PromptTooLongRecovery` 内触发点删除 |
| `GoalStageInvoke` | **删** | 直接删；`GoalDecomposer` 内触发点删除 |
| `Notification`（事件） | **删** | 作为事件删除；通知能力保留：`RecordSink` 审计 + `notification-providers.json` 供桥调用 |

删事件的连带动作（实施时逐项执行，不做兼容层）：

1. `HookEvent` 枚举删除 5 项；`HookEventMetadataRegistry` 同步删除；
   `/hooks events` 输出自动收敛。
2. 各业务模块触发点删除 `FireAsync` 调用（Query/Compact/Goal/Notification/Session 相关）。
3. `hooks.md`、`settings.md`、`commands.md`、ADR-0005 相关章节删除或改写；全文检索幽灵类名/路径。
4. 旧 `hooks.json` 写了被删事件的用户配置：编译期**硬报错**（“已删除，无映射”），
   不静默忽略、不自动迁移。

`matcher` 收敛：仅 tool 点支持按工具名过滤；model 点按 modelId；
input/output/agent 点不支持 matcher（全量）。旧自定义 matcher 字段废弃，编译期报错。

## 3. `hooks.json` DSL v2 形态

`hooks.json` **不是标准协议的一部分**。标准只认进程内 `IInterceptor` 对象 +
`AgentHooksOptions`。保留 `hooks.json` = 保留一层**非标准 DSL 编译器**：
文件还在、语义变“声明拦截器”，启动时编译成 `IInterceptor` 交给工厂执行。
用户零迁移（改改 `event` 名即可）；代价是长期维护“DSL 语义 vs 标准语义”两套解释。

- `event` 只允许 8 个标准点；旧 11 种写法编译期报错。
- 每条目保留：`matcher`（仅 tool 点）、`priority`（→注册顺序，小先执行）、
  `once`（→拦截器内进程级一次状态）、`type: command/http/notification` + 执行参数、
  `timeoutMs`（统一收口）、`shell`（实现或删字段二选一，不再静默忽略）。
- 校验失败逐项诊断（替代 `HookLoadDiagnostics`）；整文件失败则整代不生效
  （last-good 粒度从“注册表交换”变为“管线重建回滚”，见阶段 4）。

## 4. 目标架构与模块归属

- **编译器（新增，App 层）**：schema 校验 → matcher 编译 → priority 排序 →
  `once` 状态建模 → 执行参数固化 → 诊断。输出直接是 `AgentHooksOptions`。
- **三桥接拦截器（新增，App 层，实现标准 `IInterceptor`）**：
  Command 桥（stdin JSON、exit 2 判阻断、stderr 传递、stdout 决策 JSON、超时杀进程、
  输出上限、secret 脱敏）；Http 桥（区分 text/JSON/URL 模板语义、响应截断、脱敏）；
  Notification 桥（复用现有 Provider 签名/响应解析 + 声明式渠道定义）。
- **管线（Infrastructure 层改造）**：`AgentPipelineBuilder` 交出装配权，
  改由工厂组装；BudgetGuard、UsageTracking、PromptTooLongRecovery、审批包装
  按缝外/缝内重排，顺序测试锁死；Main/Worker/Team/子 agent 逐个安装。
- **宿主保留**：工作区信任预门（进桥前）、Stop 纠偏续跑、会话多 run 聚合、
  关闭总预算、热重载触发管线重建、`/hooks` 可观测（编译结果 + `RecordSink` 审计流）。

约束：不新增第九个拦截点、不另建事件总线；transform 默认关闭；
取消（OCE）永远透传不降级；观察型失败不覆盖主结果。

## 5. 分阶段交付

### 阶段 0：冻结 + Spike（不开工门禁）

1. 版本对齐：本地 `agent-framework` HEAD vs 引用 MAF `1.21.0` 是否一致；
   `ResponsibleAI.AgentHooks` 包可获得性；`IInterceptor/Emitter/Builder/Composition/
   EnforcementMode/RecordSink/Timeout` 默认语义实测。
2. 工厂全文核实：host-owned-session 重载、per-run provider override 包装、
   嵌套/子 agent 行为。
3. 最小原型：Command 桥跑通 `pre_tool_call` 阻断 + `post_tool_call` 审计 +
   流式缓冲体感 + 历史门控与 Session 管理冲突清单。
4. 产出：映射终表定稿（即 §2）、破坏性清单、包引入评审
   （alpha + `IsAotCompatible=false` 接受记录）、现有 build/test 基线。
5. 退出条件：以上四个问题都有实测答案才开工。

### 阶段 1：依赖 + 管线交权

1. 引入 `Microsoft.Agents.AI.AgentHooks` + `ResponsibleAI.AgentHooks`（版本钉死 + 接受记录）。
2. `AgentPipelineBuilder` 改工厂组装；BudgetGuard、UsageTracking、
   PromptTooLongRecovery、审批包装重排；顺序测试锁死“工具不得在 verdict 下执行”。
3. 先观察模式（只 `RecordSink` 对比 verdict）影子跑，再切强制。
4. 验收：流式/非流式、多工具批次、审批 + 拒绝、取消透传、Main/Worker/Team 装配。

### 阶段 2：编译器 + 三桥

按 §3、§4 实现；验收矩阵：成功/失败/超时/取消、`once` 并发不重放、
失败不解释为成功、取消不降级。

### 阶段 3：生命周期缺口收口

Stop 纠偏外置续跑（含上限 + durable Build）、会话聚合、关闭总预算、
5 个被删事件的触发点/文档/测试同步删除；历史字段
`UpdatedInput/PreventContinuation/AdditionalContexts` 无唯一消费者则删承诺。

### 阶段 4：可观测 + 平台边界 + 热重载

`/hooks` 重写为编译后视图 + 审计流；`IFileSystem` 边界、脱敏、读取上限；
文档三件套同步；热重载改为**防抖 → 编译 → 新管线 → 原子切换 → 失败回滚**，
flight 中 run 归属与旧 `once` 状态迁移规则写死并单独压测
（最易出生产事故的一块）。

### 阶段 5：门禁与切换

`dotnet build/test OneCode.slnx` + 真实子进程/回环 HTTP/配置写入重载测试
（mock 执行器测试基本重写）；CLI `publish` 实测包体积与裁剪影响；
灰度观察 → 单点强制 → 全量，回滚开关保留一版。

## 6. 已接受代价（实施中不再重议）

1. 流式变缓冲：verdict 前零更新外泄，TUI 增量 token 流变“等 verdict 后放”，延迟上升。
2. 历史双门控：工厂门控 `InMemoryChatHistoryProvider` + 自管 Session，
   被拒谁丢、变换后谁留、服务端 conversation-id 门控不住，三条写进契约。
3. 审批双层：`IApprovalResolver`（liftable deny）vs 现有 Ask 硬闸；
   默认桥内禁用 liftable，避免“批了还被拦”。
4. provider 侧工具盲区：hosted tools 不进 tool 缝，只在 `post_model_call` 投影可见。
5. 现有 Hook 测试大半重写，换真实工厂 + 假模型/假工具管线测试。

## 7. 明确不做

整体搬运 MAF 源码；新事件总线/异步队列；Hook 启动子 Agent；
被删 5 事件的任何兼容层；顺带重写 Query/Session/Goal 模块。


