# MAF 集成边界与禁止清单

**状态**: Accepted
**日期**: 2026-09-16；§1 修订于 2026-09-17
**关联**: [Permission vs ToolApproval vs Filter](./0001-permission-vs-toolapproval-vs-filter.md)、
[Declarative 工作流评估](./0002-declarative-workflow-assessment.md)、
[记忆模块架构设计](./0004-memory-module-design.md)、
[Harness 默认能力替换核实](../plan/harness-defaults-replacement-audit.md)、
[技能系统](../skills.md)、[Team 编排模式选型](../team-modes.md)

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

**2026-09-17 修订**：保留当前产品语义与 Harness 组合路线；撤销「Skills Source 会丢 runner」「策略迁移必然双挂」「DI 必须放弃 Harness」及三项迁移永久禁止的旧论据。实施与验收清单见关联审计文档，尚未代表运行时重构完成。

`OneCodeHarnessDefaults.ApplyProductOptOuts` 设置 6 个 Disable 布尔值及空 `HarnessInstructions`。统一 opt-out 不表示所有路径都装配相同替代品：AutoDream 是独立只读工具路径；Main、Worker、Team、Explore、Plan 按 profile 装配。

| 项 | 当前所有者与决定 | 边界 |
|---|---|---|
| Todo | `TaskContextProvider` + Task 工具，保留产品任务模型 | 依赖/owner/日志丰富不等于 MAF Todo 所有语义的严格超集；须验证作用域、恢复和只读工具提示 |
| AgentMode | 宿主模式 + Main `ModeInstructionProvider` | 不恢复 LLM `mode_set` 形成双状态；子代理由角色/profile 约束 |
| FileMemory | MEMORY.md + `TextSearchProvider` 检索 + 宿主治理 | 不以会话文件 CRUD 替代长期记忆；默认后端可换，领域语义仍不同 |
| AgentSkills | 官方 `AgentSkillsProviderBuilder` + 文件/内联/MCP/runner | 当前组合合理；`AgentSkillsSource` 可携带 runner，迁移需保留缓存、去重、顺序及资源所有权 |
| WebSearch | 产品 AIFunction 搜索后端链 | 当前多后端政策下关闭默认 hosted tool；不是框架普遍不支持搜索 |
| Compaction | ChatClient 侧 `CompactionPipelineBuilder` | 当前防御性关闭 Harness。迁移可行但须移除旧挂载、验证顺序与状态，不得双挂 |
| HarnessInstructions | `PromptComposer` 统一合成，空值抑制 MAF 默认 | 合法配置；改为传入产品指令同样可行，按可维护性和多路径一致性决定 |

**扩展模型**：Harness 内置 provider 用 `new`，`AIContextProviders` 是追加实例列表，没有按 provider 类型从 DI 自动替换的入口。`HarnessAgent` / Options 为 sealed，但 OneCode 可在 DI 工厂内构建实例后传入 Options，**不需要因此放弃 Harness**。`services` 会流经 builder 工厂/装饰器，不只用于 AIFunction。

**选择原则**：默认适用则复用/配置；后端不同可换 source/store/strategy；provider 的配置、生命周期或顺序需要产品控制时，官方 builder + Disable + 追加同样合理。业务语义不同则以官方扩展协议接入产品实现。禁止重复同一能力，不禁止有证据、有净收益的入口迁移。

**Skills 的真实迁移约束**：runner 由 `AgentFileSkillsSource` 创建的 script 持有，Harness 不传 runner 参数不构成障碍。builder 默认聚合、缓存、去重，并构建 `ownsSource: true` 的 provider；Harness 自定义 source 构造默认不接管 source 所有权，也不补这些装饰。不能只减少一个 Disable 而遗漏释放方或复制一套 builder 政策。

**压缩的真实迁移约束**：自定义 `CompactionStrategy` 在 Disable 为 false 时直接生效，不要求两个 token 字段。OneCode 主入口提供的是 MAF `InMemoryChatHistoryProvider`，所以不走 Harness 自建 history/reducer 分支。当前产品压缩位于 Harness 的 chat OTel 内侧，Harness 自有压缩位于其外侧；相同 `UseAIContextProviders` 机制不代表装饰位置完全相同。

**保持 Harness 的理由**：仍复用 approval binding/bypass、FICC、消息注入、逐次服务调用历史处理、agent 与 ChatClient 两级遥测及按 profile 启用的 ToolApproval。不是「7/7 默认全部推翻后仅剩三项」。迁出应单独论证收益与全部路径的等价行为；不要复制 agent 执行循环。

**可用扩展点**：Options 注入、`ChatClientAgentRunOptions.ChatClientFactory`（也是 Options API）、ChatClient `UseAIContextProviders`、agent/function middleware。显式有序装配是本项目选择；不能从 MAF 提供这些入口推导它否定 DI。

**测试边界**：现有 Options 守卫只锁定配置，不证明生产路径实际无双挂。后续须补实际 provider 工具、状态恢复、scope 隔离与资源释放契约。记忆取消传播与产品日志边界等具体待办见审计文档。

### 2. ParallelDag 不是 MAF Concurrent

`docs/team-modes.md` 将 ParallelDag 描述为「静态扇出/扇入（编译期确定）」，但**实现是按 `AssigneeRole` 选单成员 +
`SequentialWorkflowBuilder`**——产品语义是「并行视角分支」，不是真并行扇出。

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
| 调度方 | 代码 / `AgentTool` **确定性**调用 | **父 LLM** 调 `BackgroundAgents_StartTask` |
| 子 Agent 形态 | `SubAgentPipelineFactory` + `PipelineProfile`（Worker/Explore/Plan）、工具白名单、只读裁剪 | 预注册的命名 `AIAgent` 字典 |

**决策**：不迁移。迁到 BackgroundAgents 等于把子 Agent 启动权交给主对话 LLM，破坏确定性编排与 `TaskService` 生命周期，
且 `BackgroundAgents` 只认已构建好的 `AIAgent` 列表，不会自动带上 Explore 只读等产品闸。

**禁止**：把 `HarnessAgentOptions.BackgroundAgents` 挂到默认 Full 路径（会与 AgentTool 形成「派工」双挂）。

### 5. 承接既有 ADR 的边界

本次盘点未改变这三条，重申以杜绝回归：

- **Permission ≠ ToolApproval**（ADR 0001）：Permission 是唯一 Allow/Deny 安全门，ToolApproval 是 MAF 协议层。
  **禁止**把 Permission 并进 ToolApproval。
- **Memdir ≠ FileMemoryProvider**（ADR 0004）：领域模型不同。**禁止**用 `FileMemoryProvider` 整换 Memdir，
  **禁止**重新引入 KV Store / 第二套 `IMemoryStore`。
- **禁止引入 `Microsoft.Agents.AI.Workflows.Declarative`**（ADR 0002）。

### 6. 禁止自造清单

- 自写 `while (RunStreaming)` 外层目标环（Goal 已用 MAF `LoopAgent` + `DelegateLoopEvaluator`）
- 新的 `ApprovalHandler` / 平行 Ask 引擎（历史踩坑处）
- 自研 chat history 循环（交给 `ChatHistoryProvider`）
- 在 Harness 已拥有 approval 时再挂一套 `UseToolApproval`（现有 `harnessOwnsToolApproval` 已挡住，**禁止回归**）
- 再实现「自家 Harness 运行时」
- 启用实验性 `FileAccess` / `BackgroundAgents`（除非单独立项）

**StateMachineMiddleware 例外**：它不是 MAF 的重复实现，而是产品侧「连续失败 → Recovering/Blocked → 仅允许
AskUser*」恢复闸。**禁止**用主 Harness `LoopEvaluators` 替代。
Profile 现状：Full 开（含 3-strike），Worker / TeamMember / Explore / Plan 全关。

## 后果

- 原本散落于 `docs/plan/` 计划文档的边界与禁令由本 ADR 承接；该计划文档（P0/P1 与 W1–W6 全部落地）已归档删除。
- 后续评审先对照产品语义与实际扩展入口，不凭名称相近判定可以等价替换；也不凭本 ADR 的历史裁决永久禁止复评。
- 新增能力使用显式有序装配，禁止重复能力和隐式反射扫描；DI 依赖解析与显式注册并不冲突。
- MAF 升级复查：包与源码版本对应、默认装饰顺序、工具/审批语义、provider 缓存与释放、状态 key、Options 变化及所有 profile 行为。是否取消 sealed 不是唯一触发条件。
- 只有可执行契约和相关文档都更新后，才可删除临时审计；不能仅因 Options 测试通过就宣布收口。

## 引用

- 落地代码：`src/OneCode.Infrastructure/Agent/OneCodeHarnessDefaults.cs`（§1 七项 opt-out 所在，类注释指向本 ADR）、
  `src/OneCode.Infrastructure/Agent/AgentPipelineBuilder.cs`、
  `src/OneCode.App/Services/Agent/AgentContextPipeline.cs`、
  `src/OneCode.App/Services/Agent/ForkedAgentRunner.cs`、
  `src/OneCode.App/Services/Skills/SkillProviderFactory.cs`
- 测试：`src/OneCode.Tests/OneCodeHarnessDefaultsTests.cs`（§1 七项守卫）
- 过程记录：`docs/plan/harness-defaults-replacement-audit.md`（源码证据、待办优先级与删除验收门槛）