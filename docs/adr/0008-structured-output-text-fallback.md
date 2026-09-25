# 结构化请求 + 文本降级

**状态**: Accepted
**日期**: 2026-09-21
**关联**: [MAF 集成边界与禁止清单](./0007-maf-integration-boundaries.md)、[MAF C# 接入指引 §2.4](../maf/integration-guide.md)、[结构化调用入口](../../src/OneCode.App/Services/StructuredChatCall.cs)

## 语境

OneCode 有三处「模型输出 → JSON」消费点：

| 位置 | 形态 | 类型 |
|---|---|---|
| `Services/Agent/GoalDecomposer.cs` | 直连 chat client（decompose / replan / sub-decompose 三个调用点） | `GoalPlan` |
| `Services/ClarificationQuestionGenerator.cs` | 直连 chat client | `RequirementIntake` |
| `Services/AutoDream/AutoDreamService.cs` | **带 Read/Glob/Grep 工具循环的 Agent** + 会话 | `List<ConsolidationChange>` |

三处此前都是手搓的 prompt-only JSON：提示词内写 JSON 契约，模型返回后用手写提取器
（`ExtractJsonBlock` / `ExtractJsonObject` / `ExtractJsonArray`）+ 围栏容错反序列化。
最初的决策记录在 `GoalDecomposer.CreateStructuredChatOptions` 注释里：**部分 OpenAI 兼容网关拒绝所有
`response_format` 变体**，prompt-only 是可移植基线。

MAF / M.E.AI 提供结构化输出能力（`AIAgent.RunAsync<T>`、`IChatClient.GetResponseAsync<T>`、
`AgentRunOptions.ResponseFormat` → `ChatOptions.ResponseFormat`），[接入指引 §2.4](../maf/integration-guide.md)
早已记载该能力，但产品侧没有采用记录；`DecomposeSubGoalAsync` 的 `modelId` 参数注释在当时还残留
「用于按模型缓存结构化输出兼容性」——该缓存从未实现，属于失效文档（本 ADR 落地时已改为说明
response_format 能力协商在 `StructuredChatCall` 内每次调用完成）。

## 决策

直连 chat client 的轻量调用采用 **「结构化请求 + 文本降级」** 三段式，统一入口
`Services/StructuredChatCall.cs`（`CallAsync<T>` 返回 `StructuredChatResult<T>`）：

```
① 结构化请求：GetResponseAsync<T>(useJsonSchemaResponseFormat: true) → TryGetResult 成功 → 类型化结果
② 同响应文本降级：TryGetResult 失败（provider 忽略 schema / 输出带围栏）→ 调用方既有文本解析器，不重试
③ 硬拒绝重试：结构化调用抛非取消异常 → 去掉 ResponseFormat 重试一次 → 再走 ②
```

首批落地：`GoalDecomposer`（3 个调用点）、`ClarificationQuestionGenerator`。
现有文本解析器全部保留为降级路径——它们是兼容层，不是死代码。

**源码取证（决定实现细节，均已核对）**：

1. `useJsonSchemaResponseFormat: false` **仍会发送** `response_format: json_object`
   （M.E.AI `ChatClientStructuredOutputExtensions`），对拒绝所有变体的网关依然被拒；
   因此③的完整重试必须用**不带任何 `ResponseFormat` 的裸调用**。
2. `ChatResponse<T>.TryGetResult` 失败不抛异常、取末条消息文本、容忍多顶层 JSON、**不剥 markdown 围栏**
   ——与 OneCode 的围栏解析器正好互补，② 零成本。
3. 非对象 `T`（数组等）由框架自动包 `{ "data": … }` 对象壳并还原（`IsWrappedInObject`）。
4. `GetResponseAsync<T>` 内部调用 `serializerOptions.MakeReadOnly()`：未显式指定 `TypeInfoResolver`
   的 `JsonSerializerOptions` 会在那里抛 `InvalidOperationException`，且会被③的宽捕获吞掉、
   **伪装成「网关拒绝」并静默降级**。`StructuredChatCall` 入口以 fail-fast 守卫封堵该陷阱；
   两个调用点的 `JsonOptions` 均已显式声明解析器。

**异常过滤取「所有非取消异常」**：各 provider 的 400 拒绝没有统一异常类型（OpenAI 为
`ClientResultException`，兼容网关各异），窄过滤会把陌生错误码的网关重新推回硬失败。宽过滤的最坏后果是
多一次 doomed 请求后落入既有失败路径，与纯 prompt-only 行为一致。取消（`OperationCanceledException`
及其派生）不过滤、不重试，直接传播。

**不引入跨调用的能力缓存**：「按模型缓存结构化输出兼容性」从未实现；拒绝网关每次多付一次快速 400，
而 GOAL 分解 / 澄清均为每会话一次，代价可忽略。换来无状态、无测试污染、无缓存失效面。

## 边界与禁令

1. **带工具循环的 Agent 不设 `ResponseFormat`**。`AutoDreamService` 的整合 Agent 有意保持 prompt-only：
   schema 会约束工具循环内**每条**助手消息，模型无法在工具调用之间自由叙述，与现有行为有实质差异；
   且其输出已有 `SanitizeKey/Value` 消毒、50 条配额与「记 Warning 返回 0」的完整降级链，schema 增益有限。
   MAF 自身的 judge 消费方（`AIJudgeLoopEvaluator`）也是「无工具、无会话」的直连调用。工具循环 Agent
   采用结构化输出须单独论证。
2. **结构化路径不得旁路调用方归一化**。`ClarificationQuestionGenerator` 抽出 `Finalize()`：
   清洗（trim/去空）、5 条上限、空问题 fail-closed——类型化结果与文本降级结果同样受产品契约约束。
3. **文本解析器是兼容层，不是死代码**。`ExtractJsonBlock` / `ExtractJsonObject` / `ExtractJsonArray`
   及对应单测禁止当冗余删除；②③ 两条路径都依赖它们。
4. **provider 请求整形只走 SDK 公开扩展点 / MEAI 类型化字段**（承 [MAF 集成边界](./0007-maf-integration-boundaries.md)）：
   `AgentRunOptions.ResponseFormat` / `ChatOptions.ResponseFormat` / `GetResponseAsync<T>`，
   禁止往 `AdditionalProperties` 手写提供商线协议键。
5. **诊断语义保持**：GoalDecomposer 的「空文本（reasoning budget 提示）」与「无 JSON 计划块」两条错误消息
   逐字保留；ClarificationQuestionGenerator 的 fail-closed（无模板兜底）保持。
6. **澄清记录（防再次误判）**：**Todo 不是结构化输出**。Todo 是 Harness `TodoProvider` 的工具能力
   （`todos_*` AIFunction + `AgentSessionStateBag`，MAF 集成边界 能力归属表），与 ResponseFormat 是两种
   不同机制，互不替代；「OneCode 已用 MAF 的 Todo」不构成「已用 MAF 的结构化输出」。

## 后果

- 支持原生 schema 的模型/网关获得受限解码，格式合规性不再依赖提示词运气。
- 忽略 schema 的网关：② 的文本降级零额外成本，与今天行为一致。
- 硬拒绝 `response_format` 的网关：③ 重试后回到今天的 prompt-only 行为，功能可用性不变。
- 成本：每处调用多一个分支与一次潜在重试；`JsonSerializerOptions` 必须携带 `TypeInfoResolver`。
- 未覆贴：`GoalSubGoalJudge`（VERDICT marker 手搓 judge，历史上「Kept custom (not AIJudgeLoopEvaluator)」
  决策未复核）不在本期范围 —— **已由 [代理循环边界](./0010-agent-loop-boundaries.md) 决策 4 闭环**：
  保留自研 judge（判定依据是确定性证据而非响应文本），但判定的 fail-safe 规则向框架对齐（MORE 优先）。
