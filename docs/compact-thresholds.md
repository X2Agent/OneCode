# 上下文压缩阈值说明（Compact Thresholds）

自动上下文压缩由 MAF in-pipeline（`CompactionProvider`）完成，按**模型上下文窗口比例**计算阈值。显式 `/compact` 仍由 App 层管理产品历史。`AutoCompactService` 不执行压缩，仅在 turn 结束后提供 **0.70 历史规模告警**；其完整历史估算与模型实际输入不是同一口径，不能称为压缩前预警。

---

## 1. MAF in-pipeline 压缩（CompactionPipelineBuilder）

**所在层**：`OneCode.Infrastructure.Agent.CompactionPipelineBuilder`（产品策略由 App 层 `CompactionStrategyFactory` 构建，经 `HarnessAgentOptions.CompactionStrategy` 交给 Harness 挂载唯一 provider）
**注入方式**：由 Harness 在 `HarnessAgent.BuildInnerAgent` 中经 `AsBuilder().UseAIContextProviders(CompactionProvider)` 注入到 ChatClient 管道，而非 agent-level context provider。产品侧不自行构建 provider——两处各挂一份会让同一次模型调用被压缩两次。
**持久化**：压缩状态自动持久化在 `AgentSession.StateBag` 中，由 `AgentSessionPersistence` 的 mafSession 持久化机制携带，支持跨进程恢复。
**生效范围**：一个会话里压缩只在**第一次服务调用**上执行，之后跳过，直到历史发生结构性改动。`HarnessAgent` 强制 `RequirePerServiceCallChatHistoryPersistence = true`，该装饰器在框架自管历史的路径上把哨兵值 `_agent_local_chat_history` 写入 `ChatClientAgentSession.ConversationId`；`CompactionProvider` 把「`ConversationId` 非空」解读为「历史由远端服务托管」并整个跳过压缩。`ConversationId` 的 setter 是 internal 且拒绝空值，产品侧无法在内存里复位，只能从持久化快照剔除：`MafSessionInvalidator` 在 `/compact`（full / partial）与 `/checkpoint restore` 后定向剔除哨兵，因此**每次结构性历史改动都会重新武装一次压缩**。这是框架现状而非产品可配置项，由 [`HarnessCompactionActivationTests`](../../src/OneCode.Tests/HarnessCompactionActivationTests.cs)（框架行为）与 [`MafSessionInvalidatorTests`](../../src/OneCode.Tests/MafSessionInvalidatorTests.cs)（剔除定向性）守卫。因此长会话的上下文控制实际由两条腿承担：首次调用的压缩 + 0.70 告警提示用户执行 `/compact`。

### 阈值计算

所有阈值基于 `inputBudget = (maxContextWindowTokens - maxOutputTokens) × (1 − RequestOverheadRatio)` 按比例计算，
其中 `RequestOverheadRatio = 0.10` 为非消息开销（instructions、工具 schema、协议框架）预留。
非法配置（窗口或输出预留非正、输出预留 ≥ 窗口）由 `CompactionPipelineBuilder` 在装配时抛 `ArgumentOutOfRangeException`，
不再钳制为 1——钳制会让三层阈值全部归零，压缩看起来「已装配」却永不触发。

| 配置方法 | 角色 | ToolResult 折叠 | LLM 摘要 | 截断兜底 |
|----------|------|----------------|----------|----------|
| `BuildForMainAgent` | 主 Agent | 0.50 | 0.70 | 0.85 |
| `BuildForWorkerAgent` | Worker / Forked / Team 子 Agent | 0.40 | 0.60 | 0.80 |

- Worker 阈值更激进：子 Agent 上下文更短、生命周期更短。
- 模型上下文窗口与最大输出 token 由 `CompactionStrategyFactory` 从 `IModelManager` 解析（主 Agent 输出上限缺省 8,192，Worker 缺省 4,096，可被 `maxOutputTokensOverride` 覆盖）。
  窗口按守卫式读取：`0` 视作「未解析出窗口」，回落到 catalog / 128,000 默认值，不会作为有效窗口传给严格校验。
- 输出预留仅对**本地 Ollama** 收敛为 `min(缺省值, 窗口 / 4)`：本地小窗口（4K~8K）装不下固定 8,192，而预留 ≥ 窗口会被装配校验直接拒绝。窗口 ≥ 32,768 时结果与固定预留一致；其余 provider 保持原值，云端预算不受影响。

### 策略升级顺序（`PipelineCompactionStrategy`，逐级触发）

| 层 | 策略 | 触发 | 行为 | LLM |
|----|------|------|------|-----|
| L1 | `ToolResultCompactionStrategy`（经 `ToolCallFormatter` 槽位接入产品 `OneCodeToolCallFormatter`） | TokensExceed(50% / 40% inputBudget) | 保留最近 2 组；折叠旧 tool call 组为有界 YAML 摘要——保留调用参数（200 字符上限）与结果正文（800 字符上限，超出标注截断） | 否 |
| L2 | `SummarizationCompactionStrategy`（外层 `GuardedSummarizationCompactionStrategy`） | TokensExceed(70% / 60% inputBudget) | 保留最近 8 组；LLM 摘要。守卫拒绝空白摘要与「比原文更长」的摘要并回滚索引；原生普通异常恢复 excluded groups | 是 |
| L3 | `TruncationCompactionStrategy` | TokensExceed(85% / 80% inputBudget) | 截断最旧非系统组，本层保护最近 2 组；不继承 L2 的 8 组保护 | 否 |

各层依据前一层处理后的 included groups 重算，不按原始使用率一次选档。默认 Target 是 Trigger 的反条件；摘要插入后的净收益及最终预算不是原生 L2 的硬保证。

> 摘要 prompt 经 `CompactPromptBuilder` 统一加载（`system/compact`，所有存储均缺失时 fail-fast）。两条路径共用同一契约：模型直接输出工作摘要，无 `<analysis>`/`<summary>` 包裹；自动 L2 的输出上限由 `SummarizationOutputLimitChatClient` 补齐，与显式 `/compact` 的 8,192 一致。

**计数限制**：当前 Provider 未传 tokenizer，索引按消息内容 UTF-8 字节数/4 估算；独立 instructions、工具 schema 和协议开销未完整计入。比例阈值不保证最终请求不超窗；保护尾部或系统消息过大也可能无法达标。

---

## 2. App 层告警（AutoCompactService）

**所在层**：`OneCode.App.Services.Compact.AutoCompactService`
**触发时机**：`QueryStreamService` 在每次 agent turn 结束后调用 `CheckAndWarnAsync`。

| 常量 | 值 | 含义 |
|------|----|------|
| `WarningThreshold` | 0.70 | 首次跨越时设置告警标志 |
| `MaxTrackedSessions` | 100 | 会话告警状态缓存上限（超过后清理 1 小时前的陈旧条目） |

- 使用率基于 `TokenBudget.Estimate`：`UsageRatio = EstimatedInputTokens / MaxInputTokens`，其中 `MaxInputTokens = MaxContextTokens − 输出预留`。预留与压缩装配同源解析（本地 Ollama 为 `min(8,192, 窗口 / 4)`，其余 provider 固定 8,192），避免出现「压缩按窗口比例正常工作、`/status` 却按固定预留持续告警」的矛盾。
- `ConsumeWarning` 消费未读告警后由 `QueryStreamService` 发射 `TuiCompactSuggested` 事件；使用率降回 0.70 以下后标志重置，可再次告警。
- Worker 进程（`ONECODE_IS_WORKER=1/true`）跳过告警检查。

---

## 3. 两者对比

| 维度 | MAF in-pipeline（CompactionProvider） | App 层（AutoCompactService） |
|------|---------------------------------------|------------------------------|
| 所在层 | Infrastructure / App（Builder） | App |
| 触发时机 | 会话第一次服务调用（pipeline 中，`CompactionProvider` 在模型调用前执行）；此后因哨兵值被写入 `ConversationId` 而跳过，直到结构性历史改动（`/compact`、`/checkpoint restore`）剔除哨兵后重新武装 | agent turn 结束后 |
| 触发阈值 | 按上下文窗口比例（Main 0.5/0.7/0.85，Worker 0.4/0.6/0.8，逐层按剩余 groups 重算，严格 `>`） | 0.70 历史规模告警（不压缩） |
| 是否调用 LLM | L2 Summarization 调用 | 否 |
| 用户感知 | 无（静默压缩） | 0.70 时提示执行 `/compact`（历史规模提示，非模型输入口径） |
| 配置 | 零配置（比例自适应） | 零配置 |

---

## 4. 设计说明：为什么用比例阈值

阈值全部按 `inputBudget` 比例计算，而不是写死绝对 token 数，理由有两条：

1. **绝对阈值无法适配模型差异**——同一个数字对 32K 小窗模型永远触发不了，对 1M 窗模型又触发过早；比例阈值随每个模型自己的窗口与输出预留推算。
2. **压缩只有一个所有者**——自动压缩由 MAF in-pipeline 的 `CompactionProvider` 执行，`HarnessAgentOptions.CompactionStrategy` 是产品唯一的接入点；产品侧再挂一份 provider 会让同一次模型调用被压缩两次。

App 层保留 MAF 不具备的产品编排：显式 `/compact`（指定范围、边界标记、Conversation 落盘与 hooks），以及 0.70 历史规模告警。MAF 另提供 `CompactionProvider.CompactAsync` 与 `CompactionStrategy.AsChatReducer`，并非没有历史压缩能力。

0.70 通知在 turn 结束后按完整历史计算，可能晚于已发生的静默压缩，因此定位为历史规模提示，不与模型输入预算严格对齐（压缩所有权边界见 [ADR 0007](adr/0007-maf-integration-boundaries.md)）。

> 显式 `/compact` 命令（用户主动触发）始终可用，不受阈值控制。
