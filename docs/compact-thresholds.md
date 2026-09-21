# 上下文压缩阈值说明（Compact Thresholds）

自动上下文压缩由 MAF in-pipeline（`CompactionProvider`）完成，按**模型上下文窗口比例**计算阈值。显式 `/compact` 仍由 App 层管理产品历史。`AutoCompactService` 不执行压缩，仅在 turn 结束后提供 **0.70 历史规模告警**；其完整历史估算与模型实际输入不是同一口径，不能称为压缩前预警。

---

## 1. MAF in-pipeline 压缩（CompactionPipelineBuilder）

**所在层**：`OneCode.Infrastructure.Agent.CompactionPipelineBuilder`（产品策略由 App 层 `CompactionStrategyFactory` 构建，经 `HarnessAgentOptions.CompactionStrategy` 交给 Harness 挂载唯一 provider）
**注入方式**：由 Harness 在 `HarnessAgent.BuildInnerAgent` 中经 `AsBuilder().UseAIContextProviders(CompactionProvider)` 注入到 ChatClient 管道，而非 agent-level context provider。产品侧不自行构建 provider——两处各挂一份会让同一次模型调用被压缩两次。
**持久化**：压缩状态自动持久化在 `AgentSession.StateBag` 中，由 `AgentSessionStore` 的 mafSession 持久化机制携带，支持跨进程恢复。

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

### 策略升级顺序（`PipelineCompactionStrategy`，逐级触发）

| 层 | 策略 | 触发 | 行为 | LLM |
|----|------|------|------|-----|
| L1 | `ToolResultCompactionStrategy`（产品 `ToolCallFormatter`） | TokensExceed(50% / 40% inputBudget) | 保留最近 2 组；折叠旧 tool call 组为有界 YAML 摘要——保留调用参数（200 字符上限）与结果正文（800 字符上限，超出标注截断） | 否 |
| L2 | `SummarizationCompactionStrategy`（外层 `GuardedSummarizationCompactionStrategy`） | TokensExceed(70% / 60% inputBudget) | 保留最近 8 组；LLM 摘要。守卫拒绝空白摘要与「比原文更长」的摘要并回滚索引；原生普通异常恢复 excluded groups | 是 |
| L3 | `TruncationCompactionStrategy` | TokensExceed(85% / 80% inputBudget) | 截断最旧非系统组，本层保护最近 2 组；不继承 L2 的 8 组保护 | 否 |

各层依据前一层处理后的 included groups 重算，不按原始使用率一次选档。默认 Target 是 Trigger 的反条件；摘要插入后的净收益及最终预算不是原生 L2 的硬保证。

> 摘要 prompt 经 `CompactPromptBuilder` 统一加载（`system/compact`，所有存储均缺失时 fail-fast）。两条路径共用同一契约：模型直接输出工作摘要，无 `<analysis>`/`<summary>` 包裹；自动 L2 的输出上限由 `SummarizationOutputLimitChatClient` 补齐，与显式 `/compact` 的 8,192 一致。

**计数限制**：当前 Provider 未传 tokenizer，索引按消息内容 UTF-8 字节数/4估算；独立 instructions、工具 schema 和协议开销未完整计入。比例阈值不保证最终请求不超窗；保护尾部或系统消息过大也可能无法达标。非法预算（非正数或输出预留 ≥ 窗口）由 `CompactionPipelineBuilder` 在装配时显式拒绝，并按 `RequestOverheadRatio` 预留 10% 给非消息开销。

---

## 2. App 层告警（AutoCompactService）

**所在层**：`OneCode.App.Services.Compact.AutoCompactService`
**触发时机**：`QueryStreamService` 在每次 agent turn 结束后调用 `CheckAndWarnAsync`。

| 常量 | 值 | 含义 |
|------|----|------|
| `WarningThreshold` | 0.70 | 首次跨越时设置告警标志 |
| `MaxTrackedSessions` | 100 | 会话告警状态缓存上限（超过后清理 1 小时前的陈旧条目） |

- 使用率基于 `TokenBudget.Estimate`：`UsageRatio = EstimatedInputTokens / MaxInputTokens`，其中 `MaxInputTokens = MaxContextTokens − ReservedOutputTokens(8,192)`。
- `ConsumeWarning` 消费未读告警后由 `QueryStreamService` 发射 `TuiCompactSuggested` 事件；使用率降回 0.70 以下后标志重置，可再次告警。
- Worker 进程（`ONECODE_IS_WORKER=1/true`）跳过告警检查。

---

## 3. 两者对比

| 维度 | MAF in-pipeline（CompactionProvider） | App 层（AutoCompactService） |
|------|---------------------------------------|------------------------------|
| 所在层 | Infrastructure / App（Builder） | App |
| 触发时机 | agent turn 内（pipeline 中，每次模型调用前） | agent turn 结束后 |
| 触发阈值 | 按上下文窗口比例（Main 0.5/0.7/0.85，Worker 0.4/0.6/0.8，逐层按剩余 groups 重算，严格 `>`） | 0.70 历史规模告警（不压缩） |
| 是否调用 LLM | L2 Summarization 调用 | 否 |
| 用户感知 | 无（静默压缩） | 0.70 时提示执行 `/compact`（历史规模提示，非模型输入口径） |
| 配置 | 零配置（比例自适应） | 零配置 |

---

## 4. 设计说明：从绝对阈值到比例阈值

早期版本使用两套机制：in-pipeline 绝对 token 阈值（主 Agent 120K/80K、Worker 60K/40K、`BuildAggressive` 30K/20K）+ App 层三档压缩（0.85 轻压 / 0.95 Full LLM 压缩 / 熔断退避）与 `AppAutoFullCompactEnabled` 配置项。该设计的问题：

1. **绝对阈值无法适配模型差异**——32K 小窗模型永远触发不了 120K 阈值，1M 窗模型又触发过早。
2. **两套压缩重复**——App 层 Layer-0/1/2 与 in-pipeline 策略能力重叠，且 `AppAutoFullCompactEnabled` 二值开关把"何时压缩"和"是否压缩"混为一谈。

现行设计：

- **自动压缩收敛到 MAF in-pipeline 单一机制**，阈值全部按 `inputBudget` 比例计算，`BuildAggressive` 与 App 层三档压缩、熔断退避全部移除。
- **App 层保留 MAF 没有的产品编排**：显式 `/compact`（指定范围、边界标记、Conversation 落盘与 hooks），以及 0.70 历史规模告警。MAF 本身提供 `CompactionProvider.CompactAsync` 与 `AsChatReducer`，并非没有历史压缩能力。
- 0.70 通知在 turn 结束后按完整历史计算，可能晚于已发生的静默压缩；已重新定位为历史规模提示；不与模型输入预算严格对齐（2026-09-18 压缩批次二决定：先纠正标签、不新增一套自动压缩，见 ADR 0007）。

> 显式 `/compact` 命令（用户主动触发）始终可用，不受阈值控制。
