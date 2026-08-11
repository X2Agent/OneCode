# 上下文压缩阈值说明（Compact Thresholds）

OneCode 同时存在两套上下文压缩系统，二者按不同阈值、不同时机触发，互为兜底。本文档记录各自的阈值与触发条件。

App 层自动压缩采用**三档阈值驱动**（零配置，阈值即语义）：
- `0.70` 告警提醒用户 → `0.85` 无 LLM 轻压 → `0.95` Full LLM 压缩。

---

## 1. MAF in-pipeline 压缩（CompactionProvider）

**所在层**：`OneCode.Infrastructure.Agent.CompactionPipelineBuilder`
**触发时机**：每次 agent turn 内，由 MAF `CompactionProvider` 在 `IChatClient` pipeline 中自动执行，不修改持久化的消息历史。
**触发条件**：当前上下文 token 数超过 `exceedTokens` 阈值时触发，压缩至 `targetTokens` 以下停止。

按 agent 角色区分阈值（见 `CompactionPipelineBuilder`）：

| 配置方法 | 角色 | exceedTokens（触发） | targetTokens（目标） |
|----------|------|----------------------|----------------------|
| `BuildForMainAgent` | 主 Agent | 120,000 | 80,000 |
| `BuildForWorkerAgent` | Worker Agent | 60,000 | 40,000 |
| `BuildAggressive` | Prompt-too-long 恢复 | 30,000 | 20,000 |

**策略升级顺序**（`PipelineCompactionStrategy`，逐层尝试直至 token 降至目标以下）：

1. `ToolResultCompactionStrategy` — 清理旧 tool result 内容
2. `SummarizationCompactionStrategy` — LLM 摘要（调用 `IChatClient`）
3. `SlidingWindowCompactionStrategy` — 滑动窗口裁剪
4. `TruncationCompactionStrategy` — 截断

> in-pipeline 压缩**不修改持久化消息历史**，仅在当次 agent 调用的上下文窗口内生效。

---

## 2. App 层 Full 自动压缩（AutoCompactService）

**所在层**：`OneCode.App.Services.Compact.AutoCompactService`
**触发时机**：`QueryStreamService` 在每次 agent turn 结束后调用 `CheckAndAutoCompactAsync`。
**设计**：三档阈值驱动，零配置。

### 三档阈值

| 常量 | 值 | 行为 | LLM 成本 |
|------|----|------|----------|
| `WarningThreshold` | 0.70 | 首次跨越时发射 `TuiCompactSuggested` 事件，提醒用户执行 `/compact` | 0 |
| `SafeCompactThreshold` | 0.85 | 触发 Layer-0 MicroCompact + Layer-1 SnipCompact | 0 |
| `CriticalThreshold` | 0.95 | 升级到 Layer-2 Full LLM 压缩（替换持久化消息历史） | 1 次 |

### 其他阈值常量

| 常量 | 值 | 含义 |
|------|----|------|
| `CompactThresholdBuffer` | 13,000 | 绝对值兜底缓冲（MaxInputTokens − 13K） |
| `MaxSequentialCompactions` | 3 | 熔断器最大连续压缩次数 |
| `CooldownPeriod` | 30 s | 熔断冷却基础时长 |
| `BackoffBase` | 60 s | 指数退避基础时长 |

### 压缩升级顺序（`CheckAndAutoCompactAsync`）

0. **70% 告警检查** — `CheckAndWarn` 设置 `HasUnconsumedWarning` 标志，`ConsumeWarning` 消费后由 `QueryStreamService` 发射 `TuiCompactSuggested` 事件
1. **Layer-0 MicroCompact** — 清理旧 tool result 内容（同步、无 LLM）
2. **Layer-1 SnipCompact** — 移除重复 `(tool, args)` call+result 对（无 LLM）
3. **Layer-2 Full LLM compact** — 仅当 Layer-0/1 执行后仍超过 `CriticalThreshold`（0.95）才升级，调用 `CompactService.CompactAsync` 生成摘要并**替换持久化消息历史**

每层执行后会重新评估 `usageRatio`，若已降至 `SafeCompactThreshold` 以下则提前返回，不再升级。Layer-2 额外要求 Layer-0/1 后仍超过 `CriticalThreshold` 才触发。

> 注意：`TokenBudget` 中另有 `AutoCompactThreshold = 0.80`（用于 `ShouldCompact` 静态判断）与 `ReservedOutputTokens = 8,192`（预留输出空间）。`MaxInputTokens` = `MaxContextTokens - 8,192`。

---

## 3. 两者对比

| 维度 | MAF in-pipeline（CompactionProvider） | App 层 Full（AutoCompactService） |
|------|---------------------------------------|-----------------------------------|
| 所在层 | Infrastructure | App |
| 触发时机 | agent turn **内**（pipeline 中） | agent turn **结束后** |
| 触发阈值 | 绝对 token 数（主 Agent 120K / Worker 60K / 激进 30K） | 三档：0.70 告警 / 0.85 轻压 / 0.95 Full LLM |
| 是否修改持久化历史 | 否（仅当次上下文窗口） | 是（Layer-2 替换消息历史为摘要） |
| 是否调用 LLM | 策略 2 Summarization 调用 | 仅 Layer-2 调用 |
| 熔断/退避 | 无 | 有（3 次熔断 + 指数退避 + 30 s 冷却） |
| 配置 | 始终启用（in-pipeline 兜底） | 零配置（阈值驱动） |

---

## 4. 设计说明：三档阈值替代二值开关

早期版本通过 `AppAutoFullCompactEnabled` 配置项（默认 `false`）控制 Layer-2 是否自动触发。该设计存在两个问题：

1. **二值开关粒度过粗**——把"何时压缩"和"是否压缩"混为一谈，用一个全有/全无的开关替代了本该由阈值驱动的决策
2. **70% 告警是死代码**——`GetWarningState` 从未有消费者，用户无法收到主动提醒

新设计移除配置项，改为三档阈值驱动：

- **0.70**：激活告警通道，`ConsumeWarning` + `TuiCompactSuggested` 事件让用户在压缩前收到提醒
- **0.85**：Layer-0/1 无 LLM 轻压，与 in-pipeline 不冲突，始终保留
- **0.95**：Layer-2 Full LLM 压缩，仅在临界点触发，避免与 in-pipeline 频繁双打

### 为什么 0.95 比 0.85 更合理

- 0.85 时 in-pipeline（120K 触发）已经工作了一段时间，还能兜底
- 0.95 说明 in-pipeline + Layer-0/1 都压不住了，持久化历史实在太大，必须 Layer-2 介入
- 此时双打成本可接受——因为不压就要爆了

> 显式 `/compact` 命令（用户主动触发）始终可用，不受阈值控制。
