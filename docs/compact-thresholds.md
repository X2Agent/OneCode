# 上下文压缩阈值说明（Compact Thresholds）

自动上下文压缩由 MAF in-pipeline（`CompactionProvider`）完成，按**模型上下文窗口比例**计算阈值。显式 `/compact` 仍由 App 层管理产品历史。`AutoCompactService` 不执行压缩，仅在 turn 结束后提供 **0.70 历史规模告警**；其完整历史估算与模型实际输入不是同一口径，不能称为压缩前预警。

> **现状与计划分开**：本文描述当前运行时代码。已发现的缺陷及重构目标见 [Harness 审计 §4.5](plan/harness-defaults-replacement-audit.md#45-compaction先修正确性与状态边界再评估装配入口)。计划移除 L0、修复手动压缩边界与统一预算，但尚未实施，下面仍保留当前策略表。

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
| L0 | `SnipDuplicateCallsCompactionStrategy` | Always（策略基类守卫通过时） | 最近 10 个非系统组不参与判重；其余同名同参数调用组仅保留最后一次，不比较结果（自定义，计划移除） | 否 |
| L1 | `ToolResultCompactionStrategy` | > 0.50（Main）/ 0.40（Worker）对应 token 阈值 | 合并旧调用组为 YAML-like 文本；默认保留结果正文但不保留调用参数，无正文长度上限；保护最近 2 组 | 否 |
| L2 | `SummarizationCompactionStrategy` | > 0.70（Main）/ 0.60（Worker）对应 token 阈值 | LLM 摘要；普通异常恢复 excluded groups，取消不走该恢复分支，空响应以占位文本替换；本层保护最近 8 组 | 是 |
| L3 | `TruncationCompactionStrategy` | > 0.85（Main）/ 0.80（Worker）对应 token 阈值 | 截断最旧非系统组，本层保护最近 2 组；不继承 L2 的 8 组保护 | 否 |

各层依据前一层处理后的 included groups 重算，不按原始使用率一次选档。默认 Target 是 Trigger 的反条件；摘要插入后的净收益及最终预算不是原生 L2 的硬保证。

> 摘要 prompt 经 `CompactPromptBuilder` 统一加载（`system/compact`，所有存储均缺失时 fail-fast，无此处所称的内置兜底）。当前手动路径还进行格式后处理，自动 L2 直接保存模型文本；统一契约属于待实施重构。

**计数限制**：当前 Provider 未传 tokenizer，索引按消息内容 UTF-8 字节数/4估算；独立 instructions、工具 schema 和协议开销未完整计入。比例阈值不保证最终请求不超窗；保护尾部或系统消息过大也可能无法达标。非法预算当前被 `max(1, …)` 钳制，改为显式校验属于待实施计划。

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
- 0.70 通知在 turn 结束后按完整历史计算，可能晚于已发生的静默压缩；重新定位为历史规模提示。把通知与模型输入预算严格对齐属于待实施计划（审计 §4.5 批次二）。

> 显式 `/compact` 命令（用户主动触发）始终可用，不受阈值控制。
