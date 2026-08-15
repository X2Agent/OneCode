# 上下文压缩阈值说明（Compact Thresholds）

上下文压缩统一由 MAF in-pipeline（`CompactionProvider`）自动完成，按**模型上下文窗口比例**计算阈值，自动适配不同模型（32K ~ 1M+）。App 层 `AutoCompactService` 不再执行任何压缩动作，仅保留 **0.70 告警**（提醒用户执行 `/compact`）——这是 MAF 没有的能力（MAF 只在 token 超阈值时静默压缩，不会提前提醒用户）。

---

## 1. MAF in-pipeline 压缩（CompactionPipelineBuilder）

**所在层**：`OneCode.Infrastructure.Agent.CompactionPipelineBuilder`（构建入口为 App 层 `CompactionProviderBuilder`）
**注入方式**：通过 IChatClient builder 层注入（`AsBuilder().UseAIContextProviders(...)`），而非 agent-level context provider。
**持久化**：压缩状态自动持久化在 `AgentSession.StateBag` 中，由 `AgentSessionStore` 的 mafSession 持久化机制携带，支持跨进程恢复。

### 阈值计算

所有阈值基于 `inputBudget = max(1, maxContextWindowTokens - maxOutputTokens)` 按比例计算：

| 配置方法 | 角色 | ToolResult 折叠 | LLM 摘要 | 截断兜底 |
|----------|------|----------------|----------|----------|
| `BuildForMainAgent` | 主 Agent | 0.50 | 0.70 | 0.85 |
| `BuildForWorkerAgent` | Worker / Forked / Team 子 Agent | 0.40 | 0.60 | 0.80 |

- Worker 阈值更激进：子 Agent 上下文更短、生命周期更短。
- 模型上下文窗口与最大输出 token 由 `CompactionProviderBuilder` 从 `IModelManager` 解析（主 Agent 输出上限缺省 8,192，Worker 缺省 4,096，可被 `maxOutputTokensOverride` 覆盖）。

### 策略升级顺序（`PipelineCompactionStrategy`，逐级触发）

| 层 | 策略 | 触发 | 行为 | LLM |
|----|------|------|------|-----|
| L0 | `SnipDuplicateCallsCompactionStrategy` | 压缩轮次执行时 | 移除重复的 `(toolName, args)` 调用组，只保留最近一次（项目自定义策略） | 否 |
| L1 | `ToolResultCompactionStrategy` | ≥ 0.50（Main）/ 0.40（Worker） | 折叠旧 tool call 组为 YAML 摘要，保留最近 2 组 | 否 |
| L2 | `SummarizationCompactionStrategy` | ≥ 0.70（Main）/ 0.60（Worker） | LLM 深度摘要；失败时自动恢复 excluded groups，保留最近 8 组硬下限 | 是 |
| L3 | `TruncationCompactionStrategy` | ≥ 0.85（Main）/ 0.80（Worker） | 兜底截断最旧的非系统消息组，保留最近 2 组 | 否 |

> 摘要 prompt 经 `CompactPromptBuilder` 统一加载（system/compact + 内置兜底）。

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
| 触发时机 | agent turn 内（pipeline 中） | agent turn 结束后 |
| 触发阈值 | 按上下文窗口比例（Main 0.5/0.7/0.85，Worker 0.4/0.6/0.8） | 0.70 告警（不压缩） |
| 是否调用 LLM | L2 Summarization 调用 | 否 |
| 用户感知 | 无（静默压缩） | 0.70 时提示执行 `/compact` |
| 配置 | 零配置（比例自适应） | 零配置 |

---

## 4. 设计说明：从绝对阈值到比例阈值

早期版本使用两套机制：in-pipeline 绝对 token 阈值（主 Agent 120K/80K、Worker 60K/40K、`BuildAggressive` 30K/20K）+ App 层三档压缩（0.85 轻压 / 0.95 Full LLM 压缩 / 熔断退避）与 `AppAutoFullCompactEnabled` 配置项。该设计的问题：

1. **绝对阈值无法适配模型差异**——32K 小窗模型永远触发不了 120K 阈值，1M 窗模型又触发过早。
2. **两套压缩重复**——App 层 Layer-0/1/2 与 in-pipeline 策略能力重叠，且 `AppAutoFullCompactEnabled` 二值开关把"何时压缩"和"是否压缩"混为一谈。

现行设计：

- **压缩收敛到 MAF in-pipeline 单一机制**，阈值全部按 `inputBudget` 比例计算，`BuildAggressive` 与 App 层三档压缩、熔断退避全部移除。
- **App 层只保留 MAF 没有的能力**——0.70 提前告警，让用户在压缩前有感知、可主动 `/compact`。

> 显式 `/compact` 命令（用户主动触发）始终可用，不受阈值控制。
