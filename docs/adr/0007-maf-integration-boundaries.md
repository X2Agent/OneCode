# MAF 集成边界与禁止清单

**状态**: Accepted
**日期**: 2026-09-16
**关联**: [Permission vs ToolApproval vs Filter](./0001-permission-vs-toolapproval-vs-filter.md)、
[Declarative 工作流评估](./0002-declarative-workflow-assessment.md)、
[子代理派工边界：不迁移 MAF BackgroundAgents](./0011-background-agents-delegation-boundary.md)、
[记忆模块架构设计](./0004-memory-module-design.md)、
[技能系统](../skills.md)、[Team 编排模式选型](../team-modes.md)、[记忆总览](../memory-overview.md)、
[压缩阈值](../compact-thresholds.md)、[后台服务](../background-services.md)

## 语境

OneCode 站在 Microsoft Agent Framework（MAF，NuGet `Microsoft.Agents.AI*`）之上。2026-09-14 至 09-15 完成了一轮
「MAF 重叠盘点 → Harness opt-out → W1–W6 重构」，结论原本散落在 `docs/plan/` 的规划文档与若干代码注释中
（其中「MAF 重叠分析与重构计划」已随本 ADR 建立而删除，仅存于 git 历史）。

盘点过程中反复出现同一类误判：**看到产品实现与 MAF 官方能力语义相近，就提议"用官方替换掉"或"当死代码删掉"**。
这类误判已实际发生四次，均在二次核对时被推翻：

| 早期误判 | 核对结论 |
|---|---|
| Harness 压缩「双挂」 | 主路径未设 token 参数，本就不启用 → 降为【防御性关闭】 |
| ParallelDag 可用 MAF Concurrent「等价替换」 | ParallelDag 是单成员 Sequential 分支 → 【保留】 |
| MCP WebSocket / InProcess / Registry「冷代码可删」 | 均有实际调用方 → 【保留】，判断作废 |
| ToolApproval「现存双挂」 | 已有 `harnessOwnsToolApproval` 挡住 → 【禁止回归】，非现存 bug |

误判的根源是：**这些边界只存在于一份尚无决策记录地位的计划文档里**。本 ADR 把仍有效的边界判定
提升为 ADR，使规划文档可以归档、而边界不至于随之流失。

## 决策

### 1. Harness 默认能力的处置与扩展边界

**保留当前产品语义与 Harness 组合路线**：不迁移到自建 provider 链，也不整体关闭 Harness 默认能力。下表为实际归属。

`OneCodeHarnessDefaults.ApplyProductOptOuts` 设置 3 个 Disable 布尔值（AgentMode / AgentSkills / WebSearch），**不再**触碰 Todo、FileMemory、Compaction 与 HarnessInstructions。按 profile 决定的能力由 `PipelineProfileBehavior` 表达：`AgentCapability.Todo`、`FileMemory` 仅在允许的 profile 上开启，其余路径通过 `DisableTodoProvider` / `DisableFileMemory` 显式关闭。

| 项 | 当前所有者与决定 | 边界 |
|---|---|---|
| Todo | Harness `TodoProvider`（`todos_*`），按 profile 启用 | 宿主执行记录（Worker 状态、后台任务、Build 依赖）仍归 `ITaskService` / `BuildTaskLinker`；`TaskTool` 只保留 get/list/stop/output，不再提供普通清单的 create/update |
| AgentMode | 宿主模式 + Main `ModeInstructionProvider` | 不恢复 LLM `mode_set` 形成双状态；子代理由角色/profile 约束 |
| FileMemory | Harness `FileMemoryProvider`（`file_memory_*`），store 根绑定项目 | 仅 Main 启用；Worker/Team/Explore/Plan 关闭（只读 Agent 不应有写面，并发成员不应共享可写目录）。长期 MEMORY.md + `TextSearchProvider` 仍是治理后的知识，两者分目录共存 |
| AgentSkills | 官方 `AgentSkillsProviderBuilder` + 文件/内联/MCP/runner | 双入口共用 `SkillDiscovery` 的发现规则；runner 有超时/输出上限/进程树清理；宿主通过 `AgentContextProviderLease` 释放每 run 的 provider |
| WebSearch | 产品 AIFunction 搜索后端链（Tavily + DuckDuckGo 两个 `IWebSearchProvider`） | 关闭默认 hosted tool；两个提供方的适配都在 Infrastructure |
| Compaction | Harness 策略入口（`HarnessAgentOptions.CompactionStrategy`） | 产品提供策略（比例、formatter、摘要守卫、prompt），Harness 挂载唯一 provider；不再双挂 |
| HarnessInstructions | MAF 合成：通用片段 → `HarnessInstructions`，主体 → `ChatOptions.Instructions` | 产品保留加载、覆盖、渲染与缺失 fail-fast；`PromptComposer` 不再拼接 |

**扩展模型**：Harness 内置 provider 用 `new`，`AIContextProviders` 是追加实例列表，没有按 provider 类型从 DI 自动替换的入口。`HarnessAgent` / Options 为 sealed，但 OneCode 可在 DI 工厂内构建实例后传入 Options，**不需要因此放弃 Harness**。`services` 会流经 builder 工厂/装饰器，不只用于 AIFunction。

**选择原则**：默认适用则复用/配置；后端不同可换 source/store/strategy；provider 的配置、生命周期或顺序需要产品控制时，官方 builder + Disable + 追加同样合理。业务语义不同则以官方扩展协议接入产品实现。禁止重复同一能力，不禁止有证据、有净收益的入口迁移。

**待办驱动循环的取舍（2026-09-21 记录）**：MAF 官方执行姿势是用 `TodoCompletionLoopEvaluator` + `LoopAgent`（`Modes=["execute"]`、`MaxIterations` 有界）把 agent 反复跑到待办清零。OneCode 交互 BUILD 路径**有意不接**：用户旁观流式输出，自动续跑会抢在人对工具审批的即时裁决之前（且 pending tool approval 无人解析时 `LoopAgent` 会直接停止，见 `AgentPipelineAssembly` 注释）。自治需求由 GOAL 模式承载——子目标走 `LoopAgent` + `DelegateLoopEvaluator` 语义 judge。若 headless / YOLO 路径将来需要「持续运行到待办清零」，在那一层接入 `TodoCompletionLoopEvaluator`，**不要**改交互路径；`OneCodeHarnessDefaultsTests` 对 `LoopEvaluators` 为 null 的守卫随之保留。

**Skills 的真实迁移约束**：runner 由 `AgentFileSkillsSource` 创建的 script 持有，Harness 不传 runner 参数不构成障碍。builder 默认聚合、缓存、去重，并构建 `ownsSource: true` 的 provider；Harness 自定义 source 构造默认不接管 source 所有权，也不补这些装饰。不能只减少一个 Disable 而遗漏释放方或复制一套 builder 政策。

**压缩的真实迁移约束**：自定义 `CompactionStrategy` 在 Disable 为 false 时直接生效，不要求两个 token 字段。OneCode 主入口显式提供 `TranscriptChatHistoryProvider`（桥接会话转录），所以不走 Harness 自建 history/reducer 分支。实施后压缩只有一个所有者：策略经 `HarnessAgentOptions.CompactionStrategy` 交给 Harness，产品侧不再挂 provider。

**保持 Harness 的理由**：仍复用 approval binding/bypass、FICC、消息注入、逐次服务调用历史处理、agent 与 ChatClient 两级遥测及按 profile 启用的 ToolApproval。不是「7/7 默认全部推翻后仅剩三项」。迁出应单独论证收益与全部路径的等价行为；不要复制 agent 执行循环。

**可用扩展点**：Options 注入、`ChatClientAgentRunOptions.ChatClientFactory`（也是 Options API）、ChatClient `UseAIContextProviders`、agent/function middleware。显式有序装配是本项目选择；不能从 MAF 提供这些入口推导它否定 DI。

**测试边界**：Options 守卫只锁定配置，不证明生产路径实际无双挂；实际装配契约已由行为测试补齐——provider 工具集与 profile 一致、Session 保存/恢复、scope 隔离与资源释放、记忆取消传播与产品日志边界（见文末引用的测试清单）。

### 2. ParallelDag 不是 MAF Concurrent

`docs/team-modes.md` 的 ParallelDag 矩阵描述是**产品语义**（「静态扇出/扇入（编译期确定）」），而**实现是按 `AssigneeRole`
选单成员 + `SequentialWorkflowBuilder`**——产品语义是「并行视角分支」，不是真并行扇出（该文自身也以实现说明段澄清并反链本 ADR）。

**禁止**：把 ParallelDag「等价替换」为 MAF Concurrent 工作流。名称保留；多成员误配保持「单成员断言 + 日志」。

### 3. MCP 运输与 Registry 保留

`WebSocketClientTransport`、`InProcessMcpTransportPair`、`OfficialMcpRegistryClient` / `BuiltInMcpServers`
均有实际调用方（`McpConnectionManager` 分支 / `McpCommand` / DI / 测试）。
**「MCP 冷代码无引用可删」的判断已作废。**

Agent 工具路径已站在 MAF 上（`ListAgentToolsWithTasksAsync` + `UseMcpSkills`）；保留下来的厚壳是产品连接/配置层。
**禁止**为瘦身把 `McpConnectionManager` 拆成纯转发壳，也**禁止**自研 JSON-RPC 或自研 MCP→AIFunction 桥。

`RenamedAIFunction` 因 MAF `TaskAwareMcpClientAIFunction` 未暴露 `WithName` 而**短期保留**；待上游提供改名 API 后再删。

### 4. ForkedAgentRunner ≠ MAF BackgroundAgents

| 维度 | `ForkedAgentRunner` | `BackgroundAgentsProvider` |
|---|---|---|
| 挂载点 | 产品 `IAgentRunner`（`WorkerAgentService` → AgentTool / ParallelAgents / DAG） | Harness `AIContextProvider`，给**父 Agent** 注入工具 |
| 调度方 | 代码 / `AgentTool` **确定性**调用 | **父 LLM** 调 `background_agents_start_task` |
| 子 Agent 形态 | `SubAgentPipelineFactory` + `PipelineProfile`（Worker/Explore/Plan）、工具白名单、只读裁剪 | 预注册的命名 `AIAgent` 字典 |

**决策**：不迁移。迁到 BackgroundAgents 等于把子 Agent 启动权交给主对话 LLM，破坏确定性编排与 `TaskService` 生命周期，
且 `BackgroundAgents` 只认已构建好的 `AIAgent` 列表，不会自动带上 Explore 只读等产品闸。

**禁止**：把 `HarnessAgentOptions.BackgroundAgents` 挂到默认 Full 路径（会与 AgentTool 形成「派工」双挂）。

**补强（见 [子代理派工边界](./0011-background-agents-delegation-boundary.md)）**：完整评估在此，本条只留索引。
四条硬证据——① 能力门禁被绕过（provider 工具不经 `ToolCatalog`/`ToolCapabilitySet` 直注，
普通主路径 `IsToolAllowed` 未设故无运行时名字闸）；② 审批不转发致写代理编辑静默失效
（`ToolApprovalAgent` 未自动批准时工具不执行，任务仍标 completed）；
③ DAG/fan-in/上游注入无对应物；④ `TaskService` 持久化 + per-task stop 与 provider 的 `lost` + 无取消语义倒置。
另含接口级结论：provider 公共面仅 构造/`StateKeys`/`GetIncompleteTasks`/`ReleaseSessionAsync`，
无编程式起任务入口，故也不能用作 `AgentTool`/`ParallelAgentsTool` 的底层实现。

### 5. 承接既有 ADR 的边界

本次盘点未改变这三条，重申以杜绝回归：

- **Permission ≠ ToolApproval**（权限与工具审批边界）：Permission 是唯一 Allow/Deny 安全门，ToolApproval 是 MAF 协议层。
  **禁止**把 Permission 并进 ToolApproval。
- **Memdir ≠ FileMemoryProvider**（内存模块设计）：领域模型不同。**禁止**用 `FileMemoryProvider` 整换 Memdir，
  **禁止**重新引入 KV Store / 第二套 `IMemoryStore`。
- **禁止引入 `Microsoft.Agents.AI.Workflows.Declarative`**（声明式工作流评估）。

### 5.1 版本敏感行为与升级复查

**项目引用的 NuGet 为 `1.22.0`，本地 `agent-framework/` 是该发布再前进 6 个提交**
（`git describe --tags` = `dotnet-1.22.0-6-g669c8b95e`）。二者基线已对齐，但 checkout 仍非逐字等同发布包——
版本敏感断言以「**1.22.0 包行为 + 发布 tag 源码**」双证为准，不得仅凭 HEAD 源码为发布包行为作证。

**包与目录不等同**：`Microsoft.Agents.AI.Harness` 包只有 `HarnessAgent.cs` / `HarnessAgentOptions.cs` /
`ChatClientHarnessExtensions.cs` / `FeatureIndex.cs`；被使用的默认 provider（Todo / AgentMode / FileMemory /
FileAccess / BackgroundAgents / Loop / ToolApproval）实际位于 **`Microsoft.Agents.AI` 包的 `Harness/**`**。

**仍无差异的路径**（`dotnet-1.22.0..HEAD`，可标【已核对】；`1.21.0..1.22.0` 段亦无改动）：`dotnet/src/Microsoft.Agents.AI.Harness`、
`dotnet/src/Microsoft.Agents.AI/Harness/{Loop,BackgroundAgents,Todo,FileMemory,FileAccess,FileStore}/**`、
`dotnet/src/Microsoft.Agents.AI/Compaction/**`。
装配顺序、默认 provider 形态、Todo/FileMemory/FileAccess 工具面、Loop 循环语义、BackgroundAgents 接口面与压缩策略的结论
有版本对应证据；不向其他模块外推。`Harness/**` 整体与 `Skills/**` **不能**标【已核对】——其中 `ToolApproval/**` 与
`AgentMode/**` 在该区间有改动，见下。

**同一 range 内确有改动（结论以 `dotnet-1.22.0` 为准——pinned 包与本地 checkout 同处该基线；
`dotnet-1.21.0` tag 是升级前旧行为，仅作 diff 对照）**：

- `dotnet/src/Microsoft.Agents.AI/ChatClient/` 的审批协议实现被 `[BREAKING]` 改写：
  `ApprovalResponseBindingChatClient` 共 139 行变动（+112/-27）重写、`ApprovalNotRequiredFunctionBypassingChatClient`
  抽出共享判定、新增 `ApprovalRequirement.cs`（同目录 `AIAgentChatClient.cs`/`ChatClientExtensions.cs` 亦有改动，不影响下述结论）。**1.21.0 的「已知请求」有两个来源**：① 框架在上一轮浮出时记录过的请求
  （覆盖「只回显响应、不回放原请求」的调用方）；② 当前消息历史里已经出现的审批请求（覆盖被回放的历史，以及宿主内部
  生成的审批——如 AG-UI 的混合服务端/客户端工具调用）。**HEAD 只认 ①**：历史里出现的请求不再是授权依据，否则调用方
  可以同时伪造请求与其批准来授权任意工具调用；调用方无法在服务端记录审批时，正确做法是整体关闭 approval-response
  binding，而不是依赖它回放的历史。OneCode 会持久化 session 并由 `ApprovalBroker` 处理审批，**恢复/回放路径正落在被
  改写的语义上**。
- `dotnet/src/Microsoft.Agents.AI/Harness/ToolApproval/ToolApprovalAgent.cs` 被重写（`#8432`，+232/-121，共 353 行变动），
  「哪些请求算已浮出」这一绑定权威随之变化：1.21.0 在把未批准请求交还调用方时把**整批**未批准请求都登记为 surfaced，
  而达到自动批准上限的那一轮**完全不登记**；HEAD 只登记**真正交给调用方的那些**（排队项在出队呈现时才登记），
  且上限轮返回前也会登记（否则调用方对它的「总是允许」无法绑定）。OneCode 的 `AllowAlways` 经 Team 桥接当前只提供
  单次批准（§5.1 下文），若日后放开 standing rule，必须先按该语义核对绑定路径。
- `dotnet/src/Microsoft.Agents.AI/Skills/**` 有改动（4 文件 +107）：`SKILL.md` frontmatter 校验变严——已知字段名必须
  全小写、重复字段被拒；`metadata` 映射内的键大小写不敏感且首个值胜出；custom source 默认缓存并新增缓存隔离键说明。
  **1.21.0 没有这层校验**，因此技能发现与 frontmatter 解析的结论不能再标【已核对】。
- `dotnet/src/Microsoft.Agents.AI/Harness/AgentMode/**` 有改动（`#8458`）：新增
  `HarnessAgentOptions.AgentModeProviderOptions`（含 `DisableModeSetTool` / `DisableModeGetTool`），把「关掉 LLM
  模式工具」变成官方开关——与 §1 的「AgentMode 归宿主」同向，升级后可评估用它替代自建 opt-out。
- `OpenTelemetryAgent.cs` / `AgentExtensions.cs` 亦有变化，**agent 级遥测结论同样版本敏感**。

**升级复查必做**（升级 MAF 版本时逐条执行；当前 pinned 为 `dotnet-1.22.0`）：

1. 重跑「暂停 → 恢复 → 审批响应绑定」与「只回显响应、不回放请求」两组用例——注意 1.22.0（`#8432`）同时改写了
   `ToolApprovalAgent` 与 `ApprovalResponseBindingChatClient` 两侧的"已浮出"登记语义，用例必须覆盖上限轮与排队出队两条路径。
2. 重新核对 `ApprovalNotRequiredFunctionBypassingChatClient` 与 `OpenTelemetryAgent` 语义。
3. 核对 `GuardedSummarizationCompactionStrategy` 依赖的 MAF 字面量：该守卫按字符串匹配 `SummarizationCompactionStrategy` 的 `[Summary unavailable]` 占位符与 `[Summary]` 前缀（对应 `GuardedSummarizationCompactionStrategy.cs` 的 `UnavailablePlaceholder` / `SummaryPrefix`）。MAF 若更改任一字面量或提交语义（空摘要/超长摘要是否提交），守卫会静默失效，须同步更新。
4. 核对 `project.assets.json` 的实际版本（不要用旧 artifacts 或单独存在的 NuGet 缓存代替）。
5. 评估是否采用 `AgentModeProviderOptions.DisableModeSetTool/DisableModeGetTool`（1.22 新增的官方开关）替代自建 opt-out；
   若采用，保持「AgentMode 归宿主、LLM `mode_set` 永不恢复」的决定不变。
6. 核对 `Skills/**` 的 frontmatter 校验（小写字段名、拒绝重复）与缓存隔离键语义是否影响 `SkillDiscovery`，并回归
   `SkillDiscoveryRuleTests`。

**框架行为（跨版本需复核）**：`FunctionInvokingChatClient` 的审批是**全有或全无**——只要同一响应中存在一个
`ApprovalRequiredAIFunction`，该响应里**所有** `FunctionCallContent` 都会变成 `ToolApprovalRequestContent`，
包括原本不需要审批的工具，再由 bypass/binding 装饰器区分。技能工具默认需要审批，因此会与普通工具调用同批
出现在一个响应里，这是审批链必须覆盖的真实场景（`MainAgentRunner` 的混合内容拆分即为此而设）。

**Team 工作流审批桥（R4 落地形态）**：Team 成员保留 MAF `ToolApprovalAgent`（`PipelineProfileBehavior`
不减 `ToolApproval` 能力）；Ask 决策产生的审批请求在工作流中作为外部请求浮出（`RequestInfoEvent`），
由 `TeamWorkflowRunner.BridgeToolApprovalAsync` 经 `ApprovalBroker.ForTeam` 推送
`OrchestrationEvent.ApprovalRequest`，用户决策经 `SendResponseAsync` 送回同一工作流。注意：
`AIAgentHostOptions.InterceptUserInputRequests`（消息模式）在单成员 Sequential 链上会把请求发往
**不存在的下游 executor 而被静默丢弃**（`SendMessageAsync` 无出边即无投递），因此 Team 一律走端口模式。
「以后允许」（`AllowAlways`）经桥接当前**只提供单次批准**：原生 standing rule 依赖
`AlwaysApproveToolApprovalResponseContent` 包装（非 `ToolApprovalResponseContent` 子类），能否无损通过
工作流端口响应类型校验未经验证，按「不得静默降级」原则显式降为单次并记录日志；待单独验证后放开。
**端到端覆盖**：`TeamToolApprovalBridgeTests` 不 mock 上述五层（`FunctionInvokingChatClient` →
`ToolApprovalAgent` → `ChatClientAgent` → `AIAgentHostExecutor` → `AIAgentUnservicedRequestsCollector`），
只脚本化最外层 `IChatClient` 与产品审批通道（`eventSink` 完成 `ResponseSource`），断言审批请求浮出的
工具名/参数、批准后工具真的执行且 turn 恢复、拒绝后不执行且同样恢复、无审批通道时 fail-closed 且不挂起。
**待定政策**：「每次询问」强制确认无法仅靠自动批准规则实现（standing rules 先于
自定义规则短路），产品尚未裁决是否需要，当前 TUI 直接提供「总是允许」。

**保留 Harness 的理由与迁出验收**：仍在复用 approval binding/bypass、FICC、消息注入、逐次服务调用历史处理、
agent 与 ChatClient 两级遥测及按 profile 启用的 ToolApproval。若未来立项迁出，必须逐项验收：
审批结果绑定与 bypass、FICC 与迭代上限（`HarnessAgentOptions.MaximumIterationsPerRequest` 转发给
`FunctionInvokingChatClient.MaximumIterationsPerRequest`；`PipelineOptions.MaxToolCalls` 同时驱动迭代上限与
本仓工具计数——**数值同源、语义不同**，不是同一预算）、长工具循环的消息注入、per-service history 持久化、
history 相关选项与实际装饰链一致、两级 OTel 各自存在且不重复、`ToolApprovalAgent` 按 profile 恰好一套、
provider 次序与状态 key、source/provider/client 所有权不误释放共享客户端。
**注意**：不存在 `ChatOptions.MaximumIterationsPerRequest`。

### 6. 禁止自造清单

- 自写 `while (RunStreaming)` 外层目标环（Goal 已用 MAF `LoopAgent` + `DelegateLoopEvaluator`；`/loop` 运行时有界循环同样走 `LoopAgent` —— 见 [代理循环边界](./0010-agent-loop-boundaries.md)）
- 新的 `ApprovalHandler` / 平行 Ask 引擎（历史踩坑处）
- 自研 chat history 循环（交给 `ChatHistoryProvider`；Main 路径经 `TranscriptChatHistoryProvider` 读会话转录，宿主不注入历史消息、也不清空框架历史）
- 再实现「自家 Harness 运行时」
- 在 Harness 已拥有 compaction 时再从产品侧挂一个 `CompactionProvider`（现由 `HarnessAgentOptions.CompactionStrategy` 单一入口保证，**禁止回归**）
- 在 Harness 已拥有 approval 时再挂一套 `UseToolApproval`（现有 `harnessOwnsToolApproval` 已挡住，**禁止回归**）
- 启用实验性 `FileAccess` / `BackgroundAgents`（除非单独立项）
- **手写提供商线协议键**：向 `ChatOptions.AdditionalProperties` / `ChatMessage.AdditionalProperties` 塞
  提供商私有键（`cache_control`、`thinking`、`reasoning_effort` …）来「实现」缓存或推理参数（见 §7）

**`FileAccess` 例外条款**：2026-09-18 评估后仍不接管通用文件工具（多根工作区、ignore 单一清单、远程读取三项硬性不满足；Grep 能力净损失；provider 自动批准规则会成为第二套政策源）。若未来立项，须先满足：多根 store 适配（或显式放弃 `/add-dir`）、ignore 政策注入 store、远程读取扩展或显式排除，并同批迁移 `Read`/`Write`/`Edit`/`Delete`/`LS`/`Glob`/`Grep` 的全部引用方（`AutoDreamService.AllowedTools`、`TeamRequirementService.RequiredTools`、`TeamCapabilityProfile`、`ToolResultSummarizer`、提示词、权限元数据）。可复用资产：`SplitLines`/`ScanContent` 行号原语（作对照基准）、`file_access_replace_lines` 的过期行检测（作为独立新工具候选）。

**StateMachineMiddleware 例外**：它不是 MAF 的重复实现，而是产品侧「连续失败 → Recovering/Blocked → 仅允许
AskUser*」恢复闸。**禁止**用主 Harness `LoopEvaluators` 替代。
Profile 现状：Full 开（含 3-strike），Worker / TeamMember / Explore / Plan 全关。

**`MainAgentRunner` 审批桥例外**：主路径的 `while (RunStreaming)` 审批桥**不落入**上述第一条禁令
——它桥接 MAF 的 `ToolApprovalRequestContent`，不是自写目标环。1.22 源码证据：`ToolApprovalAgent` 把多余的未批准
请求排队、只 surface 一个（`agent-framework/dotnet/src/Microsoft.Agents.AI/Harness/ToolApproval/ToolApprovalAgent.cs:211-216, 369-381`），
工具执行只发生在宿主携响应重入之后（`InjectCollectedResponses`，同文件 `:565-582`）；官方示例驱动同一循环
（`agent-framework/dotnet/samples/02-agents/Agents/Agent_Step01_UsingFunctionToolsWithApprovals/Program.cs:38-64`）。不存在可替代的
框架 API（`AutoApprovalRules` 是策略而非人在环桥接）。Team 路径 `TeamWorkflowRunner.BridgeToolApprovalAsync`
（`RequestPort` 形态）同理。

### 7. 提供商请求整形：只走公开扩展点与 MEAI 类型面

**决定**：提供商特有的请求整形（提示词缓存断点、推理/思考参数、采样参数）只能通过两条路径之一实现：

1. **SDK 公开扩展点**——如 Anthropic SDK 的 `AIContentCacheExtensions.WithCacheControl`（把缓存断点挂在
   `AIContent` 上，由 SDK 在序列化时写入正确键名与结构）；
2. **MEAI 类型化字段**——如 `ChatOptions.Reasoning`（`ReasoningOptions.Effort`）、`ChatOptions.MaxOutputTokens`，
   由各提供商适配器各自翻译成自己的线协议字段。

**禁止**在 `AdditionalProperties` 里手写提供商线协议键名。`ProviderAwareDecorator` 曾用
`ChatMessage.AdditionalProperties["cache_control"]` 实现 Anthropic 缓存、用
`ChatOptions.AdditionalProperties["thinking"]` 实现思考参数——两者都是**死代码**：Anthropic SDK 12.40.0 只认
`AIContent.AdditionalProperties["anthropic:cache_control"]`（且键名与结构由 `WithCacheControl` 独占），
适配器**从不读取** `options.AdditionalProperties["thinking"]`。结果是产品以为开启的
prompt caching 与扩展思考从未生效，而单元测试因为断言的是产品自己写的键而全绿。

**判定规则**（新增/修改任何提供商整形代码时）：

- 先查 SDK 是否已有公开扩展方法或类型化属性；有则**只能**用它。
- 没有时，先确认适配器**实际读取**的键名与结构（以二进制/源码为准，不以命名习惯推断），并在**线上契约测试**
  （捕获 `DelegatingHandler` 的请求体）中断言最终 JSON，而不是断言产品中间对象。
- 产品侧不得缓存「适配器默认值」的知识（如 Anthropic 的 `_defaultMaxTokens`）：一旦复制，适配器升级后
  两边会静默发散。
- 断点必须 **clone-on-write**：MAF 历史层跨工具轮 / 跨 turn 复用同一批 `ChatMessage` / `AIContent` 实例，
  若原地写 `cache_control`，上一轮的「末条消息」断点会滞留在中间历史里随轮数累积，超过 Anthropic 的
  4 断点上限后每个请求都被 400 拒绝。`ProviderAwareDecorator` 每次调用在消息副本上重建断点
  （system 消息 1h + 末条 5m；常规单 system 请求恰为 2 个，断点数随 system 消息数增长，仍远低于 4 上限），
  历史原始实例永不被写入——由 `Anthropic_CacheBreakpoints_DoNotAccumulateAcrossCallsOnSharedMessageInstances` 回归测试锁死。

**配套证据**：`ProviderWireContractTests`（Anthropic 缓存断点 1h/5m、`system` 字段、`max_tokens > thinking budget`；
OpenAI `reasoning_effort`；Ollama 顶层 `think` + `options.num_ctx`）与 `ThinkingOptionsContractTests`
（预算 → `ReasoningEffort` 映射与 `BuildChatOptions` 输出）。

**已知有损处**：`EffortThinking.ToReasoningEffort` 是粗粒度映射（`<=0 → None`、`<=1024 → Low`、`<=8192 → Medium`、
`<=16384 → High`、其余 `ExtraHigh`），Anthropic 适配器再映射回 1024/8192/16384/32768 预算。
产品侧精确预算（如 12000）不再逐字下发。这是刻意取舍：用 `RawRepresentationFactory` 复刻精确
`budget_tokens` 需要复制适配器的 `_defaultMaxTokens` 逻辑，并有把 `max_tokens` 压到 0 的风险。
`MaxOutputTokens` 与 thinking 预算也因此解耦（显式指定时不再自动上调），避免适配器把预算夹到 `MaxTokens - 1`。

### 8. 自研 vs MAF 能力裁定（保留清单）

对「MAF 有对应能力但 OneCode 保留自研 / 不替换」的裁定与依据。已收敛项以代码为准，此处只记**不收敛的理由**，防止未来重复评估。

| 项 | 裁定 | 依据 |
|---|---|---|
| Hook 引擎 | 保留自研（终判） | 见 [Hook 模块设计](./0005-hook-module-design.md)：与 `HarnessAgent` 装配双重硬冲突 + 依赖仅 alpha |
| 双预算（`PipelineOptions.MaxToolCalls` 计数闸 vs `MaximumIterationsPerRequest` 轮数） | 同源保留（有意） | 工具调用数 ≥ 迭代轮数，以 `MaxToolCalls` 作迭代上限是保守上界，不会提前截断；数值同源，避免新增调参面 |
| WebSearch 自研提供方链 | 保留 | 官方仅服务端托管 `HostedWebSearchTool`（`agent-framework/dotnet/src/Microsoft.Agents.AI.Harness/HarnessAgent.cs:315`），无域过滤 / 缓存 / 多提供方故障转移 |
| Skills frontmatter 自研解析（`SkillFrontmatterParser`） | 保留 | 官方解析链路 `private`、`AgentFileSkill` 构造器 `internal`（`agent-framework/dotnet/src/Microsoft.Agents.AI/Skills/File/AgentFileSkillsSource.cs`），无公开解析入口；产品专有字段官方无对应 |
| AgentMode 自建 opt-out（`ModeInstructionProvider`） | 保留 | 官方 `AgentModeProvider`（1.22 `AgentModeProviderOptions`）是 agent 内部模式：暴露 `mode_set` / `mode_get` 由模型自切换；OneCode 四模式由宿主控制且运行期可变，语义不同域 |
| durable run registry（fencing / lease / CAS / 代次） | 保留 | `Microsoft.Agents.AI.Hosting` 不做单写者协调（`HostedWorkflowState.cs` remarks：an application that needs a single writer per session owns that coordination）；二者是不同层（ASP.NET 托管层 vs durable run 协调层） |
| `PlanWorkflow` 自研状态机 | 保留 | 业务聚合（计划审批 / 步骤证据）非 MAF Workflow 能力域 |
| lease 回调（Build / Goal / Team 三处）、恢复策略（4 套）、验证证据与质量门（3 套） | 保留 | 聚合类型、状态校验集合与终止语义各不同域；泛型统一需引入多参数委托与约束，复杂度高于三份直白代码（抽象泄漏） |
| JSON run store（Build / Goal / Team 三份 TrySave / Claim / CAS） | **未收敛** | 三者记录模型与状态机不同，收敛需先统一记录抽象；独立立项 |

## 后果

- 原本散落于 `docs/plan/` 计划文档的边界与禁令由本 ADR 承接；该计划文档（P0/P1 与 W1–W6 全部落地）已归档删除。
- 后续评审先对照产品语义与实际扩展入口，不凭名称相近判定可以等价替换；也不凭本 ADR 的历史裁决永久禁止复评。
- 新增能力使用显式有序装配，禁止重复能力和隐式反射扫描；DI 依赖解析与显式注册并不冲突。
- 提供商整形（§7）不再由产品持有 wire key 知识：产品只表达意图（`ChatOptions.Reasoning` / 内容块缓存断点），
  翻译责任完全在适配器。代价是产品侧精确 budget 变为档位（有损），换来的是 SDK 升级时不会静默失效。
- MAF 升级复查：包与源码版本对应、默认装饰顺序、工具/审批语义、provider 缓存与释放、状态 key、Options 变化及所有 profile 行为。是否取消 sealed 不是唯一触发条件。

## 引用

- 机制说明与现状对照：[MAF C# 接入指引](../maf/integration-guide.md)（本 ADR 承载决策，该文承载「框架怎么运转 / 怎么接 / 接得对不对」）
- 落地代码：`src/OneCode.Infrastructure/Agent/OneCodeHarnessDefaults.cs`（§1 产品 opt-out 所在，类注释指向本 ADR）、
  `src/OneCode.Infrastructure/Agent/AgentPipelineBuilder.cs`（审批标记、压缩策略、profile 门控的单一装配汇合处）、
  `src/OneCode.Infrastructure/Agent/PipelineProfile.cs`（能力集与减法清单）、
  `src/OneCode.Infrastructure/Agent/ToolApprovalMarker.cs`、`ToolApprovalMarkingContextProvider.cs`、
  `HarnessProviderTools.cs`、`AutoApprovalRulesFactory.cs`、
  `src/OneCode.Infrastructure/Agent/CompactionPipelineBuilder.cs`、`GuardedSummarizationCompactionStrategy.cs`、
  `src/OneCode.App/Services/Agent/AgentContextPipeline.cs`、`AgentContextProviderLease.cs`、
  `src/OneCode.App/Services/Coordinator/TeamWorkflowRunner.cs`（Team 工作流审批桥 §5.1）、
  `src/OneCode.Infrastructure/Ai/ProviderAwareDecorator.cs`、`src/OneCode.Infrastructure/Ai/ChatClientFactory.cs`、
  `src/OneCode.App/Services/Agent/MainAgentRunner.cs`（`BuildChatOptions`，§7 类型化推理参数出口）、
  `src/OneCode.Core/Domain/EffortThinking.cs`（`ToReasoningEffort`，§7 有损映射）、
  `src/OneCode.App/Services/Skills/SkillDiscovery.cs`、`SkillProviderFactory.cs`
- 测试：`src/OneCode.Tests/OneCodeHarnessDefaultsTests.cs`、`ToolApprovalMarkerTests.cs`、
  `AutoApprovalRulesFactoryTests.cs`、`TeamToolApprovalBridgeTests.cs`、`ProviderWireContractTests.cs`、
  `ThinkingOptionsContractTests.cs`、`HarnessInstructionsCompositionTests.cs`、
  `FileMemoryProfilePolicyTests.cs`、`AgentContextProviderLeaseTests.cs`、
  `GuardedSummarizationCompactionStrategyTests.cs`、`CompactionBudgetValidatorTests.cs`、
  `MafSessionInvalidatorTests.cs`、`AgentSessionPersistenceEpochSnapshotTests.cs`、
  `MemoryRecallEvaluationTests.cs`、`TextTokenizerTests.cs`、`MemoryBudgetTests.cs`、
  `DuckDuckGoSearchProviderTests.cs`、`SkillDiscoveryRuleTests.cs`