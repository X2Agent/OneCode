# Harness 默认能力替换审计与重构验收清单

> **状态**：2026-09-17 源码复核修订；不是「七项全部收口」，运行时重构与契约验收尚未完成。
> **用途**：作为后续重构的临时施工清单；完成 §8 的验收与决策迁移后再删除。
> **关联**：[ADR 0007](../adr/0007-maf-integration-boundaries.md)、[记忆设计 ADR 0004](../adr/0004-memory-module-design.md)、[技能系统](../skills.md)。

## 1. 结论与证据边界

**OneCode 使用 MAF 的总体分工合理，但原文否决若干重构方向的理由不成立，不能据此认定已符合最佳实践。**

- 保留 MAF 的 agent 执行、工具调用循环、上下文协议、审批协议、历史处理与遥测；OneCode 管理产品模式、任务、长期记忆、搜索后端和权限政策。
- `Disable` 不是反模式：关闭不适用的默认能力，再通过官方扩展点装配产品能力，是有效组合方式。
- Skills 已复用 `AgentSkillsProviderBuilder`，记忆检索已复用 `TextSearchProvider`，压缩已复用 MAF provider 与大部分策略；不能统称「禁用 MAF 后自研一套」。
- **不以减少 Disable 数量为重构目标，也不以维持现状为验收目标。** 判断标准是语义适配、单一所有者、生命周期、执行顺序、可验证性与维护成本。
- 暂保留 `HarnessAgent`。没有证据证明必须改用 `ChatClientAgent`；也不能把「现有测试通过」当作禁止未来迁移的依据。

### 1.1 版本基线

- 项目版本声明：`../../src/Directory.Packages.props` 中 Core / Harness / Workflows 为 `1.21.0`；MCP 为 `1.21.0-alpha.260911.1`，Shell / Hyperlight 为 `1.21.0-preview.260911.1`。不是所有 `Microsoft.Agents.AI*` 包都为稳定版 `1.21.0`。
- 本地源码：`../../agent-framework`，提交 `c97528b13084b5f2bbecc410159ea7c1e0700f9d`，描述 `dotnet-1.21.0-87-gc97528b13`。不能把整个 checkout 等同于已发布包。
- 已对比 `dotnet-1.21.0..HEAD`：`dotnet/src/Microsoft.Agents.AI.Harness` 与 `dotnet/src/Microsoft.Agents.AI/Skills` 无差异。本次这些核心结论有版本对应证据；不向所有模块外推。
- 实施前仍须核对当前构建所用 `project.assets.json` 的实际版本；不要以旧 artifacts 目录里的资产文件或单独存在的 NuGet 缓存代替它。
- 本文仅使用 .NET 实现作依据，不用 Python API 或设计提案推断 .NET 发布 API。

### 1.2 阅读范围

本次重点为 Harness 装配、技能 source/provider、Todo、产品上下文与模式、长期记忆检索、搜索工具、压缩及提示词合成。不是对全部 MAF 后端适配器、Workflows 或所有 OneCode 安全边界的全面认证。

## 2. MAF 的设计与扩展边界

### 2.1 分层职责

| 层 | MAF 职责 | OneCode 应承担的部分 |
|---|---|---|
| `IChatClient` / builder | 模型调用与逐次调用装饰 | 模型选择、请求限制、需要在每次模型调用运行的上下文组件 |
| `ChatClientAgent` / session | agent 运行、历史与上下文 provider 协议 | 会话存储适配、产品上下文、实例生命周期 |
| `HarnessAgent` | 有默认政策的组合入口，封装 ChatClientAgent 和装饰链 | 明确选择默认能力、配置或关闭不适用项 |
| `AIContextProvider` | 贡献指令、消息、工具及可选 session 状态 | 薄的产品适配；状态与业务规则仍有唯一来源 |
| source / store / strategy | 技能来源、文件后端、压缩策略等可组合对象 | 使用现有实现，补充确实缺失的产品行为 |
| agent / function middleware | run 级与单次工具调用横切 | 权限、预算、产品恢复政策；不能与审批协议混为一谈 |

`HarnessAgent` / `HarnessAgentOptions` 为 sealed，内部默认 provider 使用 `new`；没有按 provider 类型向 DI 容器解析替换实现的机制。`AIContextProviders` 在默认 provider 后 `AddRange`，不会按类型或工具名替换、去重。

但这**不妨碍 DI 组合**：OneCode 的工厂可从 DI 获取依赖，再把实例传给 Harness 的 Options。`services` 还传到 builder 工厂/装饰器，并非只服务 AIFunction。不要从「默认 provider 不走 DI」推出「使用 DI 必须放弃 Harness」，也不要据此评价哪个框架更符合 .NET 惯例。

### 2.2 选择规则

1. 默认行为适用时直接使用，必要时配置 Options。
2. 语义匹配而后端不同，优先复用 provider + source/store/strategy。
3. 需要控制 provider 配置、缓存、生命周期或顺序时，使用官方 builder 构建 provider，再显式追加；关闭对应默认项。
4. 业务语义不同，保留产品实现，使用官方上下文/工具/中间件扩展协议接入。
5. 只有装配限制已造成可测问题且收益明确，才评估不用 Harness；不复制 agent 工具循环。

上述是决策顺序，不是「存在入口就必须迁移」的硬性排名。每项必须说明能力所有者、适用 profile、状态归属、资源释放方以及验证方式。

### 2.3 入口分类

| 入口 | 精确语义 |
|---|---|
| `ChatHistoryProvider` | 传入历史 provider；OneCode 主构建入口当前用的是 MAF `InMemoryChatHistoryProvider`，不是自研 provider |
| `CompactionStrategy` | 自定义策略；关闭 Disable 后生效，不要求两个 token 字段 |
| `FileMemoryStore` | 换存储后端，不自动改变记忆领域语义或 Harness 的 session 初始化政策 |
| `AgentSkillsSource` | 换完整技能 source 管道，可携带脚本运行器；缓存、去重、所有权须调用方处理 |
| `AgentModeProviderOptions` | 配置内置 mode provider，不能直接替换 provider |
| `HarnessInstructions` | `null` 使用默认；空白抑制默认；非空在 agent 指令前拼接 |
| `AIContextProviders` | 追加实例；与不适用的默认项配合 Disable |
| `FileAccessStore` / `BackgroundAgents` / `LoopEvaluators` | opt-in；不能算作已经禁用的默认能力 |
| `ChatClientAgentRunOptions.ChatClientFactory` | run 级 ChatClient 工厂；本身也是 Options API，不是默认 provider 替换入口 |
| `UseAIContextProviders` / function middleware | 分别是 ChatClient 上下文装饰与逐工具调用横切；不是同一个执行层 |

Harness Options 有 10 个 `Disable*` 属性，不代表 OneCode 全部置 true，也不代表这些能力只有 Disable 没有配置入口。`ApplyProductOptOuts` 是 **6 个布尔关闭 + 1 个空指令配置**，不是「7/7 框架能力全部推翻」。

## 3. 七项处置建议

| 项 | 当前实现与语义 | 重构建议 |
|---|---|---|
| Todo | 关闭默认；`TaskContextProvider` 读 `ITaskService`，Task 工具负责修改 | 保留产品任务模型与官方 context 接入；验证 scope、恢复和工具可用性，不称严格超集 |
| AgentMode | 关闭默认；宿主驱动模式，Main 注入 `ModeInstructionProvider` | 保留。不能恢复 LLM `mode_set` 形成第二套模式状态；子代理由角色/profile 控制 |
| FileMemory | 关闭会话文件 CRUD；长期记忆为 MEMORY.md + 检索 + 宿主治理 | 保留领域边界，不用 FileMemoryProvider 整换 Memdir |
| AgentSkills | 关闭 Harness 默认；官方 builder 组合文件、bundled、MCP 与 runner | 当前路线合理；Source 路线技术可行，不能以「丢 runner」禁止迁移。先验收生命周期，再按净收益决定 |
| WebSearch | 关闭 hosted tool；产品 AIFunction 走搜索提供方链 | 当前多后端政策下保留；不是 MAF .NET 普遍不能搜索 |
| Compaction | 关闭 Harness 压缩；官方 provider 在输入 ChatClient 上装配 | 保证唯一入口；评估策略所有权与顺序后决定是否迁入 Harness，不能以「迁移必然双挂」否决 |
| HarnessInstructions | 空字符串；`PromptComposer` 提前合成产品指令 | 当前集中合成可保留；传入产品 HarnessInstructions 同样合法，不是不可控拼接 |

统一 opt-out **不等于各路径都挂了全部替代能力**。AutoDream 独立构建只读工具 agent，不走 shared provider 全栈；Explore / Plan 的工具白名单不含 Task，但共享上下文仍可能包含任务提示。必须区分「有产品替代」「有意关闭」「只读观察」三种情况。

## 4. 逐项证据与真正的边界

### 4.1 Todo 与模式

MAF `TodoProvider` 使用 session StateBag 与每会话锁，工具为 `todos_add/complete/remove/get_remaining/get_all`。OneCode 有任务依赖、owner、日志等更多领域信息，但这不证明批处理、删除、会话序列化、完成理由等所有语义等价。

`TaskContextProvider` 通过 ambient conversationId/buildRunId，以 `exactScope: true` 查询；当前筛出 Pending/InProgress，并把更新指导放入 system 消息。应验证真实 Task 写入后同 scope 可见、跨 scope 隔离，以及只读 profile 的提示不要求调用其没有的 Task 工具。provider 放在 agent 层，不能默认声称每次内部模型调用都刷新任务快照。

模式由 `MainModeContextProviderBuilder` 与宿主政策决定；关闭 LLM mode 工具是避免状态分叉，不是只因 Harness 没有替换属性。模式提示不能代替工具权限闸。

### 4.2 长期记忆与 FileMemory

`HarnessAgent.BuildContextProviders` 默认配置 `{cwd}/agent-file-memory`，新 session 初始化随机 WorkingFolder。它是会话文件记忆/产物工具，不是跨新 session 自动共享的产品长期记忆。

- 默认路径对 OneCode 不合适，但 `FileMemoryStore` 可以改变物理后端，不能说落盘位置永远不可定制。
- Harness 未暴露 WorkingFolder 初始化委托；独立 `FileMemoryProvider` 有初始化扩展。恢复原 session 与新建 session 不是一回事，不能说 MAF 完全不能持久化。
- 即使改后端，其文件 CRUD/index 语义也不会自动变成 MEMORY.md 的检索、命中反馈、作用域与 AutoDream 治理，所以保留 Memdir 是合理产品决策。

`MemorySearchProviderFactory` 已用 `TextSearchProvider`，配置按需 function calling、自定义搜索名和结果格式。这是复用框架而非重复造轮子。业务检索、排名与写回仍由 `IMemoryService` 承担，框架不会自动替产品完成。

**已观察到的具体待修项**：`SearchAsync` 的外层 `catch (Exception)` 会接住取消异常；即使内部 RecordHitsAsync 重抛取消，外层仍将其转为普通结果。产品日志还记录原始 query，并把 `ex.Message` 放进工具结果；MAF 自身的遥测脱敏不会覆盖这些代码。修复时先增加取消传播及敏感内容不泄漏的反证测试，不在本次文档审计中修改实现。

### 4.3 Skills：原否决理由撤销

**runner 属于文件 source/script，不必由 Harness 直接接收。** 证据：

- `AgentSkillsProviderBuilder.UseFileSkills` 将 runner 交给 `AgentFileSkillsSource`。
- `AgentFileSkillScript.RunAsync` 调用自身保存的 runner。
- `AgentSkillsProvider(AgentSkillsSource, ...)` 使用调用方的 source；不会因为 Harness 没有 runner 参数而删除 source 中的运行能力。
- 文件、内联、MCP source 可以用框架现有聚合 source 组合；不需要重新实现 MCP 技能协议。

**但不要只换一个赋值就宣布等价**。builder 的行为包含聚合 → 默认缓存 → 可选过滤 → 首项优先去重，并构建 `ownsSource: true` 的 provider。Harness 传入自定义 source 使用默认 `ownsSource: false`，不会替调用方补齐这些装饰。迁移必须明确 source 清理方、每 run 重建、MCP 客户端所有权、重复技能优先级与顺序。

当前 `SkillProviderFactory` 每 run 构建、使用官方 builder，是合理且较省维护的组合。保持它不需要额外理由；只有 Source 方案能减少总体复杂度或满足新需求时才迁移。不能为少一个 Disable 手写一套 builder 内部政策。

脚本执行必须继续经过实际工具权限/审批链。不要由「框架提供了 skill tool」推断沙箱、超时、取消清理自动成立；也不要未经调用链证明就断言存在审批绕过。验收应覆盖只读 profile、取消后的子进程回收及输出预算。

### 4.4 搜索并非单一能力

- `HostedWebSearchTool` 是交给模型服务端执行的工具；Harness 默认加入，不先检测后端支持。当前多后端通用配置下关闭它合理。
- OneCode `WebSearchTool` 是本地 AIFunction 调用 Tavily / DuckDuckGo 提供方链，包含产品错误处理、缓存和域过滤；与 hosted 搜索不是同一执行边界。
- `TextSearchProvider` 用于注入自定义检索，当前承接记忆；`ToolSearch` 是工具发现，Grep / Glob / 符号检索是代码库搜索。不能仅因都叫搜索就合并。

是否有独立 profile 使用 hosted 搜索，应由支持的后端、费用与隐私政策决定，不应仅为恢复默认而自动启用。

### 4.5 Compaction：默认关闭不等于最优装配

自定义 `CompactionStrategy` 在 `DisableCompaction = false` 时直接生效。**不要求同时设置** `MaxContextWindowTokens` / `MaxOutputTokens`；只有没有自定义策略时才用两字段构造默认策略。

OneCode 主构建入口未提供这些 Harness 字段，因此关闭开关目前是防御性的。`CompactionPipelineBuilder` 的链为自定义重复调用清理 + MAF 工具结果折叠 + 摘要 + 截断，不是「全部策略均 MAF 原生」。

Harness 同样通过 `UseAIContextProviders` 安装压缩；自建默认 history 时还可能安装 reducer。OneCode 主路径显式传入 MAF InMemory history，所以不走该 fallback reducer 分支。

当前请求方向的关键顺序（从外向内，省略产品 run/function 横切）：

`approval binding → bypass → FICC → message injection → per-service history persistence → chat OTel → 产品 ChatClientContextProviders → supplied client`

Harness 自有压缩若启用，位于 history persistence 与 chat OTel 之间。因此二者是相同机制、同在内部模型调用范围，但不是完全相同的装饰位置。迁移时移除旧 provider，验证遥测、状态 key、恢复、摘要客户端不递归和工具消息配对；**双挂是错误迁移的结果，不是迁移的必然结果**。

### 4.6 指令合成

`HarnessInstructions = ""` 是 API 支持的抑制默认行为；MAF 使用可预测的前置拼接。OneCode 的 `PromptComposer` 同时服务 Main / fork / Team，集中合成有合理性；迁入 Harness 也可以通过同一上层工厂保持一致。

两条路线都应验证共享片段恰好一次、角色顺序、模式提示、缺失 prompt 的失败策略与工具提示一致性。不要把 AutoDream 的专用提示误写成必然经过 PromptComposer。

## 5. 保留 Harness 与显式组装 ChatClientAgent

当前建议保留 Harness：仍在复用重要执行与协议行为，不只是为了获得三个便利属性。DI 不是迁出理由。

如果未来因装配限制立项迁出，必须验证这些行为，而不是照旧文三行迁移表实施：

| 保留项 | 验收要求 |
|---|---|
| approval response binding / bypass | 审批结果正确绑定；无需审批的函数正常处理 |
| FICC 与迭代上限 | 工具循环只一套；迭代轮数和工具调用总数分别验证，不能视为相同预算 |
| message injection | 长工具循环中的消息注入仍生效 |
| per-service history persistence | 每次服务调用的历史回调、失败/取消时历史语义，以及 session 持久化与恢复 |
| history 相关选项 | `RequirePerServiceCallChatHistoryPersistence` 等配置与实际装饰链一致；冲突政策明确 |
| agent + ChatClient OTel | 两级遥测各自存在且不重复；不是仅保留一层 |
| ToolApprovalAgent | 按 profile 恰好一套；`DisableToolAutoApproval` 关闭的是此装饰器，不是 OneCode Permission |
| context 与资源 | provider 次序、状态 key、source/provider/client 所有权清楚；不误释放共享客户端 |
| 可选 Loop | 只有有意启用时才加，不能把内部 FICC 与外层目标环混为一谈 |

`ChatOptions.MaximumIterationsPerRequest` 不是本文已核实的迁移 API；实际应在 `FunctionInvokingChatClient` 配置迭代上限。禁止照猜测 API 写施工代码。

## 6. 按优先级实施

以下是待办，不代表已经修改或验证完成。按最小变更逐项交付，不要同时更换能力入口和业务语义。

| 优先级 | 工作 | 完成证据 |
|---|---|---|
| P0 | 修复记忆检索取消传播与日志/错误信息边界 | 从工厂产生的工具路径验证取消；普通 I/O 错误仍可控降级；日志与结果不暴露敏感原文 |
| P1 | 建立实际装配契约，而非仅断言 Options | Main/Worker/Team/Explore/Plan 的有效工具、provider 与权限符合 profile；AutoDream 单独覆盖 |
| P1 | 核实每 run Skills 构建的 source/provider 释放方 | 同 run 缓存、跨 run 新文件/MCP 变化、重复技能优先级、取消后资源释放；共享 MCP 客户端不被误释放 |
| P1 | 任务上下文与工具能力对齐 | 真实 Task 写入→上下文、恢复与 scope 隔离；只读路径不提示不存在的修改工具 |
| P1 | 固定压缩唯一所有者与执行契约 | 在一个 run 内多轮工具/模型调用触发压缩；状态恢复、工具结果配对、历史回调和计数通过 |
| P2 | 在上述契约保护下选择 Skills Source / Harness 策略入口 | 比较改前改后同组行为测试与总体复杂度；允许有理由地保留当前路线 |
| P2 | 验证指令与各类搜索边界 | 每条路径提示与实际工具一致；共享指令一次；hosted 与产品搜索不意外双挂 |

## 7. 验证范围与尚缺证据

2026-09-17 本次运行筛选：`OneCodeHarnessDefaultsTests`、`AgentPipelineBuilderTests`、`TaskContextProviderTests`、`SkillProviderFactoryTests`、`MemorySearchProviderFactoryTests`、`DocFactConsistencyTests`，**49 通过、0 失败、0 跳过**。测试运行构建了项目依赖；未完成全量测试或外部模型服务的 live 验证。

`OneCodeHarnessDefaultsTests` 主要断言 Options 字段，不能证明生产 agent 实际工具不重复，也不能证明 caller 永远不传 opt-in 配置。工厂返回非 null 不证明脚本执行或 MCP 正常；直接测搜索委托不等于测工厂产生的 MAF 工具。

后续测试优先走真实写入/实际 provider 工具路径，使用可控假 ChatClient，不依赖外网模型：

- Task 工具写入 → 同 scope provider 可见 → 恢复后可见；另一 scope 不可见。
- MAF 技能工具 → 文件 script runner；默认审批/只读 profile 保持，取消与资源清理有可观察结果。
- 工厂产生 `search_memories` → 真实记忆写入端 → 命中反馈落盘 → 重新加载验证；取消不转为普通结果。
- 一次 run 中多次模型调用，观察压缩执行次数、历史内容与恢复，而不只检查属性值。
- Main / fork / Team / AutoDream 有意差异明确；主路径不携带 Todo/mode/file_memory/hosted 的非预期默认工具。

这些是验收要求，不能把「尚未补测」直接描述成已复现的运行时 bug。

## 8. 删除本文的门槛

- [ ] P0 已修复，P1 的关键契约有真实行为测试；失败项有明确处理，不以更改期望值掩盖。
- [ ] 每项入口有最终决定：迁移，或记录保留理由；不要求所有 Disable 都消失。
- [ ] 已决定的实现只有一个所有者；被替代的旧装配删除，无双轨兼容层。
- [ ] 本文中具体代码问题及资源问题均有结果，仍未知的点不得标为已收口。
- [ ] `dotnet build` 与全量 `dotnet test` 通过，外部服务未验证的限制如实记录。
- [ ] 稳定决策更新到 ADR 0007；记忆/技能公开行为同步 ADR 0004、memory-overview、background-services、skills 及相关注释。
- [ ] 将必要的版本/行为证据迁入长期文档和测试；全文移除本文链接后再删文件。

## 9. 源码证据索引

路径相对本文件，源码类名/成员是定位锚点；工作区根目录为 `e:\OneCode`。

| 文件 | 证据 |
|---|---|
| [HarnessAgent](../../agent-framework/dotnet/src/Microsoft.Agents.AI.Harness/HarnessAgent.cs) | BuildAgent / BuildInnerAgent / BuildContextProviders：顺序、默认创建、追加、指令、压缩 |
| [HarnessAgentOptions](../../agent-framework/dotnet/src/Microsoft.Agents.AI.Harness/HarnessAgentOptions.cs) | 选项类型、默认与自定义策略的关系 |
| [Skills builder](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Skills/AgentSkillsProviderBuilder.cs) | UseFileSkills、Build：runner、缓存、去重、ownsSource |
| [Skills provider](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Skills/AgentSkillsProvider.cs) | 自定义 source 构造、工具与生命周期 |
| [File script](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Skills/File/AgentFileSkillScript.cs) | RunAsync 委派 runner |
| [产品 opt-out](../../src/OneCode.Infrastructure/Agent/OneCodeHarnessDefaults.cs) | 6 bool + 空指令，路径专属配置不在此修改 |
| [Pipeline builder](../../src/OneCode.Infrastructure/Agent/AgentPipelineBuilder.cs) | 输入 client 先包装、Harness 后包装、history 与审批所有权 |
| [压缩构建](../../src/OneCode.Infrastructure/Agent/CompactionPipelineBuilder.cs) | 自定义 L0 + 原生 L1–L3，状态命名 |
| [共享上下文](../../src/OneCode.App/Services/Agent/SharedContextProviderBuilder.cs) | profile 装配、技能 per-run、任务上下文 |
| [任务上下文](../../src/OneCode.App/Services/Context/TaskContextProvider.cs) | exactScope 与只读上下文 |
| [模式上下文](../../src/OneCode.App/Services/Agent/MainModeContextProviderBuilder.cs) | 宿主模式与 Main 专属注入 |
| [技能工厂](../../src/OneCode.App/Services/Skills/SkillProviderFactory.cs) | 官方 builder + 文件/内联/MCP |
| [记忆检索工厂](../../src/OneCode.App/Services/Memory/MemorySearchProviderFactory.cs) | 官方 TextSearchProvider、委托、取消与日志 |
| [产品搜索](../../src/OneCode.App/Tools/WebSearchTool.cs) | AIFunction 后端链 |
| [PromptComposer](../../src/OneCode.App/Services/PromptComposer.cs) | Main / role 共享合成 |
| [AutoDream](../../src/OneCode.App/Services/AutoDream/AutoDreamService.cs) | 独立 Harness 构建，不等于 shared provider 全栈 |
