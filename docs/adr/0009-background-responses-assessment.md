# MAF 后台响应（Background Responses）评估

**状态**: Accepted（决策：暂不集成，候选方案存档待启）
**日期**: 2026-09-21
**关联**: [MAF 集成边界与禁止清单](./0007-maf-integration-boundaries.md)（§4 BackgroundAgents、§5.1 版本敏感）、[MAF C# 接入指引](../maf/integration-guide.md)、[Declarative 评估结论](./0002-declarative-workflow-assessment.md)（评估存档范式）

## 语境

2026-09-21 对 Microsoft Agent Framework 的「后台响应」功能做了一轮评估：读官方文档
（[代理后台响应](https://learn.microsoft.com/zh-cn/agent-framework/agents/background-responses?pivots=programming-language-csharp)）、
核对 vendored `agent-framework/` 源码，并对 OneCode 实际 pinned 的 NuGet 包做 DLL 级验证。
触发问题是三连：这个功能定位是什么？OneCode 作为 Code Agent 可以用到吗？有什么要求？

本 ADR 记录结论与候选方案；**不做代码改动**（用户决策：先不重构，只存档）。

## 功能定位

后台响应 = 让**单次 agent run** 在模型服务端（OpenAI Responses API 的 background 模式）异步执行，
客户端持**延续令牌（continuation token）**轮询结果（非流式）或续传中断的流（流式）。

```
RunAsync(AllowBackgroundResponses=true) ──► 服务端后台执行
   ├─ 立即完成：ContinuationToken = null，响应即终态
   └─ 后台处理：返回 ContinuationToken
        └─ 带 token 再次 RunAsync（轮询）…… 直到 token = null
RunStreamingAsync：每个 update 携带 token（终止事件为 null）；
   断流后用最后一个非 null token 重新 RunStreamingAsync，从其断点继续
```

**它不是**：

- **不是 BackgroundAgents**：后者（Harness 的 `background_agents_start_task`）是把工作派给子代理；
  后台响应是把**同一次请求**交给服务端异步执行。OneCode 的 `ForkedAgentRunner` 与 BackgroundAgents
  的边界判定见 [MAF 集成边界](./0007-maf-integration-boundaries.md)，本功能不触及该边界。
- **不是客户端 fire-and-forget**：长计算发生在服务端，MAF 只做跨 `IChatClient`/`AIAgent` 边界的令牌管线。
- **Harness 代理不会自动启用**：需每 run 显式设置、保留 session，并在进程重启后持久化令牌（上游文档明确）。

适用场景（上游归纳）：复杂推理长任务、网络/客户端超时易中断的操作、启动长任务后稍后取结果的 UX。

## 机制与契约（源码级）

上游源码（`agent-framework/dotnet/src`，本地 checkout 仅供理解，行为以包为准，见 §版本证据）：

| 契约 | 实现与位置 |
|---|---|
| 开关 | `AgentRunOptions.AllowBackgroundResponses`（`bool?`，稳定 API）→ `ChatClientAgent` 映射到 MEAI `ChatOptions.AllowBackgroundResponses`（`ChatClientAgent.ApplyAgentRunOptionsOverrides`） |
| 令牌 | `AgentResponse.ContinuationToken` / `AgentRunOptions.ContinuationToken` 带 `[Experimental("MEAI001")]`；`AgentResponseUpdate.ContinuationToken` 未标注（其承载类型 `ResponseContinuationToken` 亦非实验） |
| 必须显式 session | `AllowBackgroundResponses=true` 且 session 为 null 时抛 `InvalidOperationException`（初始 run 即要求） |
| 禁带输入 | 带 `ContinuationToken` 续跑时再传 input messages 抛 `InvalidOperationException` |
| 令牌结构 | `ChatClientAgentContinuationToken`（internal）= `innerToken`（底层 IChatClient 产出）+ `InputMessages` + 已收到的 `ResponseUpdates`；JSON 可序列化（`ToBytes`） |
| 流式续传语义 | 恢复时只向消费者推送**新** update；token 中内嵌的旧 update 仅用于 run 末聚合（`ToChatResponse`）与会话/context provider 通知——消费者侧的断点前内容不会重放 |
| 历史持久化降级 | Harness 强制 `RequirePerServiceCallChatHistoryPersistence`，后台响应场景 MAF 回退 end-of-run 持久化并打 Warning（`LogAgentChatClientBackgroundResponseFallback`） |
| 支持面 | 上游文档与源码注释一致：**当前仅 OpenAI Responses API 路径**（`OpenAIResponseClient` / Foundry `AIProjectClient`）；不支持的实现静默忽略该标志（token 恒为 null，立即完成） |

要点：轮询节奏（间隔、指数退避、`token == null` 判定完成）由**调用方**负责；令牌可跨用户会话持久存储。

## 版本证据（以 OneCode pinned 包为准）

按 [MAF 集成边界](./0007-maf-integration-boundaries.md)「不得用本地 checkout 为已发布包行为作证」的要求，
对 OneCode 依赖的正式包做了 DLL 字符串验证（`D:\NuGet\Packages`，net10.0）：

| 包 | `AllowBackgroundResponses` | `ContinuationToken` | 备注 |
|---|---|---|---|
| `Microsoft.Agents.AI` 1.22.0 | ✅ | ✅ | API 面在 pinned 版本就绪 |
| `Microsoft.Extensions.AI.OpenAI` 10.10.0 | ✅ | ✅ | MEAI 侧映射就绪 |
| `Microsoft.Extensions.AI.Abstractions` 10.10.0 | — | ✅ | 实验 ID MEAI001 |
| `Microsoft.Agents.AI.Harness` 1.22.0（同上） | — | — | 含 `RequirePerServiceCallChatHistoryPersistence`（1.21.0 亦含该成员；Harness 侧确认，触发上述降级路径） |

即：**升级 MAF/MEAI 主版本时须重新核对**上述契约（尤其 Experimental 面的形状变化）。

## OneCode 适用性判定

### 已具备的条件

- 主路径**总是显式传 session**：`MainAgentRunner.RunStreamingAsync` 先
  `_sessionStore.CreateOrRestoreSessionAsync(...)` 再 `RunStreamingAsync(messages, session, new AgentRunOptions(), ct)`；
  AutoDream（整合 Agent `RunConsolidationAgentAsync` 内同样显式建 session 后 `RunAsync`）满足硬性要求。
  注：本条目曾引用 `AutoDreamService.cs:328` 行号，行号已漂移，改以方法名为锚点——行号不是契约。
- Harness 的 per-service-call 历史持久化降级有 MAF 内建处理（Warning + end-of-run 持久化），不会破坏会话。

### 阻塞点（当前用不上）

1. **客户端 API 不匹配（决定性）**：`ChatClientFactory.CreateOpenAiClient` 走
   `openAiClient.GetChatClient(model).AsIChatClient()` = **Chat Completions API**；
   后台响应只有 Responses API 路径支持（`GetResponsesClient().AsIChatClient()`，上游样例佐证）。
   Chat Completions / Anthropic（`AnthropicClient.AsIChatClient`）/ Ollama（`OllamaApiClient`）/
   第三方 OpenAI 兼容端点上一律静默忽略——开了也是 no-op。
2. **无 Azure 路径**：OneCode 不引用 `Azure.AI.Projects`；Azure/Foundry 用户要用须新增依赖
   （上游示例均走 `AIProjectClient`），与「保持本地 IChatClient 架构」的现状冲突
   （同 [声明式工作流评估](./0002-declarative-workflow-assessment.md) 对 `ResponseAgentProvider` 的排除理由）。
3. **会话/历史模型冲突**：token 续跑不加载 session 历史、输入全部来自 token 内嵌；
   OneCode 的 transcript 拥有历史（经 `TranscriptChatHistoryProvider` 按 MAF `ChatHistoryProvider` 契约承载）、
   compaction、approval 续跑循环（`currentMessages = approvalResponses` 后以新 messages 重跑同一 session）
   均假设「本地喂全量历史」，与服务端有状态模型需要专门适配与验证。
4. **实验性 API**：读 `ContinuationToken` 触发 MEAI001，需集中抑制并有升级复查点。
5. **装饰链存在令牌丢失缺陷**（详见候选方案 P2）：
   `RetryOnOverloadChatClient`（实现所在的文件是 `RetryOnOverloadMiddleware.cs`，该文件中没有与文件同名的类型）的流式重试
   `CloneUpdate` 不拷贝 `ContinuationToken`；`VcrChatClientDecorator` 的 fixture key 仅
   ModelId+messages，Responses 与 chat-completions 录制会碰撞。

### 价值场景（若将来启用）

- 无头长任务：GOAL 子目标（`GoalSubGoalExecutor` → `LoopAgent`）、Cron（`CronJobExecutor`
  以 `WorkingMode.Goal` 跑 `StreamQueryAsync` 后丢弃事件）、AutoDream（非流式 `RunAsync`，
  崩溃即丢失本次巩固）——启动后轮询、CLI 重启后可恢复。
- TUI 流式断线恢复：网络闪断后从断点续流而非整轮重来（当前中断只有 "(cancelled)"，无续跑 UX）。
- 超长单响应绕过高层网关超时（OneCode 已设 `NetworkTimeout = Infinite`，该收益部分被抵消）。

## 要求清单（集成时必须满足）

1. 底层 chat client 必须支持：当前仅 OpenAI Responses API；其余忽略。
2. 必须显式 `AgentSession`（初始 run 即要求）。
3. 带 token 续跑不得携带 input messages。
4. 调用方自持轮询/续传：间隔 + 指数退避；以 `token == null` 判定完成。
5. 跨重启持久化：`SerializeSessionAsync/DeserializeSessionAsync` +
   `AgentAbstractionsJsonUtilities` 序列化 `ResponseContinuationToken`（上游 Step10 示例模式）。
6. 服务端留存：轮询时后台响应必须仍在服务端存在，需过期/重试策略。
7. per-service-call 历史持久化场景会降级（Warning + end-of-run 持久化），需在文档/日志解释。
8. Experimental（MEAI001）：集中单文件接触实验面 + 升级复查。
9. Harness 代理不自动启用，需每 run 设置并保留 session。

## 候选集成方案（存档，不实施）

形态目标：**对现有调用方透明**——装饰器层吸收轮询/续流，TUI/GOAL/Cron/AutoDream 近零改动。

| 层 | 内容 |
|---|---|
| 配置 | `useResponsesApi`（Boolean、默认 false、RestartRequired）注册进 `ConfigModels.cs` descriptor 表（`ollamaContextWindow` 同款模式）；非 openai provider 开启时抛带指引的异常 |
| 客户端 | `ChatClientFactory.CreateOpenAiClient` 分支：`GetResponsesClient(model).AsIChatClient()`；`ChatClientServiceCollectionExtensions` 读 `Effective` 传入 |
| 令牌修复 | `RetryOnOverloadChatClient.CloneUpdate` 补 `ContinuationToken`（注意：实现所在文件是 `RetryOnOverloadMiddleware.cs`，但文件中没有该同名类型）；`VcrChatClientDecorator` fixture key 增加 API 模式维度 + 记录补 token 字段；`MaxOutputTokensDecorator` 对未完成响应（token 非 null）跳过 Length 恢复；`OpenAiResponseSanitizer.TryExtractUpstreamError`（2026-09-11 起已能识别通用 `{"error": {…}}` 错误体形态，Responses 错误体同形）回归验证即可，不再新增分类逻辑 |
| 编排 | 新装饰器 `BackgroundResponseChatClient`：非流式轮询到完成（空 messages + token、指数退避、MaxBackgroundWait 默认 30min）；流式传输异常时以最后非 null token 自动重订阅（有上限），消费者看到连续流；MEAI001 pragma |
| 打通 | `MainAgentRunOptions.AllowBackgroundResponses`（null=按能力默认）→ `BuildChatOptions`；`BuildAsAIAgentAsync`/Forked/Team 三条装配路径经 `AgentPipelineBuilder.BuildChatClientAgent` 自动流透 |
| 持久化 | `BackgroundRunStateStore`（`~/{userConfigDir}/background-runs/{conversationId}.json`，原子写 + .bak，仿 `JsonTaskStore`）；`/resume`（docs/commands.md）扩展为可续后台 run；TUI 复用 `TuiNotice` 提示 |

风险与对策：Responses 语义漂移（reasoning items、服务端 store、tool items）冲击 transcript/compaction/VCR/ReasoningPassback → opt-in 默认关 + e2e 矩阵；token 体积随流增长 → 体积指标与阈值告警；approval 流与后台响应交互（ToolApprovalAgent break 流后服务端响应成孤儿）→ 行为测试验证，必要时交互 run 禁用；Harness 降级警告噪音 → ADR/日志说明；MEAI001 版本漂移 → 集中实验面 + 复查清单；第三方兼容端点无 Responses API → 非官方端点开启时启动 Warning。

## 决策

**暂不集成，候选方案存档。** 理由：

1. 默认路径（Chat Completions / Anthropic / Ollama / 第三方兼容端点）上该能力为零收益，而 opt-in 的
   可达用户面窄（仅 OpenAI 官方 Responses API）。
2. Responses API 与 OneCode 现有 transcript/compaction/VCR/ReasoningPassback 装饰链的兼容性未验证，
   集成前需要一轮专门的语义核对与 e2e 矩阵，成本高于当前收益。
3. `ContinuationToken` 为 Experimental（MEAI001），存在版本漂移风险；MAF 集成边界要求每次升级复查。
4. 没有已证实的用户痛点需求（长任务/断线恢复）驱动。

与 [声明式工作流评估](./0002-declarative-workflow-assessment.md) 同一处置范式：评估入档、议题保持关闭、
设定明确的重启条件。

## 重启条件

出现任一条件即重开议题：

1. 用户明确提出需要超长/可恢复的无头 run（GOAL/Cron/AutoDream 场景有实证）。
2. MAF/MEAI 升级时 `ContinuationToken` 转正，或其形状变化需要同步评估。
3. 计划引入 Azure AI Foundry / 托管代理路径（届时 Responses 客户端与依赖策略一并重评）。
4. GOAL/Cron 长任务因超时/中断的失败率数据支持投入。

## 影响

- 仅新增本 ADR；零代码、零依赖、零行为变化。
- 后续重开会话时，以本 ADR §候选集成方案 为起点，先做 P2 令牌修复与兼容性验证，再谈编排层。
-  [`docs/maf/integration-guide.md`](../maf/integration-guide.md) 的「相关决策」区补充本 ADR 链接
   （一句话指引，不展开机制）。
