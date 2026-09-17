# Harness 默认能力替换审计与重构验收清单

> **状态**：2026-09-17 静态复核修订（含版本基线、Todo、Memory、HarnessInstructions、Skills 与 Compaction 专项计划）。§4.2 已统一为「启用 Harness 内置 FileMemory 管理会话工作记忆 → OneCode 整理长期知识」，补充现状/目标、风险分级与 M1–M5 施工验收；§4.5 保留压缩专项及 L0 规则复算。上述重构均尚未实施，不是「七项全部收口」；此次只更新文档，没有新增构建或 C# 测试通过记录。
> **用途**：作为后续重构的临时施工清单；完成 §8 的验收与决策迁移后再删除。
> **关联**：[ADR 0007](../adr/0007-maf-integration-boundaries.md)、[记忆设计 ADR 0004](../adr/0004-memory-module-design.md)、[技能系统](../skills.md)。

## 1. 结论与证据边界

**OneCode 使用 MAF 的总体分工合理，但原文否决若干重构方向的理由不成立，不能据此认定已符合最佳实践。**

- 保留 MAF 的 agent 执行、工具调用循环、上下文协议、审批协议、历史处理与遥测；普通 Agent TODO 改用 MAF `TodoProvider`（待实施，见 §4.1）。OneCode 管理产品模式、宿主执行任务、长期记忆、搜索后端和权限政策。
- `Disable` 不是反模式：关闭不适用的默认能力，再通过官方扩展点装配产品能力，是有效组合方式。
- Skills 已复用 `AgentSkillsProviderBuilder`，记忆检索已复用 `TextSearchProvider`，压缩已复用 MAF provider 与大部分策略；不能统称「禁用 MAF 后自研一套」。
- 会话工作记忆改为按 profile 启用 **Harness 内置 FileMemoryProvider**（待实施，§4.2）：配置原生 store，不自研或重复装配 provider。对话历史仍由 Session/history 管理；OneCode 从工作产物提炼长期知识与偏好，继续用 TextSearchProvider 检索。不得以「FileMemory 不能整换长期库」推导继续全局关闭它。
- **不以减少 Disable 数量为重构目标，也不以维持现状为验收目标。** 判断标准是语义适配、单一所有者、生命周期、执行顺序、可验证性与维护成本。
- 暂保留 `HarnessAgent`。没有证据证明必须改用 `ChatClientAgent`；也不能把「现有测试通过」当作禁止未来迁移的依据。
- 指令合成改用 MAF `HarnessInstructions` + `ChatOptions.Instructions`（待实施，见 §4.6）；产品提示词内容、文件加载/覆盖与模板渲染仍由 OneCode 负责，不把使用原生机制等同于使用默认文案。
- **证据分层**：装配顺序、默认 provider 形态、Skills builder/source/provider、审批开关这些结论在 `dotnet-1.21.0..HEAD` 内可对应到未变化的路径；「审批绑定/绕过」与「agent 级遥测」则随版本改变（§1.1、§5），不得混用 checkout HEAD 与 1.21.0 的语义。

### 1.1 版本基线

- 项目版本声明：`../../src/Directory.Packages.props` 中 Core / Harness / Workflows 为 `1.21.0`；MCP 为 `1.21.0-alpha.260911.1`，Shell / Hyperlight 为 `1.21.0-preview.260911.1`。不是所有 `Microsoft.Agents.AI*` 包都为稳定版 `1.21.0`。
- 项目引用的**发布版本是 NuGet `1.21.0`**，本地源码是另一提交：`../../agent-framework` 当前 HEAD 为 `ab8299beb0bdd244debdd8a8d4f7d4aedd933f28`，描述 `dotnet-1.21.0-94-gab8299beb`（上一轮复核为 `c97528b13084b5f2bbecc410159ea7c1e0700f9d` / `dotnet-1.21.0-87-gc97528b13`，checkout 已前进 7 个提交）。不能把整个 checkout 等同于已发布包。
- **包与目录不等同**：`Microsoft.Agents.AI.Harness` 包只有 `HarnessAgent.cs` / `HarnessAgentOptions.cs` / `ChatClientHarnessExtensions.cs` / `FeatureIndex.cs`；被审计的默认 provider（Todo / AgentMode / FileMemory / FileAccess / BackgroundAgents / Loop / ToolApproval）实际位于 **`Microsoft.Agents.AI` 包的 `Harness/**`**。引用证据时必须按目录区分，不能都记成 `.Harness` 包。
- 已对比 `dotnet-1.21.0..HEAD`，下列**三处路径在新 HEAD 复核仍无差异**：`dotnet/src/Microsoft.Agents.AI.Harness`、`dotnet/src/Microsoft.Agents.AI/Harness/**`、`dotnet/src/Microsoft.Agents.AI/Skills`。装配顺序、默认 provider 形态、Skills builder/source/provider 与审批开关这些结论有版本对应证据；不向所有模块外推。
- **同一 range 内确有改动，且不在上述三处**：`dotnet/src/Microsoft.Agents.AI/ChatClient/` 的审批协议实现已被改写（`ApprovalResponseBindingChatClient` ≈139 行重写、`ApprovalNotRequiredFunctionBypassingChatClient` 抽出共享判定、新增 `ApprovalRequirement.cs`，提交标题含 `[BREAKING]`），`OpenTelemetryAgent.cs` / `AgentExtensions.cs` 亦有变化。因此**「审批绑定/绕过」与「agent 级遥测」两类结论只能以 `dotnet-1.21.0` tag 为准**，不得用 HEAD 源码为该版本行为作证；升级复查项见 §5。
- 实施前仍须核对当前构建所用 `project.assets.json` 的实际版本；不要以旧 artifacts 目录里的资产文件或单独存在的 NuGet 缓存代替它。
- 本文仅使用 .NET 实现作依据，不用 Python API 或设计提案推断 .NET 发布 API。

### 1.2 阅读范围

本次重点为 Harness 装配、技能 source/provider、Todo、产品上下文与模式、长期记忆检索、搜索工具、压缩及提示词合成。不是对全部 MAF 后端适配器、Workflows 或所有 OneCode 安全边界的全面认证。

### 1.3 简洁优先与旧实现清理（所有批次强制遵守）

**接入一项原生能力，就收掉对应的重复实现；保留产品差异，不保留历史包袱。** 本节落实 [src/AGENTS.md 重构总则](../../src/AGENTS.md#0-重构总则)，以减少重复职责和维护成本为目标，不以增加框架包装层、删除类的数量或减少 Disable 数量为目标。

1. **替换与删除同步完成**：原生能力接管某项职责后，同一改动中删除被替代的自定义实现和旧调用路径；不能只关闭旧实现、注释代码或留作备用。允许分批实施，但每批交付后已迁移的职责只有一套运行路径。
2. **不留双轨与冗余兼容层**：不保留同一职责的新旧双写、兼容双读、旧入口转发、废弃重载或仅为旧实现服务的配置开关。旧数据须明确迁移、保留为执行记录或丢弃的政策，不因此长期维持两套运行时机制；不同职责的数据源及必要故障回退不属于此处的兼容双轨。
3. **清理完整引用链**：同步检查无用接口、模型、DI 注册、能力枚举、配置、依赖包、测试辅助代码、注释和文档。共享组件只有在确认没有剩余使用者后才删除，公共行为变化须全文搜索并同步引用。
4. **只保留必要的产品适配**：产品政策和框架尚未覆盖的行为保留薄适配；不重新实现 MAF 已有的 provider、工具循环、缓存、消息分组或存储协议，不增加没有实际职责的转发层。针对原生缺陷的适配必须由失败行为测试界定最小范围。
5. **按职责删除，不机械删除整个模块**：普通 Todo 被替换不等于删除后台执行与 Build 任务管理；提示词合成被替换不等于删除文件加载和模板渲染；引入工作记忆不等于删除长期记忆治理。必要保留项注明具体职责和调用方，不以“以后可能有用”为理由。
6. **以行为测试保护精简**：删除或改写仅服务旧实现的测试，但保留必要的产品行为契约，并让测试经过新的真实调用路径。不能通过删除失败测试或只修改 Options 期望值完成验收；每批交付须满足构建与测试门禁。

各专项的清理边界如下；这是待实施清单，不表示相关代码已删除：

| 模块 | 删除或精简范围 | 必须保留的职责 |
|---|---|---|
| Todo（§4.1） | 自定义普通清单工具职责、重复清单注入、相关注册与能力引用；清理不再使用的普通 Todo 存储路径 | 后台停止/输出、Worker 执行记录、Build 依赖与恢复；仍被这些调用方使用的 Task 服务和存储 |
| Prompt（§4.6） | `Compose()` 手工拼接及调用方预拼接路径，不留兼容合成入口 | 产品提示词加载、覆盖、模板渲染、缺失失败及独立 Agent 提示政策 |
| Compaction（§4.5） | L0 参数判重策略及引用；修复后淘汰的 cleanup；若迁入 Harness，再删除旧 provider 装配 | 原生策略组合、必要的产品压缩编排、历史原子边界和失败状态保护 |
| Skills（§4.3） | 统一规则后被替代的重复发现/解析逻辑；若采用受控执行设施，删除被替代的旧 runner 实现 | 官方 builder/provider、用户调用筛选与参数渲染、必要执行适配和完整使用期管理 |
| Memory（§4.2） | 被替代的文件实现、重复读写路径及无用包装；不新增自定义会话文件 CRUD/index | 长期知识模型、来源核验、作用域、人工保护、必要事件回退；文件实现迁层不等于删除持久化职责 |
| Session（§4.2 M2 / §4.5 B3） | 被定向失效替代的历史全量失效路径、重复状态处理及旧装配残留 | 历史正确性、非历史状态保留、恢复和提交控制；清空/新建等有意重置的语义 |

每批改动说明列出“删除 / 保留 / 新增”的实现及理由，核对新增适配是否确有职责、旧实现是否已退出生产路径；§8 将清理结果作为验收门槛，不把接线完成等同于重构完成。

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
| `AgentSkillsProviderOptions` | 三个 `Disable*Approval`（load_skill / read_skill_resource / run_skill_script，**默认均 false = 需要审批**）、`IncludeDetailedErrors`、`SkillsInstructionPrompt`；不是 source 替换入口 |
| `AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule` / `AllToolsAutoApprovalRule` | MAF 提供的自动批准规则，**按工具名**匹配；全批准规则会顺带放行 `run_skill_script` |
| `TextSearchProviderOptions` | `SearchTime` / `FunctionToolName` / `ContextFormatter` / `RecentMessageMemoryLimit`，以及日志脱敏开关 `EnableSensitiveTelemetryData` + `Redactor`（默认 `ReplacingRedactor("<redacted>")`，只作用于 provider 自身 logger） |
| `AgentFileSkillScriptRunner` | runner 由 source 持有并传给 script；不是 Harness Options 的参数，不能以「Harness 不收 runner」判定迁移丢能力 |

Harness Options 有 10 个 `Disable*` 属性，不代表 OneCode 全部置 true，也不代表这些能力只有 Disable 没有配置入口。`ApplyProductOptOuts` 是 **6 个布尔关闭 + 1 个空指令配置**，不是「7/7 框架能力全部推翻」。

## 3. 七项处置建议

| 项 | 当前实现与语义 | 重构建议 |
|---|---|---|
| Todo | 关闭默认；`TaskContextProvider` 与 Task 工具混合承担清单和执行记录 | 普通 TODO 改用 MAF `TodoProvider`；移除重复清单，暂保留 Worker/后台/Build 执行管理和已有依赖逻辑（§4.1） |
| AgentMode | 关闭默认；宿主驱动模式，Main 注入 `ModeInstructionProvider` | 保留。不能恢复 LLM `mode_set` 形成第二套模式状态；子代理由角色/profile 控制 |
| FileMemory | 全局关闭；仅有长期 MEMORY.md + 检索 + 宿主治理 | 按 profile 启用 Harness 内置实例管理会话工作记忆，配置原生 store；OneCode 整理产物为长期知识，不替代原始历史（§4.2） |
| AgentSkills | 关闭 Harness 默认；官方 builder 组合文件、bundled、MCP 与 runner | 保留原生 provider/builder 薄装配；重构双入口技能规则、脚本执行边界与资源释放，不为减少 Disable 改写 source 管道（§4.3） |
| WebSearch | 关闭 hosted tool；产品 AIFunction 走搜索提供方链 | 当前多后端政策下保留；不是 MAF .NET 普遍不能搜索 |
| Compaction | 关闭 Harness 压缩；官方 provider 在输入 ChatClient 上装配 | 保证唯一入口；先按 §4.5 批次一修复 L0/手动压缩边界与摘要契约，批次二收敛预算与状态失效，最后在 P2 评估是否迁入 Harness 策略入口，不以「迁移必然双挂」或减少 Disable 否决 |
| HarnessInstructions | 空字符串；`PromptComposer` 提前合成产品指令 | 改用 MAF 合成：通用产品片段 → HarnessInstructions，角色/主模板正文 → ChatOptions.Instructions；保留加载、覆盖与渲染，删除重复拼接（§4.6） |

统一 opt-out **不等于各路径都挂了全部替代能力**。AutoDream 独立构建只读工具 agent，不走 shared provider 全栈；Explore / Plan 的工具白名单不含 `Task`，而共享上下文仍会注入 Task 提示（**已确认，见 §4.1**）。必须区分「有产品替代」「有意关闭」「只读观察」三种情况。

## 4. 逐项证据与真正的边界

### 4.1 Todo：普通清单复用 MAF，宿主执行管理另行保留

**决定（待实施）**：普通 Agent TODO 的工具、状态和清单上下文改用 MAF `TodoProvider`；不保留自定义普通 TODO，不将两套清单双向同步。现有后台执行、Worker、Build 使用的任务管理暂时保留，不因替换 TODO 删除整个 Task 域。下文区分源码现状与目标，不能读作已经完成迁移。

#### 4.1.1 能力与证据修正

- MAF `TodoProvider` 使用 `ProviderSessionState<TodoState>`、session StateBag 与每会话锁；提供 `todos_add` / `todos_complete` / `todos_remove` / `todos_get_remaining` / `todos_get_all`，支持批量添加、完成和删除，也自动注入清单。`TodoProviderOptions` 可配置指令、抑制清单消息或自定义格式；没有依赖图、认领调度或可替换存储的配置入口。不能把 OneCode 自动注入列表当成独有能力。
- Todo 的隔离单位是 **AgentSession，不是 AIAgent 对象**。主 Agent 和子 Agent 使用独立 Session 才能获得独立清单；共用 Session 不会自动按 Agent 身份隔离。需要逐条核对实际 Session 创建、复用和丢弃路径。
- **跨进程恢复不是自定义 Todo 的理由**：现有 `AgentSessionStore` 将序列化 Session 存入 `Conversation.Metadata["mafSession"]`，由会话保存流程落盘，再通过 `DeserializeSessionAsync` 恢复。原生 Todo 可随 Session 保存，不需另建 Todo 存储；仅恢复聊天消息则不够。`MafSessionInvalidator` 当前删除整个 `mafSession`，迁移须区分压缩保留 Todo、清空/新建会话重置 Todo 的政策，不能只打开开关。
- **TEAM/Goal 耦合不是自定义 Todo 的充分理由**：`GoalSubGoalLoop` 没有直接消费 `ITaskService`，它使用硬验证与语义评审；`WorkerAgentService` 记录子 Agent 调用的状态和输出，是宿主执行记录，不是 Worker 私有 Todo。`BuildTaskLinker` 确有 BuildPlan 与 TaskItem 的映射和恢复逻辑，但现有耦合只说明迁移边界，不能证明自定义 Todo 必须保留。
- OneCode 的 owner、输出日志、取消令牌、失败/取消状态主要服务宿主执行；更丰富的模型不等于原生 Todo 的严格超集。标题/描述更新、进行中展示等实际差异也须评估，但不以字段数量决定保留自定义实现。

#### 4.1.2 依赖图有价值，但不是序号或普通 Todo

序号/ID 不能表达“两个任务可并行，第三个必须等待二者完成”的偏序关系；拓扑排序给出合法顺序，也不等于自动完成并发调度。短线性清单可由 Agent 按描述执行；机器调度有依赖的多 Agent 工作需要显式依赖与执行门禁。

当前实现的实际强度：

- `TaskItem` 有 `Blocks` / `BlockedBy`；`TaskContextProvider` 只展示未完成依赖。其 scope 是 conversationId/buildRunId，没有按 Agent 身份过滤，不能称为 Agent 私有 Todo。
- `TaskTool` 的 create/update **不接受依赖参数**；update 调用 `TaskService.UpdateTask`，后者不校验依赖。因此“LLM 已能维护完整依赖图”不成立。
- `TaskService.ProjectTaskStatus(..., requireCompletedDependencies: true)` 会拒绝前置任务未完成时的 InProgress/Completed 状态投影；`BuildTaskLinker` 有调用。此证据证明局部投影门禁，不证明完整的并发认领、派发、去重和验收调度已经实现。

**处置**：保留已有 Build 依赖关系及校验，不用序号替代；本轮不向通用 `UpdateTask` 强加依赖守卫，也不为普通 Todo 新建 DAG。多 Agent 执行时，宿主依据执行结果与验收推进依赖计划，各 Agent 内部仍可使用独立的 MAF Todo；Todo 打勾不能替代宿主验收。“任意普通 Todo 可设置依赖并自动派发给多个 Agent”属于额外共享任务调度需求，不宣称本轮或原生 Todo 已支持。

#### 4.1.3 明确改造清单（按顺序实施）

| 当前实现 | 目标与边界 |
|---|---|
| 统一关闭 `DisableTodoProvider` | 按实际 profile 明确哪些 Agent 需要普通 Todo，并启用原生 provider；不无差别开启 AutoDream 等路径，不重复追加 provider |
| `TaskContextProvider` + `TaskContext` 装配 | 移除重复的普通任务清单注入、注册和失效能力引用；由原生 Todo 注入私有清单。若执行状态仍需进入上下文，仅呈现宿主执行信息，不再要求 LLM 把执行记录当 Todo 打勾 |
| `Task` 承担普通 create/update/get/list | 普通 Todo 统一走 `todos_*`。梳理同一工具承载的后台查询、停止、输出等执行职责，保留必要入口并明确命名/权限；不能机械删除整套 Task 工具导致后台管理不可用 |
| `TaskService` / `JsonTaskStore` | 不再创建或存储新的普通 Todo；暂时保留 Worker、后台任务、Build 所需调用和数据。实施前明确旧数据处置，不无差别把执行记录导入每个 Agent 的 Todo，也不保留双写兼容层 |
| Build 依赖与 `BuildTaskLinker` | 保留既有执行/恢复逻辑及依赖校验；其精简归属作为后续独立评估，不与本轮清单替换混做 |
| 会话保存恢复 | 复用 `AgentSessionStore`；验证 Session 隔离和序列化往返，修正消息压缩误删 Todo 的生命周期风险；不新增 Todo 持久化系统 |

**已确认的旧实现缺陷**：Explore / Plan 的能力集仍有 `TaskContext`，但 `ReadOnlyAgentTools` 不含 `Task`，会收到不可执行的 update 提示。迁移须消除该提示，同时验证原生 provider 贡献的工具也符合 profile；不能假设旧的静态工具白名单自动约束新 provider 工具。是否允许只读 Agent 修改自己的会话 Todo，应明确为 profile 政策，与修改工作区的权限分开判断。

此处 agent-level 装配的 `AIContextProvider` 每次 agent invocation 执行，而非每次内部模型调用；ChatClient-level 装配可逐次模型调用执行（如 §4.5 Compaction），不能把执行频率当作该类型固有限制。Todo 迁移后也不能宣称清单快照每次模型调用自动刷新。`TodoCompletionLoopEvaluator` 是可选的另一个能力，不因启用 Todo 自动增加“清单未完成就续跑”的产品行为。

模式仍由 `MainModeContextProviderBuilder` 与宿主政策决定；关闭 LLM mode 工具避免状态分叉。启用 Todo 不意味着恢复 `mode_set`，模式提示也不能代替工具权限闸。

### 4.2 记忆专项：Harness 工作记忆 → OneCode 长期整理

#### 4.2.1 最终决定与职责边界

**待实施决定：需要会话工作记忆的路径启用 Harness 内置 `FileMemoryProvider`，通过 `HarnessAgentOptions.FileMemoryStore` 配置存储后端；不自研 provider，不先关闭默认实例再手动追加同类 provider。** 原生文件系统 store 优先，配置路径不等于自研存储。只有未来出现 Harness 未暴露的必要配置且有行为证据时，才另行评估显式装配原生 provider，不作为本次前置工作。

纠正旧结论：FileMemory 不能直接替代长期知识库，**不意味着应该关闭它**。两者应按生命周期分工，而不是二选一：

```text
对话 / 工具执行
├─ 原始消息与事件 → 现有 Session / history / compaction
└─ Agent 主动记录 → Harness FileMemoryProvider（会话工作记忆）
                       ↓ 产物快照，必要时回查会话事件
                  OneCode / AutoDream（筛选、核验、去重、作用域和人工保护）
                       ↓ 条件提交
                  Project / User 长期 MEMORY.md
                       ↓
                  MAF TextSearchProvider（search_memories）→ 后续对话
```

| 层次 | 负责内容 | 明确不承担的职责 |
|---|---|---|
| 对话历史 | 原始消息、工具调用、事件审计与压缩 | 不把每条消息直接晋升为长期知识 |
| MAF FileMemory | 工作笔记、发现、材料摘要、决策与待确认事项；原生文件工具和索引 | 不自动捕获所有重要对话，不直接治理用户长期偏好 |
| OneCode 长期记忆 | 候选核验、来源、去重、Project/User 分级、人工保护、保留政策 | 不再实现一套会话文件 CRUD/index；不把模型笔记视作用户确认 |
| MAF TextSearchProvider | 长期检索的模型工具接入与自身日志脱敏 | 不替产品完成排名、提交、隐私处理和命中反馈 |

工作文件与长期条目分别是可修改的候选材料和经过治理的知识，并非两套相同真相源。`MemoryService` / `IMemoryEntryStore` 保留长期职责，**不要将它们的 Load/Save 硬适配到 FileMemoryProvider**；后者是 Agent 上下文/工具组件，不是结构化条目仓储。

#### 4.2.2 原生能力、Session 隔离与实际限制

- `HarnessAgent.BuildContextProviders` 默认使用 `{cwd}/agent-file-memory` 下的文件系统 store；可用 `FileMemoryStore` 替换根目录/后端。工作产物必须与长期 `MEMORY.md` 分开，具体路径在 M3 固定并纳入会话元数据，不在本文先造一个尚未实现的目录约定。
- Harness 为每个新 Session 的 FileMemory 状态初始化「时间戳 + GUID」WorkingFolder。同一 Session 多轮使用同一目录；恢复该 Session 且 store 根目录不变可继续访问。独立 Session 默认不同目录。**隔离依据是 Session，不是 Agent 实例、模型调用次数或 UI 对话文本**；多个 Agent 共用 Session 时不可宣称自动按 Agent 隔离。
- `FileMemoryState` 的 WorkingFolder 存入 Session StateBag，正文存在 store，Session JSON 不包含文件正文。只恢复聊天消息、只换 Agent 实例，均不足以证明恢复成功或会话隔离。
- 独立 `new FileMemoryProvider(store)` 未传 initializer 时 WorkingFolder 默认为空；不能把 Harness 的随机目录政策泛化为所有装配方式。Harness 追加 `AIContextProviders` 不会去重，不允许默认和手动实例双挂。
- 原生提供 `file_memory_write/read/delete/ls/grep/replace/replace_lines` 七工具、描述侧文件、`memories.md` 索引维护和索引上下文注入，足以作为本次会话工作记忆基础。Agent 需主动调用工具，不是自动对话摘要器；压缩前记录重要信息的提示不构成必定写入的保证。
- 原生 provider 使用实例级写锁；正文、描述与索引分步更新。不得把复用框架等同于跨实例/跨进程事务保证，也不得把目录分区当作安全沙箱。宿主仍须定义同会话运行串行化、目录权限、预算及失败恢复。

#### 4.2.3 当前实现与目标差异

| 位置 | 当前实现 | 目标 |
|---|---|---|
| `OneCodeHarnessDefaults` | 所有路径强制 `DisableFileMemory = true` | 从公共 opt-out 移出强制关闭，按 profile 显式选择；启用路径使用 Harness 默认实例且仅一套工具 |
| `AgentSessionStore` / `MafSessionInvalidator` | 历史 epoch 变化可删除整个 mafSession | 历史与非历史状态分离；compact 不丢 WorkingFolder，不让旧历史/压缩索引复活 |
| `AutoDreamSessionScanner` / AutoDream | 按项目扫描会话事件，尚无 FileMemory 产物消费链 | 工作产物为主要候选输入，事件为必要核验依据；有来源绑定、快照与处理标识 |
| `MemoryService` / `MemoryEntryStore` | 长期条目、摘要、检索与使用反馈 | 保留长期领域，修复存储/提交边界，不复制会话工具 |
| `MemorySearchProviderFactory` | 已配置原生按需 TextSearchProvider | 保留薄装配，修复取消与自有日志/错误结果；避免工具和提示不一致 |

启用不代表全 profile 一刀切：Main 为第一批；Worker/Team 后续验证私有 Session/产物交付，不默认为并发共享可写目录；Explore/Plan 是否允许写私有工作笔记须与只读政策明确区分；AutoDream 作为整理消费者不因公共配置变化自动获得工作记忆写工具。各路径决定须在 M3 记录并以真实装配验收。

#### 4.2.4 长期记忆实现问题与证据分级

以下来自静态源码，不代表故障时序已运行复现；启用 FileMemory 不会自动修复长期存储。

| 优先级 / 证据 | 问题 | 修复边界 |
|---|---|---|
| P0 / 已确认代码路径，数据丢失待故障注入 | `MemoryEntryStore.LoadAllAsync` 捕获所有异常返回空，Upsert 以该结果合并写回 | 区分不存在、读取失败和取消；修改侧读失败必须拒绝提交，不能覆盖旧文件；查询侧在上层可控降级 |
| P0 / 已确认 | 搜索工厂外层 catch 吞取消；存储读取也吞取消；工厂记录原始 query 并返回异常原文 | 检索、命中回写和存储全链路传播取消；检查成功 Debug 与失败 Warning，稳定错误结果不泄漏原文；MAF 自身脱敏不覆盖产品 logger |
| P1 / 并发风险待反证 | 存储仅进程内静态锁、固定 `.tmp` 文件；原子替换不保证读改写不丢更新 | 定义每个目标文件的跨进程提交契约；覆盖命中回写、命令写入与 AutoDream 的竞争，不把 AutoDream 整合锁当所有写入者的锁 |
| P1 / 并发风险待反证 | AutoDream 在提交前读取快照检查 manual，之后独立 Upsert/Remove | 人工保护在最终提交临界区校验；检查后被用户修改时拒绝过期候选，不能仅靠解析阶段守卫 |
| P1 / 已确认 | AutoDream 创建更新条目不携带命中统计，Upsert 只保留 CreatedAt | 明确同 key 更新时保留 HitCount/LastHitAt 的政策；重置必须是业务决定而非默认值副作用 |
| P1 / 生命周期风险待验证 | 写操作解析目录后又通过 LoadAllAsync 解析可变 cwd；查询与回写也分开解析 | 固定一次运行/整合的项目身份和存储目标；切目录不得跨项目读写或回写命中 |
| P2 / 已确认算法 | 连续中文按正则 token 匹配，非语义检索；Top 6 完整正文没有总预算 | 先建立中文/代码术语评测与总输出预算，不未经评测引入向量数据库 |
| P2 / 已确认呈现 | User 后接 Project，自动索引仅取前 8；人工摘要全部注入且未标 scope | 定义 Project 优先、scope 呈现和索引总预算；条目数不等于 token 上限 |
| P2 / 已确认实现约束 | App 的 MemoryEntryStore 直接 File/Directory I/O，静态可变锁表 | 文件实现按项目约束迁到 Infrastructure 或使用既有抽象，领域与编排留在原职责层，不增加通用记忆平台 |

`HitCount` 表示检索返回次数，不代表知识正确或实际采用；淘汰目前按累计 HitCount、再按 UpdatedAt，不是严格 LRU，LastHitAt 未参与排序。源码注释和长期文档须随最终政策同步。人工条目容量淘汰豁免也不等于 TTL 永久豁免。

隐私修复不得通过打开 `EnableSensitiveTelemetryData` 完成：原生 TextSearchProvider 的 `SanitizeLogData` 仅处理自己的日志。现有 `MemorySearchProviderFactoryTests` 要求结果包含异常原文的断言须改为反证；直接调用内部搜索委托不等于验证真实 provider 工具边界。

#### 4.2.5 分批施工与验收（全部待实施）

依赖顺序：**M1 长期数据正确性 → M2 会话生命周期（联动 §4.5 B3）→ M3 原生启用 → M4 产物整理 → M5 质量与收口**。不在状态指针仍会随 compact 丢失时先放开默认工具。

| 批次 | 改造范围 | 必须提供的验收证据 |
|---|---|---|
| M1 / P0–P1 | 长期读写错误分类、取消/日志、命中保留、条件提交和固定项目目标 | 真实文件写入/重载；读取失败后原文件不变；搜索及回写取消传播；日志/错误结果无 query 和异常原文；并发人工更新不能被旧 AutoDream 候选覆盖 |
| M2 / P1 | 会话—项目—store 根—WorkingFolder 关联持久化；历史与非历史状态分离，防晚到保存覆盖 | 原生工具写入 → Session 经生产保存/重载 → 新 Harness 实例读取同一内容；Full/Partial compact 后仍可读，旧历史不复活；新 Session 不可见旧内容；损坏/缺失状态恢复政策明确 |
| M3 / P1 | 从公共 opt-out 移除强制关闭；Main 先启用 Harness 内置实例，配置原生 store；定义全部 profile 政策与权限 | 捕获实际 agent 工具，仅一套 file_memory_*；未启用路径没有工具和误导提示；同会话串行化、跨项目目录、取消/异常和流式退出覆盖；释放责任及产物容量/清理政策明确 |
| M4 / P1 | AutoDream 发现工作产物、固定快照、来源及处理标识；必要时回查事件；长期提交后再标记处理完成 | 真实 provider 产物 → 整理候选 → 长期存储 → search_memories 可检索；重复运行不重复晋升，失败可重试；活动文件修改不混入快照；未确认推断不能自动成为 User 全局偏好；人工条目保护有反证 |
| M5 / P2 | 索引与检索预算、中文评测、使用反馈语义、文件实现层次与文档迁移 | 短语改写/中文/同 key 不同 scope 有用例；大条目不超预算；保留或升级检索算法有评测依据；全文同步长期文档与注释 |

M2 的隔离验收必须同时包含「恢复同一 Session」与「创建不同 Session」；只新建两个 provider/Agent 对象不能证明按对话隔离。测试使用可控 ChatClient 驱动原生工具及临时文件系统，不依赖外网模型；跨进程保证另用独立进程或等价存储契约验证，不能拿进程内测试代替。

产物清理必须区分 compact、清空、删除对话与归档；compact 不清理工作文件，未成功提交不得因已扫描而删除候选。明确无文件产物时的事件回退、已有长期 MEMORY.md 保留、旧会话无目录关联的处置，不强制一次性把历史对话生成工作文件。

#### 4.2.6 源码定位与验证边界

原生依据：`HarnessAgent.BuildContextProviders`、`FileMemoryProvider` / `FileMemoryState`、`TextSearchProvider`（链接见 §9）。产品依据：`OneCodeHarnessDefaults`、`AgentSessionStore`、`MafSessionInvalidator`、`AutoDreamSessionScanner` / `AutoDreamService`、`MemoryService` / `MemoryEntryStore` / `MemorySearchProviderFactory` 及对应 Tests。

本轮核对 Infrastructure 的 `obj/project.assets.json`，MAF Core/Abstractions/Harness/Workflows 为 1.21.0，Shell/Hyperlight/MCP 保持 §1.1 的预发行版本。对 `dotnet-1.21.0..HEAD` 检查 Harness、Core/Harness、Skills、Compaction 和 TextSearchProvider.cs 未见差异；不得外推审批等其他路径。上述记忆分析与计划均为静态审计，未新增 C# 测试或构建通过记录。

实施时同步 ADR 0004、ADR 0007、memory-overview、background-services 及实际引用：历史上删除的自定义会话记忆仍保持删除，但不能把该历史决定当成禁止引入原生 FileMemory 工作记忆的依据；“Agent 不写 MEMORY.md”须精确限定为长期条目文件，不与新工作产物写入工具混淆。本次仅更新施工文档，长期文档仍描述现状，待实施时迁移。


### 4.3 Skills：保留原生装配，重构入口规则、脚本执行与资源生命周期

**决定（待实施）**：需要重构，但不是“从自研 Skills 换回 MAF”。当前 `SkillProviderFactory` 已使用官方 `AgentSkillsProviderBuilder` / `AgentSkillsProvider`；继续保留此薄装配及每 run 重建策略。文件发现、内联技能、MCP 技能、聚合、缓存和去重主要由 MAF 实现，不重写 provider、缓存或 MCP 技能协议，也不为减少一个 Disable 改用手工 source 管道。

真正的产品适配是目录政策、内置技能内容、已连接 MCP 客户端选择、斜杠调用和脚本 runner。前几项保留薄适配，重构集中于以下三项。

#### 4.3.1 统一斜杠入口与模型入口的技能规则

**源码现状**：`SkillCatalog.LoadUserInvocableSkills` 使用自定义发现与解析，读取目录顶层 `*.md` 和直接子目录的 `SKILL.md`，同名条目后写覆盖前写；模型侧走 MAF 文件 source 的 `SKILL.md` 发现规则，builder 首项优先去重。两个入口还分别使用产品 frontmatter parser 与 MAF parser。这是已确认的规则差异；同名技能最终选中不同内容或仅一侧可见的具体案例，尚需行为测试复现，不能假定全部技能均受影响。

**改造**：明确共享技能的身份、发现范围、同名优先级及格式规则，尽量复用 MAF 发现/解析结果，再投影为斜杠命令，避免长期维护两套冲突规则。保留用户入口需要的 `UserInvocable` 筛选、`$ARGUMENTS` 和命名参数渲染；有意仅供用户调用的命令、旧顶层 Markdown 格式须明确处置，不强制所有入口集合相同，也不静默删除。实施前确定优先级政策，不把现有任一侧的覆盖顺序未经评估直接当作目标。

**验收**：用真实技能文件覆盖多目录同名、bundled 与文件同名、嵌套目录、两种文件布局和 frontmatter 差异；共享技能在两入口解析到相同身份与原始正文，参数渲染只在用户入口按政策发生。只供用户调用的条目与 MCP 技能是否展示为命令均有明确边界，不因统一规则意外扩大命令集合。

#### 4.3.2 完善文件脚本执行边界

MAF `AgentFileSkillScript` 负责脚本协议与使用前路径校验，但实际执行需要调用方提供 runner；没有 runner 时会报错。保留文件脚本能力就需要执行适配，不等于必须保留当前独立进程实现。

**源码现状**：`SubprocessScriptRunner` 根据扩展名选择解释器，通过 `ArgumentList` 传参，启动进程并读取输出；未设置显式超时或输出上限，取消时未显式终止进程树，非零退出码仅记录警告并仍返回 stdout。取消等待或 Dispose Process 不等于终止实际子进程；该实现不是沙箱，审批成功也不能证明执行隔离成立。日志还包含原始 stderr，异常结果包含 `ex.Message`，需要纳入敏感信息边界审核。

**改造**：保留 MAF `AgentFileSkillScriptRunner` 接口，优先核对并复用项目已有受控执行设施；其是否支持技能脚本的参数、工作目录及生命周期需先验证，不预设现成替代已经可用。明确超时、取消后的进程树清理、输出限额与截断标识、非零退出/启动失败的可判定结果、日志脱敏和工具结果边界。维持脚本审批与权限链，不引入全工具自动批准；不以这次重构名义新增一套并行执行引擎或宣称已具备沙箱。

**验收**：从实际 MAF `run_skill_script` 工具路径执行受控本地脚本，覆盖成功、非零退出、大输出、超时和调用方取消；观察子进程确实退出、输出有界、失败不伪装成功、日志不泄露测试秘密。调用方取消应保留取消语义，超时与执行失败应按明确协议区分；不能只测试 runner 委托而绕过生产审批/权限链。

#### 4.3.3 明确 provider/source 释放方

保留每 run 创建 provider、run 内由 MAF 缓存的策略。由拥有该次运行装配的宿主负责释放 provider/source，覆盖成功、异常、取消和流式枚举提前结束；释放须在最后一次使用后进行。若审批暂停后的恢复仍复用实例，应覆盖整个使用期，不把一次枚举结束机械当作所有权结束。验证共享 MCP 客户端仍可使用，以及下次 run 能观察文件和 MCP 连接变化。

不再以“记录有界不释放”作为最终交付替代；具体所有权证据与需要守住的审批边界如下。

#### 4.3.4 原生扩展与审批证据

**runner 属于文件 source/script，不必由 Harness 直接接收。** 证据：

- `AgentSkillsProviderBuilder.UseFileSkills` 将 runner 交给 `AgentFileSkillsSource`。
- `AgentFileSkillScript.RunAsync` 调用自身保存的 runner。
- `AgentSkillsProvider(AgentSkillsSource, ...)` 使用调用方的 source；不会因为 Harness 没有 runner 参数而删除 source 中的运行能力。
- 文件、内联、MCP source 可以用框架现有聚合 source 组合；不需要重新实现 MCP 技能协议。

**但不要只换一个赋值就宣布等价**。builder 的行为包含聚合 → 默认缓存 → 可选过滤 → 首项优先去重，并构建 `ownsSource: true` 的 provider。Harness 传入自定义 source 使用默认 `ownsSource: false`，不会替调用方补齐这些装饰。迁移必须明确 source 清理方、每 run 重建、MCP 客户端所有权、重复技能优先级与顺序。

当前 `SkillProviderFactory` 每 run 构建、使用官方 builder，是合理且较省维护的组合。保持它不需要额外理由；只有 Source 方案能减少总体复杂度或满足新需求时才迁移。不能为少一个 Disable 手写一套 builder 内部政策。

**审批链：已核实（正面）**。三个技能工具默认都被包成 `ApprovalRequiredAIFunction`（`AgentSkillsProviderOptions.Disable*Approval` 默认 false）；OneCode 只把只读的两个（`load_skill` / `read_skill_resource`）放进自动批准规则，`run_skill_script` 仍走审批 + 权限链。**反向风险**同样要记：MAF 的自动批准规则按**工具名**匹配，任何与只读技能工具同名的产品工具会被顺带自动批准；`AllToolsAutoApprovalRule` 会连带放行脚本执行，因此不得引入。审批通过不等于沙箱、超时、取消清理成立——那三项仍需独立验收。

**资源所有权：已核实结论**。`SkillProviderFactory.Create` 每 run 新建 `AgentSkillsProvider`，而 builder 构建的是 `ownsSource: true`，即 provider 拥有 source 管道。实际情况：

- MAF 的 `ChatClientAgent` 不会释放 `AIContextProviders`，OneCode 目前也没有为每 run 的 provider 指定释放方。
- 已识别的待释放资源包括 `CachingAgentSkillsSource` 的 `SemaphoreSlim` 门（默认单 cache key）与 MCP `ArchiveEntryLoader` 的 reconcile gate。单实例资源较轻、不使用 `AvailableWaitHandle` 时通常不涉及该等待句柄，但不能据此保证跨 run 总资源有界；仍需释放所有权明确，不能把 GC 可回收当成无需生命周期管理。
- **释放是安全的**：`AgentMcpSkillsSource` 明确不释放调用方提供的 MCP 客户端（`CA2213` 抑制注明 client 归调用方所有），因此释放该 provider 不会误释放共享 MCP 客户端。

结论：按 §4.3.3 落实宿主释放责任并验证所有退出路径，不以潜在资源较轻为由长期悬空。上文 source 迁移仅说明技术可行性，不是本轮施工任务；继续复用官方 builder，不手写其内部政策。

### 4.4 搜索并非单一能力

- `HostedWebSearchTool` 是交给模型服务端执行的工具；Harness 默认加入，不先检测后端支持。当前多后端通用配置下关闭它合理。
- OneCode `WebSearchTool` 是本地 AIFunction 调用 Tavily / DuckDuckGo 提供方链，包含产品错误处理、缓存和域过滤；与 hosted 搜索不是同一执行边界。
- `TextSearchProvider` 用于注入自定义检索，当前承接记忆；`ToolSearch` 是工具发现，Grep / Glob / 符号检索是代码库搜索。不能仅因都叫搜索就合并。

是否有独立 profile 使用 hosted 搜索，应由支持的后端、费用与隐私政策决定，不应仅为恢复默认而自动启用。

### 4.5 Compaction：先修正确性与状态边界，再评估装配入口

#### 4.5.1 专项结论与证据边界

**决定（待实施）**：保留 MAF Provider、分组索引与原生「工具折叠 → LLM 摘要 → 截断」组合；移除当前按参数判重的 L0。保留显式 `/compact` 的产品编排，但先修范围、原子性、摘要契约和状态失效。预算与告警口径随后收敛，是否迁入 Harness 策略入口最后决定，不以减少 Disable 为目标。

2026-09-17 专项核查确认 `src/OneCode.Infrastructure/obj/project.assets.json` 使用 Core/Abstractions/Harness/Workflows 1.21.0，其他包版本见 §1.1；本地 HEAD 为 `ab8299beb`。`dotnet-1.21.0..HEAD` 内 Compaction、Harness、Core 的 Harness 与 Skills 目录无差异，因此本节压缩源码结论可对应 1.21.0，不外推至审批与遥测实现。

证据标记：**〔源码〕**表示读取实现确认；**〔规则复算〕**表示对源码规则做内存复演，未执行实际 C#；**〔待验证〕**表示需要真实 Provider/Session 行为测试。此次只写回分析与计划，未修改运行时代码、未运行构建或 C# 测试。

#### 4.5.2 目前什么样，重构后什么样

| 维度 | 当前实现 | 目标（待实施） |
|---|---|---|
| 自动策略 | 自定义 L0 参数判重 + MAF L1 折叠/L2 摘要/L3 截断 | 删除 L0；保留原生三级组合，首批不同时调整现有比例 |
| 信息保真 | 同名同参数的旧调用即使结果不同也可能被排除 | 不再无条件按参数判重；失败→成功、文件变化等信息可进入后续摘要 |
| 显式压缩 | 输入与替换区间分别计算；Full/Partial 共用按条数清理 | 区间只解析一次；摘要与替换一致；Full 按原子组保尾部，Partial 不裁剪区间外内容 |
| 摘要格式 | 手动剥离 `<analysis>`，自动原样保存；prompt 要求详尽、逐字保留 | 两路径采用直接输出、有界、面向继续工作的统一摘要契约 |
| 摘要失败 | 普通异常恢复；取消、空摘要、摘要变长缺少完整保护 | 取消保持取消且不提交破坏性状态；空/无效/无收益摘要不替换原文；降级策略有测试 |
| 请求预算 | 消息按字节/4估算；固定输入开销未完整计入，非法值钳制为 1 | 校验模型配置，按实际请求预留指令/工具/协议/输出开销；无法满足预算时明确失败或降级 |
| Session | 自动索引随 mafSession 持久化；手动压缩删除整个 mafSession | 历史失效与非历史状态分离；保留 Todo 等政策允许的状态，旧压缩索引不得复活 |
| 告警 | turn 后按完整 transcript 估算，却被称为提前预警 | 如实标为历史规模提示；模型输入压力单独计量，不冒充压缩前通知 |
| 装配入口 | 外部 ChatClient Provider + Harness opt-out | 先保证唯一所有者；P2 比较 Harness 策略入口与现状，允许有理由保留 |

这里的“保留不同结果”不是永不压缩历史；后续仍可按预算摘要或截断，但不得把非等价调用宣称为无损去重。

#### 4.5.3 原生能力与自定义必要性

当前 `inputBudget = max(1, window − output)`，阈值为 `(int)(inputBudget × ratio)`。L0 Always、保护尾部 10 个非系统组；L1 Main/Worker 比例为 0.50/0.40、保护 2 组；L2 为 0.70/0.60、保护 8 组；L3 为 0.85/0.80、保护 2 组。`TokensExceed` 使用严格 `>`，每层基于上一层结果重新评估；原始输入超过 70% 不保证触发 L2。L2 的 8 组保护不约束 L3，不能写成整条管道的保留保证。

Harness 默认 `ContextWindowCompactionStrategy` 只有折叠 0.50 → 截断 0.80，各保护 2 组，没有 LLM 摘要，并校验非法窗口与输出参数。OneCode 增加摘要以保留编程决策、失败过程和待办、区分主/子 Agent 预算、加载产品 prompt，都是合理产品政策；**需要组合原生策略，不需要自研 Provider、分组或工具循环**。

MAF 还提供 `CompactionProvider.CompactAsync` 和 `CompactionStrategy.AsChatReducer()`，可用于临时压缩及历史 reducer。保留 `/compact` 的理由是 OneCode 的指定范围、边界标记、Conversation 存储、UI 和 Pre/PostCompact hooks，不是“MAF 完全不能压缩持久化历史”。优先复用这些入口，但需先验证它们能否满足范围保留与失败原子性，不机械强换。

#### 4.5.4 装配现状与迁移限制（沿用的正确结论）

自定义 `CompactionStrategy` 在 `DisableCompaction = false` 时直接生效。**不要求同时设置** `MaxContextWindowTokens` / `MaxOutputTokens`；只有没有自定义策略时才用两字段构造默认策略。

OneCode 主构建入口未提供这些 Harness 字段，因此关闭开关目前是防御性的：全仓没有任何调用方设置 `HarnessAgentOptions.MaxContextWindowTokens` / `MaxOutputTokens`（`AutoDreamService` 设的是 `ChatOptions.MaxOutputTokens`，不是 Harness Options 字段），所以即便不关，Harness 也不会构造默认压缩策略。`CompactionPipelineBuilder` 的链为**自定义重复调用清理 + MAF 工具结果折叠 + 摘要 + 截断**，不是「全部策略均 MAF 原生」。

**文档债务（本次复核新增）**：`CompactionPipelineBuilder` 的 XML 注释仍写「完全使用 MAF 原生压缩策略」，与自带 `SnipDuplicateCallsCompactionStrategy` 的 L0 相矛盾；同处还声称「替代早期版本的硬编码阈值」。修改该文件或迁移压缩时必须一并修正注释，并与 [compact-thresholds.md](../compact-thresholds.md) 保持一致，避免下一轮审计再次以错误注释为据。

Harness 同样通过 `UseAIContextProviders` 安装压缩；自建默认 history 时还可能安装 reducer。OneCode 主路径显式传入 MAF InMemory history，所以不走该 fallback reducer 分支。

当前请求方向的关键顺序（从外向内，省略产品 run/function 横切）：

`approval binding → bypass → FICC → message injection → per-service history persistence → chat OTel → 产品 ChatClientContextProviders → supplied client`

Harness 自有压缩若启用，位于 history persistence 与 chat OTel 之间。因此二者是相同机制、同在内部模型调用范围，但不是完全相同的装饰位置。迁移时移除旧 provider，验证遥测、状态 key、恢复、摘要客户端不递归和工具消息配对；**双挂是错误迁移的结果，不是迁移的必然结果**。

#### 4.5.5 已发现的问题与风险

1. **L0 非等价去重〔源码 + 规则复算〕**：`SnipDuplicateCallsCompactionStrategy` 只按工具名与参数序列化判重，不比较结果或组内其他文本。在两组旧调用后追加 10 组保护尾部，内存复算确认同参数 `run_tests` 的 FAIL→PASS 会排除较早 FAIL。真实测试/文件/Git 观察经常参数不变、结果变化。保护尾部只能延迟风险，不能证明等价。受保护组还被跳过判重，不应笼统称为全历史“只留最近一次”。
2. **L1 不等于正文摘要〔源码〕**：原生 `DefaultToolCallFormatter` 将 `FunctionResultContent.Result.ToString()` 原文拼入 YAML-like 块，无长度预算，且不保留调用参数。大文件结果可能仍很长，路径等定位信息却丢失；真实节省量待测。框架已提供 `ToolCallFormatter`，如有必要，应在此扩展，不另建策略引擎。
3. **显式压缩区间与原子性〔源码〕**：`CompactService` 按原始索引选摘要输入，`CompactApplier` 应用时才扩展原子边界，可能删除未进入摘要的消息。`ApplyPartialCompact` 保留区间前后后仍调用全局 cleanup；该方法反复 `RemoveAt(3)` 限制到 `3 + RecentMessagesToKeep`，违反区间外保留契约。Full 先扩展/规范化工具对，随后按条数裁剪可能再次拆对。需用实际领域消息测试固定反例，不沿用“至多 11 条”作为正确性的唯一标准。
4. **L2 非完整事务〔源码 + 待验证〕**：普通异常恢复被排除组，但取消异常绕过恢复；空响应替换为 `[Summary unavailable]`，不会回滚；摘要插入前按排除量判断 Target，插入后不检查净收益。Provider 恢复索引时浅拷贝 group，取消是否留下错误排除状态须用已有 Session 验证。不能宣称所有失败均自动恢复；L3 也不能弥补错误摘要的语义损失。
5. **摘要输出与请求契约分叉〔源码〕**：产品 prompt 要求 `<analysis>` + `<summary>`、逐字用户消息及完整代码片段；手动 `FormatSummary` 剥离前者，自动 L2 原样保存。原生摘要调用未传 ChatOptions，手动明确传模型与 8192 输出限制；自动摘要的实际路由和输出上限须捕获请求验证，不因传入共享 IChatClient 就宣称与主模型一致。手动映射还丢弃 Assistant 的工具调用块、把结果转为 User 文本，需保留调用来源与关联信息。
6. **状态与存储边界〔源码〕**：自动 Provider 保存 included/excluded 组与摘要，减少模型输入不等于减少 transcript 或 Session 落盘体积。`MafSessionInvalidator` 全量删除 mafSession；启用 Todo 后连带丢弃清单。重置压缩索引之外，还需处理 InMemory history、epoch、在途运行和后续保存覆盖，不能只删除一个 key 就认定生命周期已收口。
7. **预算不完整〔源码〕**：当前索引默认 UTF-8 字节数/4，不是模型 tokenizer 的精确输入计数；索引只计消息，不完整覆盖独立指令、工具 schema、协议及多模态开销。非法 `window − output` 被钳制为 1，掩盖配置问题。系统消息/保护尾部本身过大时策略也可能无法达标，不能把比例触发称为防超窗保证。
8. **告警口径不同〔源码〕**：`AutoCompactService` 在 turn 后按完整 Conversation + 产品 estimator + 固定预留 8192 计算，自动压缩在调用前按 included groups 计数。输入已压缩时仍可能告警，应称为历史规模提示，不能声称一定先告警再压缩。

#### 4.5.6 详细施工计划与验收（全部待实施）

依赖顺序：**批次一正确性 → 批次二预算/状态 → 批次三装配与收口**；每项可独立提交，但不得先迁入口掩盖算法缺陷。Todo 与 FileMemory 启用均依赖下述 B3 状态验收，FileMemory 同时遵循 §4.2 M2。修改运行时代码前读对应项目 AGENTS.md，先用真实写入/调用路径补失败用例，不仅修改 Options 期望值。

| 编号/优先级 | 实施范围与步骤 | 完成证据 |
|---|---|---|
| A1 / P1 | 从 Infrastructure `CompactionPipelineBuilder` 删除 L0 装配及 `SnipDuplicateCallsCompactionStrategy`，清理引用；不同时改 L1–L3 比例 | 可控 ChatClient 捕获实际低预算压力输入：同名同参数不同结果均存在；并行工具组不拆开；全文无失效类引用 |
| A2 / P1 | 在 `CompactService` 生成一次规范化区间与消息快照，摘要和 `CompactApplier` 使用同一范围；Full 按原子边界保尾部，删除会拆对的按条数 cleanup；Partial 只替换区间 | 跨工具边界选择、长前后缀、并行调用、空/越界范围测试；区间外 ID/内容/顺序不变，所有被删除消息都进入摘要输入；不能为满足固定条数拆组 |
| A3 / P1 | 调整 `system/compact.prompt` 与 `CompactPromptBuilder`：直接输出工作摘要，保留目标/约束/已验证结论/路径/待办，不要求分析段与全文复述；两路径统一格式、模型路由与输出限制，保留用户附加要求和缺失 fail-fast | 捕获两路径真实摘要请求与回填内容；格式一致、无分析段、工具名/参数/结果来源不丢；纯格式旧处理按实际需要删除，不留双轨 |
| A4 / P1 | 针对原生 L2 先补已有 Session 的取消、空摘要、摘要变长测试；优先核对上游修复，否则用最小适配隔离候选状态、验证摘要后提交，不复制整套 Provider | 调用方取消继续抛取消且状态不变；空/无效/无收益输出不替换原文；普通失败是否继续 L3 有明确政策，失败、保存/重载后状态均正确 |
| B1 / P1 | 对齐 `CompactionProviderBuilder`、Infrastructure builder、实际 ChatOptions：区分模型能力上限与请求输出额度；校验非法配置，扣除最终指令/工具/协议固定开销并保留安全余量；验证手动摘要输入也不超其模型预算 | 小窗口、中文、大 schema、模型切换、超大保护尾部用例；最终请求捕获能解释预算；无法压至预算时停止/降级有界，不无限重试或保证未经证实的精确 token |
| B2 / P1 | 测量默认 L1 在读文件/搜索/测试输出上的 token 节省与定位信息损失；只有不足时使用原生 `ToolCallFormatter`，保留路径/命令/状态并限制正文 | 固定样本记录前后估算、可恢复定位与截断标识；无收益不承诺压缩率，不重写分组/Provider；工具输出仍按不可信数据处理 |
| B3 / P1 | 联动 `AgentSessionStore`、`MafSessionInvalidator`、`CompactApplier`：定义历史与非历史 stateKey 所有权、epoch 和提交顺序；评估定向失效/选择性恢复，保留 Todo 与 FileMemory WorkingFolder，旧历史与压缩索引失效；审核审批状态，不盲目复制全部 StateBag | Todo/FileMemory 原生工具写入→Full/Partial compact→真实保存/重载后仍可访问；FileMemory store 根与目录关联保持；旧历史不复活；清空/新建按政策重置；并发运行或晚到保存不得覆盖新历史，必要时禁止活动 run 中手动压缩 |
| B4 / P1 | `TokenBudget`/`AutoCompactService`/UI 区分完整历史规模与实际模型输入；先纠正标签，再决定是否增加请求前事件；不新增一套自动压缩 | 已自动压缩但 transcript 未变的场景，两个指标各自正确；不把 turn 后通知称为提前预警 |
| C1 / P2 | 在 A/B 契约保护下比较 Harness 策略入口与现状；若迁移则 builder 提供策略、删除旧 Provider 装配、开启 Harness 压缩并继续显式提供 history | 同 run 多轮压缩、历史回调、OTel、工具配对及恢复等价；唯一 Provider；状态 key 转换政策明确；摘要客户端不递归进入自身压缩链 |
| C2 / P2 | 同步本审计、ADR 0007、compact-thresholds、CompactService/CompactionPipelineBuilder/AutoCompactService XML 注释，以及搜索到的实际引用；记录最终迁移或保留决定 | 无“完全原生”与 L0 并存、无提前预警/必然回滚/持久化能力缺失的错误断言；build + 全量 test 通过，外部模型未验证范围单独记录 |

C1 的具体约束：当前 stateKey 为 `compaction`，Harness 按策略类型命名，通常变为 `PipelineCompactionStrategy`。切换前明确旧状态转换或一次性丢弃重建政策，不误删非历史状态、不留双写兼容入口。显式 InMemory history 保持无 reducer，避免意外启用 Harness fallback 的额外压缩阶段；不能仅删外部 Provider 后认定完全等价。

**不做的事**：不自研 Agent 工具循环、Provider 或通用消息分组；不为保留类而复杂化 L0；不在未测量前认定自定义 formatter 必需；不把所有 Disable 消失或迁入 Harness 当作验收目标。若原生策略缺陷需要适配，以已失败的行为测试界定最小范围。

#### 4.5.7 源码定位与验证边界

- `src/OneCode.Infrastructure/Agent/CompactionPipelineBuilder.cs:98–128`：策略、阈值与 stateKey；`SnipDuplicateCallsCompactionStrategy.cs:78–143`：只按调用参数判重。
- `agent-framework/dotnet/src/Microsoft.Agents.AI/Compaction/ToolResultCompactionStrategy.cs:172–253`：默认 formatter；同目录 `SummarizationCompactionStrategy.cs:146–218`：排除、异常与插入；`CompactionProvider.cs:143–205`：状态、投影；`CompactionMessageIndex.cs:501–506`：默认计数。
- `src/OneCode.App/Services/Compact/CompactService.cs:86–128` 与 `CompactApplier.cs:51–87,149–163`：区间与 cleanup；同目录 `CompactPromptBuilder.cs:70–124`：映射与输出处理；`MafSessionInvalidator.cs`：全量状态失效；`TokenBudget.cs`：告警预算口径。
- 本轮规则复算仅覆盖 L0 的非等价去重反例，不等于 C# 单元/集成测试通过。A2/A4/B3 等风险需在真实 Provider、领域消息和保存/恢复边界补测；历史 §7 的 49 项不能作为这些新目标的验收。

### 4.6 HarnessInstructions：使用 MAF 合成，保留产品提示词加载与渲染

**决定（待实施）**：将 OneCode 通用提示词传入 `HarnessAgentOptions.HarnessInstructions`，将主 Agent 模板渲染结果或子 Agent 角色指令传入 `ChatOptions.Instructions`，由 MAF 完成合成。删除重复的手工拼接职责，不删除产品提示词内容，也不整体重写提示词管理系统。

#### 4.6.1 原生能力与当前实现的差异

`HarnessInstructions` 是通用系统指令片段，不是独立的提示词存储或模板引擎。`HarnessAgent.BuildInnerAgent` 已实现确定的合成顺序：`HarnessInstructions` 在前，`ChatOptions.Instructions` 在后，中间两个换行。前者为 `null` 时使用 MAF `DefaultInstructions`，空字符串可明确抑制默认，非空产品字符串替代默认内容；因此“使用 MAF 原生入口”不等于“只能使用 MAF 默认文案”。

当前 `OneCodeHarnessDefaults.ApplyProductOptOuts` 强制设置 `HarnessInstructions = ""`；`PromptComposer` 预先加载通用片段、渲染主体，然后在 `Compose()` 中手工拼接。这条旧路径不是运行时错误，但拼接与 MAF 已有能力重复，迁移收益主要是职责清晰，不应夸大为新增功能。

| 当前职责 | 处置 |
|---|---|
| `PromptComposer.Compose()` 拼接 harness + body | 移除这项重复职责，交给 MAF；调用方分别传递两段，不把旧的完整串再当作主体传入 |
| `IPromptManager` / `PromptManager` 按存储优先级加载文件与覆盖内容 | 保留；Harness Options 接受字符串，不负责寻找或加载文件 |
| 主模板的 system_context / available_tools / user_context / memory_section 渲染 | 保留，渲染结果作为 agent 专属指令；不把未渲染模板交给 MAF |
| 必需提示词缺失时抛错 | 保留既有失败策略；不能用 null 意外回退到 MAF 默认文案，掩盖产品文件缺失 |
| 子 Agent 的角色正文及记忆检索提示 | 保留产品职责，并核对 `search_memories` 等提示与该路径实际工具一致；不无条件广告不存在的工具 |

`system/harness.prompt` 含敏感信息处理、工具输出不可信、独立调用并行及源码检索策略等产品内容；MAF 默认主要提供分解任务、使用工具、调整失败策略和汇报结果等通用建议。前者不是框架特殊能力，但不能因迁移合成入口而直接丢弃；提示词也不能替代实际权限检查。

#### 4.6.2 明确改造步骤

1. 梳理 Main / fork / Team 等实际调用链，把“已合成的 system prompt”拆为通用片段与 agent 主体的独立输入。保留统一的加载/渲染入口，将必要输入传到 Infrastructure 的 Harness 构建处，不新增一套平行提示词服务。
2. 通用产品片段赋给 `HarnessInstructions`；主体赋给 `ChatOptions.Instructions`。保留原有 trim 与段落边界语义，避免拼接位置改变导致内容丢失或重复；不额外叠加 MAF 默认文案。
3. 调整 `ApplyProductOptOuts`，不再全局强制把调用方提供的 `HarnessInstructions` 清空。逐条明确独立路径的值，不能仅删除赋值就让 null 自动引入默认文案。AutoDream 使用专用提示，不假设它经过 `PromptComposer`，需保持其有意的提示范围。
4. 删除旧的 `Compose()` 及调用方预拼接路径；按剩余加载/渲染职责精简 `PromptComposer`，不以删整个类为目标。同步旧方法注释、注册或测试中的失效引用，不留双轨兼容合成。
5. 在真实 agent 装配测试保护下交付；这次迁移不顺带改变模式控制、权限、工具策略或提示词文案。若核对发现工具提示不一致，作为明确的修复项记录和验证。

#### 4.6.3 验收要求

- 使用可控 ChatClient 捕获实际收到的最终指令，而非仅测试两个字符串字段：通用片段恰好一次，位于角色/主模板正文之前，没有旧完整串重复进入主体，也没有非预期 MAF 默认文案。
- Main / fork / Team 的模板占位符已替换；环境、项目上下文、记忆与角色内容不丢失。按现有存储优先级验证覆盖生效；必需文件缺失仍失败，不静默回退。
- AutoDream 等独立入口的专用政策有单独用例；模式提示和工具提示与实际 profile 一致。调用方配置不被公共 opt-out 清空；有意传空串的路径仍保持抑制行为。
- 更新 ADR 0007 中旧“提前合成 + 空 HarnessInstructions”决定，全文同步 PromptComposer、公共 opt-out 和指令合成相关文档/注释。本轮仅更新本文计划，不表示代码或 ADR 已完成迁移。

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

**API 更正**：不存在 `ChatOptions.MaximumIterationsPerRequest`。迭代上限有两个层级：Harness 侧 `HarnessAgentOptions.MaximumIterationsPerRequest`（已核实转发给 `FunctionInvokingChatClient.MaximumIterationsPerRequest`），以及迁出 Harness 后需要自配的 `FunctionInvokingChatClient` 选项。OneCode 用 `PipelineOptions.MaxToolCalls` 同时驱动 Harness 迭代上限与本仓工具调用计数——**数值同源、语义不同**（迭代轮数 vs 工具调用总数），不能视为同一预算。禁止照猜测 API 写施工代码。

**审批绑定语义的版本边界（本次复核新增，重要）**：`ApprovalResponseBindingChatClient` 在 `dotnet-1.21.0` 与本地 checkout HEAD 之间被 `[BREAKING]` 改写。1.21.0 的绑定逻辑把「调用方消息历史中出现的审批请求」也视为配对权威（以覆盖只回显响应、不回放原请求的调用方，以及宿主内部生成的审批）；HEAD 改为**只承认框架自身记录过的请求**，历史中出现的请求不再是授权依据，同时把消息里已带 `FunctionResultContent` 的调用当作已结算历史。OneCode 会通过 `AgentSessionStore` 持久化 session，并由 ApprovalBroker 处理审批，**恢复/回放路径正落在被改写的语义上**。因此：① 当前关于绑定的结论只能按 1.21.0 描述；② 一旦采用 1.21.0 之后的后继版本，必须重跑「暂停 → 恢复 → 审批响应绑定」与「只回显响应、不回放请求」两组用例，再决定审批/权限侧是否调整。同类核对适用于 `ApprovalNotRequiredFunctionBypassingChatClient` 与 `OpenTelemetryAgent`。

另一条与本项目直接相关的框架行为，**出自 HEAD 新增的那份共享判定注释**（1.21.0 的旧私有方法语义相同，但该描述文字本身属 HEAD，需按所引用版本复核）：`FunctionInvokingChatClient` 的审批是**全有或全无**——只要同一响应中存在一个 `ApprovalRequiredAIFunction`，该响应里**所有** `FunctionCallContent` 都会变成 `ToolApprovalRequestContent`，包括原本不需要审批的工具，再由 bypass/binding 装饰器负责区分。技能工具默认需要审批，因此它会与普通工具调用同批出现在一个响应里，这是 OneCode 审批链必须覆盖的真实场景（验收见 §6 P1）。

## 6. 按优先级实施

以下是待办，不代表已经修改或验证完成。按最小变更逐项交付，不要同时更换能力入口和业务语义。

| 优先级 | 工作 | 完成证据 |
|---|---|---|
| P0 | 完成记忆 §4.2 M1 的读失败/取消/隐私边界 | 读失败不当作空库覆盖；真实工具与存储路径取消传播；日志/结果不暴露原文 |
| P1 | 完成记忆 M1 剩余提交契约与 M2 生命周期（联动压缩 B3） | 人工保护提交时校验、更新保留反馈；原生工作文件跨 Session 保存恢复和 compact 可读，新 Session 隔离 |
| P1 | 完成记忆 M3–M4：按 profile 启用 Harness FileMemory，AutoDream 消费产物 | 实际工具仅一套、权限与提示一致；产物快照→长期提交→检索闭环，失败重试和重复整理有反证 |
| P2 | 完成记忆 M5：检索/索引预算、中文评测、存储层次和文档收口 | 不靠增加条目数量证明召回；源码注释与长期知识/工作记忆新边界一致 |
| P1 | 建立实际装配契约，而非仅断言 Options | Main/Worker/Team/Explore/Plan 的有效工具、provider 与权限符合 profile；AutoDream 单独覆盖 |
| P1 | 统一 Skills 双入口的发现、解析与同名优先级（§4.3.1） | 真实文件验证共享技能身份/原始正文一致；用户专属格式、参数渲染及 MCP 命令可见性政策明确 |
| P1 | 完善 Skills runner 执行边界（§4.3.2） | 实际 MAF 工具路径覆盖超时、取消进程清理、输出限额、非零退出与信息边界；保留审批权限，不宣称具备未经验证的沙箱 |
| P1 | 落实每 run Skills provider/source 的宿主释放责任（§4.3.3） | 成功/异常/取消/流式提前退出及审批恢复使用期均覆盖；释放后共享 MCP 客户端可用；同 run 缓存与跨 run 变化符合政策 |
| P1 | 按 §4.1.3 将普通 TODO 迁至 MAF，移除旧清单装配；明确各 profile 的 Todo 政策 | 实际 agent 只暴露一套普通清单工具；Explore/Plan 提示与有效工具一致，provider 工具权限有行为证据 |
| P1 | 验收 Todo Session 生命周期与宿主执行回归 | 原生工具写入 → Session 保存/重载后可见；独立 Session 隔离；压缩保留、清空/新建按政策重置；后台停止/输出、Worker 状态、Build 依赖与恢复不回归 |
| P1 | 覆盖审批批次的绑定/绕过语义（含 §5 版本边界） | 同一响应混有技能工具与普通工具时只拦需要审批的调用；「暂停→恢复」与「只回显响应」两条路径符合所引用版本语义；升级后按 §5 重跑 |
| P1 | 固定压缩唯一所有者与执行契约（先完成 §4.5 批次一 A1–A4） | 在一个 run 内多轮工具/模型调用触发压缩；状态恢复、工具结果配对、历史回调和计数通过；L0 移除后有同名同参数不同结果的反证测试 |
| P1 | 完成 §4.5 批次二 B1–B4：预算、折叠效果、Session 状态和告警口径 | 最终请求预算有证据；Todo 启用前通过历史定向失效与保存恢复验收；不把历史规模当模型输入压力 |
| P2 | 在 §4.5 批次一/二契约保护下评估 Harness 策略入口（C1） | 比较改前改后行为与复杂度；明确 stateKey 变化、显式 history 无 reducer、摘要不递归和唯一 Provider；允许保留当前装配。Skills Source 迁移不列入本轮任务 |
| P2 | 按 §4.6 将指令合成迁入 MAF，保留产品加载/覆盖/渲染 | 实际模型输入中通用片段仅一次且在主体前；公共 opt-out 不清空调用方配置；模板、覆盖与缺失失败策略保持，AutoDream 单独验证 |
| P2 | 验证各类搜索边界 | 每条路径提示与实际工具一致；hosted 与产品搜索不意外双挂 |

## 7. 验证范围与尚缺证据

2026-09-17 **前轮验证记录（不是 Todo、HarnessInstructions 或 Skills 新方案的验收）**：重跑筛选：`OneCodeHarnessDefaultsTests`、`AgentPipelineBuilderTests`、`TaskContextProviderTests`、`SkillProviderFactoryTests`、`MemorySearchProviderFactoryTests`、`DocFactConsistencyTests`，**49 通过、0 失败、0 跳过**（`dotnet test --no-build`，与上一轮数字一致）。`dotnet build src/OneCode.slnx` 通过（0 错误；66 警告均为历史警告）。仍未完成全量 `dotnet test` 与外部模型服务的 live 验证。

Todo、Memory、HarnessInstructions、Skills 与 Compaction 专项均仅修订文档，未改运行时代码、未重跑上述测试或新增集成测试。历史 49 项通过不能证明这些新方案已实现或通过验收；旧 opt-out、TaskContext 与提示词合成测试须随目标行为调整并补实际装配契约，而非仅修改期望值。前轮测试覆盖情况：

- 六个测试文件均存在。
- `OneCodeHarnessDefaultsTests` 四个用例分别断言 6 个 `Disable*` + 空指令、不覆盖 path-specific 字段（`DisableToolAutoApproval` / `MaximumIterationsPerRequest` / `Name`）、opt-in 字段保持 null、OTel 保持开启。
- `SkillProviderFactoryTests` 断言 provider 非 null、每次调用返回新实例、能广告 bundled skills、跳过非 SDK 后端客户端。
- `MemorySearchProviderFactoryTests` 只覆盖 `MemorySearchProviderFactory.SearchAsync`（内部委托）。

因此这些测试不能证明「生产 agent 实际工具不重复」，也不能证明 caller 永远不传 opt-in 配置；工厂返回非 null 不证明脚本执行或 MCP 正常；直接测搜索委托不等于测工厂产生的 MAF 工具。

后续测试优先走真实写入/实际 provider 工具路径，使用可控假 ChatClient，不依赖外网模型：

- 实际装配的 MAF `todos_*` 工具完成添加（含批量）、查询、完成和删除；清单上下文符合 invocation 生命周期，无旧 Task 清单重复注入。
- Todo 工具真实写入 → Session 序列化并经会话存储保存/重载 → 可见；另一独立 Session 不可见。只压缩消息仍保留 Todo，清空/新建按政策重置；不依赖从聊天文本猜测恢复清单。
- Worker/后台停止与输出、Build 依赖投影与恢复不回归；反证覆盖未完成依赖拒绝投影，Todo 完成不冒充宿主验收。
- Skills 双入口由真实技能文件驱动，覆盖同名优先级、文件布局、frontmatter 与参数渲染；共享技能身份/原始正文一致，有意的入口差异有反证测试。
- MAF 技能工具 → 文件 script runner，覆盖成功/非零退出/大输出/超时/取消；审批与只读政策保持，进程清理、输出上限与失败信息有可观察结果（§4.3.2）。
- Skills provider/source 释放覆盖完整使用期的所有退出路径，释放后共享 MCP 客户端仍可用，同 run 缓存及下次 run 新文件/连接变化分别验证。
- 审批批次：同一响应混有技能工具与普通工具时只拦需要审批的调用；「暂停 → 恢复」与「只回显响应」两条路径符合所引用版本的绑定语义（§5 版本边界）。
- 工厂产生 `search_memories` → 真实记忆写入端 → 命中反馈落盘 → 重新加载验证；取消不转为普通结果。
- 一次 run 中多次模型调用，观察压缩执行次数、历史内容与恢复，而不只检查属性值。
- 压缩专项 §4.5：通过真实 MAF 分组/Provider 捕获同参数不同结果（含 FAIL→PASS），验证删除 L0 后不再无条件排除；当前只有规则复算，尚无该 C# 用例的执行记录。
- Full/Partial compact 使用真实领域工具消息，验证摘要输入与替换区间一致、区间外 ID/内容/顺序不变、工具组完整；已有 Session 的取消/空摘要/超长摘要、Todo 保存恢复及最终请求预算分别覆盖。
- 实际 Harness 实例分别接收自定义 `HarnessInstructions` 与 `ChatOptions.Instructions`，用可控 ChatClient 捕获最终指令，断言前者只出现一次且后者按顺序追加；再覆盖生产 Main/fork/Team/AutoDream 装配，验证模板渲染、加载覆盖、缺失失败、空串抑制与无非预期默认文案（§4.6.3）。
- Main / fork / Team / Explore / Plan / AutoDream 的 Todo 与 FileMemory 启用政策分别与实际工具一致；需要能力的路径有且仅有一套原生工具，未启用路径不意外获得工具；mode/hosted 的既有边界保持，**FileMemory 改按 §4.2 新政策验收，不再要求全局关闭**。
- FileMemory 原生工具写入 → 生产 Session 保存/重载 → 新 Harness 实例读取；另一新 Session 不可见；compact 后 WorkingFolder 和文件仍可访问，旧历史不复活。
- 真实工作产物 → AutoDream 快照/候选核验 → 长期存储 → search_memories 检索；覆盖去重、重试、项目归属、人工条目保护、未确认偏好不晋升及清理顺序。
- 长期存储读取失败不得覆盖旧数据；并发自动/人工更新有守卫反证；内容更新反馈保留、取消与日志隐私均走真实边界（§4.2 M1–M5）。

这些是验收要求，不能把「尚未补测」直接描述成已复现的运行时 bug。

## 8. 删除本文的门槛

- [ ] P0 已修复，P1 的关键契约有真实行为测试；失败项有明确处理，不以更改期望值掩盖。
- [ ] 每项入口有最终决定：迁移，或记录保留理由；不要求所有 Disable 都消失。
- [ ] Skills 保留官方 provider/builder，双入口共享技能规则有统一政策和真实文件测试，用户专属调用语义不被静默删除；runner 超时、取消清理、输出限额及失败/日志边界通过实际工具路径验证。
- [ ] 每 run Skills provider/source 在完整使用期结束后由宿主释放，所有退出路径有测试；共享 MCP 客户端不被误释放，缓存和跨 run 更新行为符合政策。
- [ ] 只读 profile（Explore / Plan）的注入提示与其工具白名单一致，已确认的不一致项（§4.1）消除或有明确记录。
- [ ] 普通 TODO 已按 §4.1.3 迁至 MAF：旧清单工具职责、provider、注册与能力引用清理；旧数据处置明确；不再向 Task 存储写入普通 Todo，无双写。
- [ ] Todo 独立 Session 隔离、会话保存恢复、消息压缩保留与清空政策通过真实行为测试；后台执行与 Build 的依赖/恢复回归通过，不把清单完成当执行验收。
- [ ] HarnessInstructions 已按 §4.6 迁移：MAF 是通用片段与主体的唯一合成方，旧预拼接路径移除，公共 opt-out 不覆盖调用方指令；产品加载/覆盖/渲染与缺失失败策略保持，各路径最终指令有行为测试。
- [ ] 压缩 §4.5 A1–A4 已完成：L0 移除有真实行为反证；Full/Partial 区间与原子工具组契约通过；两路径摘要格式一致，取消/空摘要/无收益摘要状态保全通过。
- [ ] 压缩 §4.5 B1–B4、C1–C2 有结果：最终请求预算与无法达标政策明确；Todo 等非历史状态跨压缩保存恢复；告警不混淆指标；入口迁移或保留有明确决定并同步长期文档。
- [ ] 记忆 §4.2 M1–M5 已验收：按 profile 启用 Harness 内置 FileMemory，配置原生 store，无重复 provider/工具；同 Session 恢复与跨 Session 隔离、compact 保留、清空/归档/清理政策有行为证据。
- [ ] AutoDream 已消费工作产物并可核验来源；快照、去重、条件提交、失败重试及长期检索闭环通过；长期存储错误、取消、隐私、人工保护、反馈保留与预算均有结果。
- [ ] 已按 §1.3 逐批列出“删除 / 保留 / 新增”的实现及理由；已迁移职责只有一个所有者和一套运行路径，被替代的自定义实现及旧装配同步删除，不以关闭开关或注释代码代替清理，无双轨兼容层及无职责包装层。
- [ ] 被替代实现的引用链已清理：无未使用的接口/模型、DI 注册、能力引用、废弃配置或仅服务旧实现的依赖与测试辅助代码；共享组件的剩余调用方已核对，注释与文档同步，无失效类名和路径。
- [ ] 必要的产品行为测试已迁至新调用路径；旧实现专属测试已删除或改写，后台执行、Build、提示词加载、长期记忆治理及有意重置会话的行为未因精简误删。
- [ ] 本文中具体代码问题及资源问题均有结果，仍未知的点不得标为已收口。
- [ ] `dotnet build` 与全量 `dotnet test` 通过，外部服务未验证的限制如实记录。
- [ ] 稳定决策更新到 ADR 0007：**替换其中旧的“Todo 保留产品任务模型”结论**，明确原生普通 Todo 与宿主执行管理边界；同时替换旧指令合成决定为 §4.6 的 MAF 原生合成方案，并同步 PromptComposer 与 HarnessInstructions 的实际引用；记忆决定须替换为 §4.2 的「Harness 原生会话工作记忆 + OneCode 长期整理」，同步 ADR 0004 的旧会话记忆删除决定与新原生能力的区别、产物数据源和长期写入边界；其余补 Skills 所有权、profile 提示及审批版本边界。实施时全文同步 TaskContext、Task 工具与 opt-out 的文档/注释引用（含 ADR 0004、README、background-services 等实际引用），避免幽灵类名。Skills 的双入口规则、runner 执行边界及释放决定须同步 ADR 0007 与 skills 文档，并核查斜杠命令、脚本执行相关引用；记忆公开行为同步相关模块文档。本轮仅修订本文，长期文档与 ADR 尚待实施阶段同步。
- [ ] 将必要的版本/行为证据迁入长期文档和测试，并**区分 `dotnet-1.21.0` tag 与 checkout HEAD**（尤其审批绑定/bypass 与 agent 遥测）；全文移除本文链接后再删文件。

## 9. 源码证据索引

路径相对本文件，源码类名/成员是定位锚点；工作区根目录为 `e:\OneCode`。表中 `../../agent-framework/...` 的证据只在对应路径未被 `dotnet-1.21.0..HEAD` 改动时才可直接用于 1.21.0 行为；审批绑定/bypass 与遥测相关项须按 §5 的版本边界处理。

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
| [技能工厂](../../src/OneCode.App/Services/Skills/SkillProviderFactory.cs) | 官方 builder + 文件/内联/MCP；保留的薄装配 |
| [技能目录与用户入口](../../src/OneCode.App/Services/Skills/SkillCatalog.cs) / [技能命令源](../../src/OneCode.App/Commands/SkillCommandSource.cs) | 文件布局、后写覆盖、用户可调用筛选与参数渲染 |
| [文件技能发现](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Skills/File/AgentFileSkillsSource.cs) | MAF SKILL.md 发现与解析，与产品用户入口的规则差异 |
| [脚本 runner](../../src/OneCode.App/Skills/SubprocessScriptRunner.cs) | 进程启动、输出与错误处理；缺少显式超时、输出限额及取消进程终止逻辑 |
| [记忆检索工厂](../../src/OneCode.App/Services/Memory/MemorySearchProviderFactory.cs) | 官方 TextSearchProvider、委托、取消与日志 |
| [产品搜索](../../src/OneCode.App/Tools/WebSearchTool.cs) | AIFunction 后端链 |
| [PromptComposer](../../src/OneCode.App/Services/PromptComposer.cs) | ComposeMainAsync / ComposeWithRoleAsync：加载、主体渲染与记忆提示；Compose：拟移除的重复拼接 |
| [PromptManager](../../src/OneCode.Core/Prompt/PromptManager.cs) | 按存储优先级加载、模板渲染；不是 HarnessInstructions 的职责 |
| [通用提示词](../../src/OneCode.App/prompts/system/harness.prompt) / [主模板](../../src/OneCode.App/prompts/system/default.prompt) | 产品规则与主模板占位符；迁移合成位置不丢弃产品内容 |
| [AutoDream](../../src/OneCode.App/Services/AutoDream/AutoDreamService.cs) | 独立 Harness 构建，不等于 shared provider 全栈 |
| [Todo 默认 provider](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Harness/Todo/TodoProvider.cs) | 默认 provider 的真实所在包；session StateBag + 每会话锁、`todos_*` 五工具 |
| [Todo Options](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Harness/Todo/TodoProviderOptions.cs) | 指令与清单消息配置，没有依赖图或替换存储入口 |
| [会话存储](../../src/OneCode.App/Services/Agent/AgentSessionStore.cs) | Session 序列化/恢复与 conversation metadata；落盘由上游负责 |
| [会话失效](../../src/OneCode.App/Services/Compact/MafSessionInvalidator.cs) | 删除整个 mafSession；迁移需区分历史压缩与 Todo 重置 |
| [任务工具](../../src/OneCode.App/Tools/TaskTool.cs) | 无依赖输入；普通 update 走无依赖守卫的 UpdateTask；后台停止/输出职责 |
| [任务模型](../../src/OneCode.Core/Tasks/ITaskService.cs) / [任务服务](../../src/OneCode.Core/Tasks/TaskService.cs) | TaskItem 的 Blocks/BlockedBy；UpdateTask 与 ProjectTaskStatus 的守卫差异 |
| [任务存储](../../src/OneCode.Infrastructure/Tasks/JsonTaskStore.cs) | 当前混合任务快照，不是原生 Todo 恢复的必要条件 |
| [Worker 执行记录](../../src/OneCode.App/Services/Coordinator/WorkerAgentService.cs) | 子 Agent 调用状态和输出，而非其私有 Todo |
| [Goal 循环](../../src/OneCode.App/Services/Agent/GoalSubGoalLoop.cs) | 硬验证与语义评审，不直接消费 ITaskService |
| [Build 任务映射](../../src/OneCode.App/Services/BuildMode/BuildTaskLinker.cs) / [拓扑算法](../../src/OneCode.Core/Workflows/WorkflowTopology.cs) | 依赖映射、恢复、局部状态投影门禁；不等于完整并发调度的证据 |
| [FileMemory 默认 provider](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Harness/FileMemory/FileMemoryProvider.cs) | 七工具、描述/索引、实例锁；独立 provider 默认空 WorkingFolder；随机目录及默认 store 路径由 Harness 装配 |
| [FileMemory Session 状态](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Harness/FileMemory/FileMemoryState.cs) | WorkingFolder 相对于 store 根目录；正文不在 Session JSON |
| [长期记忆服务](../../src/OneCode.App/Services/Memory/MemoryService.cs) / [条目存储](../../src/OneCode.App/Services/Memory/MemoryEntryStore.cs) | 索引/排名、读写错误、目录解析、命中反馈与淘汰 |
| [AutoDream 数据源扫描](../../src/OneCode.App/Services/AutoDream/AutoDreamSessionScanner.cs) | 当前读取会话事件归属；FileMemory 产物消费链待实施 |
| [检索测试](../../src/OneCode.Tests/MemorySearchProviderFactoryTests.cs) / [存储测试](../../src/OneCode.Tests/MemoryEntryStoreTests.cs) | 当前委托降级/异常原文断言及存储用例；M1–M5 要求新增真实工具/跨存储边界验收 |
| [AgentMode 默认 provider](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Harness/AgentMode/AgentModeProvider.cs) | mode provider 与其 session 状态（LLM `mode_set` 那套状态的来源） |
| [TextSearchProvider](../../agent-framework/dotnet/src/Microsoft.Agents.AI/TextSearchProvider.cs) | 自身日志脱敏（`SanitizeLogData` / `Redactor`）、检索生命周期 |
| [TextSearchProviderOptions](../../agent-framework/dotnet/src/Microsoft.Agents.AI/TextSearchProviderOptions.cs) | SearchTime / FunctionToolName / ContextFormatter / RecentMessageMemoryLimit / 脱敏开关 |
| [Skills options](../../agent-framework/dotnet/src/Microsoft.Agents.AI/Skills/AgentSkillsProviderOptions.cs) | 三个 `Disable*Approval` 默认 false；`IncludeDetailedErrors` |
| [Approval binding](../../agent-framework/dotnet/src/Microsoft.Agents.AI/ChatClient/ApprovalResponseBindingChatClient.cs) | **版本敏感**：`dotnet-1.21.0` 与 HEAD 语义不同（§5）；同目录 `ApprovalRequirement.cs` 为 HEAD 新增 |
| [产品审批规则](../../src/OneCode.Infrastructure/Agent/AutoApprovalRulesFactory.cs) | 仅放行 `ReadOnlyToolsAutoApprovalRule` + `PermissionProfiles.Check` 的 Allow |
| [Profile 能力集](../../src/OneCode.Infrastructure/Agent/PipelineProfile.cs) | Explore/Plan 的减法清单（`TaskContext` 未移除）与 `ReadOnlyAgentTools` 白名单 |
| [上下文入口](../../src/OneCode.App/Services/Agent/AgentContextPipeline.cs) | shared（Worker/Explore/Plan/TeamMember）与 Main 两条装配入口 |
