# 决策模型接入（Jev）：抽象归属与接入点白名单

**状态**: Accepted
**日期**: 2026-09-24
**关联**: [权限与工具审批边界](./0001-permission-vs-toolapproval-vs-filter.md)（Permission/Filter 是唯一 Allow/Deny 门）、
[MAF 集成边界与禁止清单](./0007-maf-integration-boundaries.md)（实验性 API 不进依赖面）、
[代理循环能力边界](./0010-agent-loop-boundaries.md)（`LoopAgent` 与评估器语义）、
[子代理派工边界](./0011-background-agents-delegation-boundary.md)（同款「上游未定型则不入依赖面」原则）、
[MAF C# 接入指引](../maf/integration-guide.md) §12/§13.2

## 语境

### 1. 讨论缘起

2026-09-24，microsoft/agent-framework 的 open issue 检索中出现 TypeSafe「Jev」决策模型相关条目
（[#8545](https://github.com/microsoft/agent-framework/issues/8545)、
[#8562](https://github.com/microsoft/agent-framework/issues/8562) 及其 PR
[#8563](https://github.com/microsoft/agent-framework/pull/8563)、Python 侧 PR
[#8592](https://github.com/microsoft/agent-framework/pull/8592)）。上游**尚未发布任何 .NET 支持**，
但接入形态一旦定型再补边界会付出更高代价，因此需要提前固化：**它能帮 OneCode 做什么、接入点白名单是哪些、
哪些位置明令禁止、什么条件下重新评估**。本 ADR 只记录边界与规划，不落地实现。

同型教训见 [子代理派工边界](./0011-background-agents-delegation-boundary.md) §语境 1：边界只存在于计划文档中时，
判断会反复漂移；本 ADR 的作用是把「是否引入第二个决策者」这个问题的答案一次写死。

### 2. Jev 是什么（决策模型，不是对话模型）

TypeSafe AI 的 "System One" 模型，2026-09-15 发布。它**不生成文本**：输入一段 `state`
（字符串 / JSON 对象 / 文本数组）加一组带类型的 `questions`，**单次请求并行**返回类型化**概率**答案。

| 原语 | 语义 | 返回 |
|---|---|---|
| **Noul** | 是 / 否 | `P(true) ∈ [0,1]` |
| **Choice** | 从调用方定义的集合中选一个（上限 **255** 项） | 选中值 + 全量概率分布 + `confidence` |
| **Score** | 有序档位（**2–10** 档） | 概率加权连续分 + 分布 + `confidence` |

调用面：

```
POST https://api.typesafe.ai/v1/systemone
Authorization: <key>
{ "state": ..., "model": "jev-latest", "questions": { ... } }
```

响应：`{ model, answers: { <id>: { type, noul | choice | probabilities, score, legend, confidence } }, usage: { input_tokens, output_tokens } }`

| 项 | 值 |
|---|---|
| 模型 | `jev-1.13.0`；别名 `jev-latest`（= 1.13.0）、`jev-preview` |
| 上下文 | 64k / 请求（`state` 32k + 最长问题） |
| 模态 | **纯文本** |
| 价格 | $0.042 / Mtok 输入，**输出免费** |
| 限流 | 250k tok/s、1200 req/min |
| 错误 | 401、422、429、529 Overloaded（需退避，遵循 `retry-after`） |

**官方定性（决定本 ADR 全部取舍的前提）**：Jev **不是**编码代理模型的替代品。官方文档明确写了
「不存在 `model: "jev-latest"` 这种开关，能把你的编码代理变成 Jev 驱动的代理」。它的推荐用法是
**活在应用/代理内部**，承担路由、分类、打分与护栏。

### 3. 上游四件与两处冲突

| 工件 | 状态 | 内容 |
|---|---|---|
| [dotnet/extensions#7764](https://github.com/dotnet/extensions/issues/7764) | `untriaged`，**未受理** | 提议在 `Microsoft.Extensions.AI.Abstractions` 增加 provider 中立的 `IDecisionClient` |
| [agent-framework#8545](https://github.com/microsoft/agent-framework/issues/8545) | open，已指派 rogerbarreto | 要求 MAF **消费未来 MEAI 抽象**，明确禁止 MAF 另立并行决策抽象；提出三个接入面：`LoopEvaluator`、工具预筛选、Agent Skill 选择 |
| [agent-framework#8562](https://github.com/microsoft/agent-framework/issues/8562) / [PR #8563](https://github.com/microsoft/agent-framework/pull/8563) | open，**未合并** | 在 `Microsoft.Agents.AI.Abstractions` 内**复刻** MEAI 契约（`[Experimental(MAAI001)]`）+ `DecisionLoopEvaluator`（`CompletionThreshold` 默认 0.90）+ `Microsoft.Agents.AI.TypeSafe` 提供方包 |
| [agent-framework PR #8592](https://github.com/microsoft/agent-framework/pull/8592)（Python） | open | **相反路线**：把 TypeSafe 包装成 **chat client**，`response_format` → `Questions`，外加约束式函数调用 |

**两处未定，且互相冲突**：① 抽象归属（MEAI 还是 MAF）；② 形态（决策客户端还是 chat client）。
PR #8563 作者已表态认同抽象应由 MEAI 持有，会等 #7764 受理后才提 dotnet/extensions PR，届时把 MAF 侧改为
消费 MEAI 类型。这意味着**现在照抄任何一个 PR 都必然返工**。

### 4. 版本证据（OneCode pinned 包）

按 [MAF 集成边界](./0007-maf-integration-boundaries.md)「不得用本地 checkout 为已发布包行为作证」的要求，
对 OneCode 锁定版本逐符号计数（`D:\NuGet\Packages`，pinned 见 `src/Directory.Packages.props`：MEAI `10.10.0`、MAF `1.22.0`）：

| 程序集 | `IDecisionClient` | `DecisionQuestion` | `DecisionResponse` | `DecisionAnswer` | `TypeSafe` | `SystemOne` |
|---|---|---|---|---|---|---|
| `Microsoft.Extensions.AI.Abstractions` 10.10.0 | 0 | 0 | 0 | 0 | 0 | 0 |
| `Microsoft.Agents.AI` 1.22.0 | 0 | 0 | 0 | 0 | 0 | 0 |
| `Microsoft.Agents.AI.Abstractions` 1.22.0 | 0 | 0 | 0 | 0 | 0 | 0 |

本地 `agent-framework/` checkout（`dotnet-1.22.0-6-g669c8b95e`）的 `dotnet/` 下检索同组符号亦无命中。

**结论：.NET 侧今天零可用面。** 因此本 ADR 记录的是边界、白名单与触发条件，不是已实现的能力；
「后续工作」表中 D2–D6 的状态是**待上游**，不是待排期。

### 5. OneCode 现状：所有「第二次推理」复用同一个 `IChatClient`

OneCode 现有的辅助判断（工具筛选、子目标判定、澄清生成、记忆巩固、计划校验等）**全部复用同一个 `IChatClient`**：

- `ModelManager` 的 `GetFastModel()` 只是返回一个 `ModelId`，底层仍是同一个 chat client，不存在第二条推理通道；
- 这些判断统一经 `src/OneCode.App/Services/StructuredChatCall.cs` 发起，靠 prompt 约定输出格式；
- 最典型的是 `src/OneCode.App/Services/Agent/GoalSubGoalJudge.cs`：让 LLM 回一行 `VERDICT: DONE` / `VERDICT: MORE`，
  再**文本解析**，歧义时 MORE 优先。

代价是双份的：每次辅助判断都要付一次完整对话 LLM 往返（含系统提示与工具 schema 开销），
并且判定结果藏在脆弱的不透明文本里。Jev 恰好补在这一点上——**类型化输出（无需解析）+ 概率而非二值（阈值可调）+
单次并行（多问题一请求）+ 数量级更低的延迟与成本**。这也正是上游 #8545 提出的
「廉价决策模型先判、强模型复核」级联（cheap-then-strong）在 OneCode 的对应物。

## 决策

### 1. 不引入上游实验类型；OneCode 自持最小契约于 Core

- **不引用** MAF `[Experimental(MAAI001)]` 的决策类型，**也不预依赖**未受理的 MEAI API。
  依赖面只增加 OneCode 自己声明的类型。
- 契约放在 `src/OneCode.Core/Decisions/`（命名空间 `OneCode.Core.Decisions`），**命名逐字镜像 MEAI 提案**，
  以免上游落地后被迫做第二次改名：

| 契约类型 | 要点 |
|---|---|
| `IDecisionClient` | `GetResponseAsync(DecisionRequest, DecisionOptions?, CancellationToken)` + `GetService(Type, object?)` |
| `DecisionRequest` | `JsonElement State` + `IList<DecisionQuestion> Questions`（用 `JsonElement` 以保持 AOT/trimming 友好） |
| `DecisionQuestion` | 抽象基类（`Id` / `Instructions` / `AdditionalProperties`）；派生 `BinaryDecisionQuestion`、`ChoiceDecisionQuestion`、`ScoreDecisionQuestion` |
| `DecisionResponse` | `Answers` / `ResponseId` / `ModelId` / `Usage` / `RawRepresentation` / `AdditionalProperties` |
| `DecisionAnswer` | 派生 `BinaryDecisionAnswer`（`TrueProbability`，不另设 `Confidence`）、`ChoiceDecisionAnswer`（`SelectedChoice` + `Probabilities` + `Confidence`）、`ScoreDecisionAnswer`（`Score` + `Probabilities` + `Confidence`） |
| `DecisionOptions` | `ModelId` / `AdditionalProperties` / `RawRepresentationFactory` / `Clone()` |
| `DecisionClientException` | 带 `FailureKind`（`Authentication` / `InvalidRequest` / `RateLimited` / `Overloaded` / `ProviderUnavailable` / `InvalidResponse` / `Unknown`）与 `IsTransient` |

- **契约中不得烘焙提供方限制**（255 选项 / 10 档 / 64k 上下文）。这些是 Jev 的实现细节，
  只在适配器内校验并抛 `InvalidRequest`；写进契约会让契约永久绑死在第一个提供方上。
- `ScoreDecisionAnswer.Score` 的语义固定为 `Σ(档位序号 × P(档位序号))`——**可移植语义**，
  而非某个提供方的原始字段。

### 2. 不把 Jev 做成 chat provider

`src/OneCode.Core/Constants.cs` 的 `ModelProviders` 是 **chat provider 枚举**，
`src/OneCode.Infrastructure/Ai/ChatClientFactory.cs` 的 `CreateBaseClient` 会按它 `switch`。
把 Jev 混进去会同时污染：`/model` 与 `fastModel` 的选择面、模型目录刷新、TUI 选择器、
以及 `src/OneCode.Core/Models/ModelCapabilities.cs` 描述的能力矩阵（Jev 不支持工具调用、不支持流式、不产文本）。

**这条同时否掉上游 PR #8592 的路线**：把决策模型包装成 chat client，等于让「判断」伪装成「对话」，
既破坏 §边界与禁令 第 1 条（决策者与授权者分离），又让 `IChatClient` 的语义承诺被稀释。
OneCode 只认「决策客户端」一种形态。

### 3. 接入点白名单

只有下列位置可以接入决策模型，且**每个接入点都必须自带开关、默认关闭**。
统一形状是：**输入 = 已授权的候选集 + 最小 state 投影 → 输出 = 候选集收窄，或「升级为 Ask」**。

| # | 接入点 | 现状实现 | Jev 形态 | 收益 | 失败语义 | 优先级 |
|---|---|---|---|---|---|---|
| 1 | 工具短名单 | `src/OneCode.App/Query/SessionToolSet.cs` 的关键词评分选工具 | Choice / Noul 语义短名单 | 上下文瘦身、工具选择准确率 | **fail-open**：回退全量已授权集 | P0 |
| 2 | GOAL 子目标判定级联 | `src/OneCode.App/Services/Agent/GoalSubGoalJudge.cs`：LLM + 文本解析 `VERDICT` | Noul `P(完成)` 先判，低置信度回落 LLM | 每轮省一次完整 LLM 判断；与上游 `DecisionLoopEvaluator` 同构 | **fail-safe**：继续 / 交下一评估器，**绝不提前终止** | P0 |
| 3 | 权限风险加严 | `src/OneCode.Core/Permissions/Yolo/YoloClassifier.cs`（纯规则）+ `src/OneCode.Core/Permissions/PermissionChecker.cs` 未命中即 Ask | Score 风险分 | 减少无谓打断 | **fail-safe**：视为未命中，保持现有 Ask | P1（安全敏感，须独立立项 + 反证测试） |
| 4 | 记忆相关性 | `src/OneCode.App/Services/Memory/MemoryService.cs` 的确定性打分 top-6 | Score / Choice | 相关性提升 | **fail-open**：回退确定性打分 | P2 |
| 5 | Team 需求澄清门 | `src/OneCode.App/Services/Coordinator/TeamRequirementService.cs` 的确定性 gate + 必要时 LLM | Noul「是否需要澄清」 | 减少澄清轮次 | **fail-safe**：照常澄清 | P2 |

### 4. 排除项（附理由）

| 位置 | 为何排除 |
|---|---|
| `/loop` 停止判定（`src/OneCode.App/Services/Loop/IterativeLoopService.cs` 的确定性 check-command） | 已经是**确定性**判据（跑构建/测试）。换成概率判断只会引入不确定性，收益为负 |
| 压缩阈值（`src/OneCode.App/Services/Compact/AutoCompactService.cs` 与压缩比例常量） | token 比例是确定性指标，无需推理 |
| 子代理 profile 路由（`src/OneCode.Infrastructure/Agent/PipelineProfile.cs`） | 由模型自己选择 agent 类型，字符串匹配已足够 |
| Skill 选择（8 个内置技能） | 候选集太小，收益不足 |
| 主代理 function-call 循环 | MAF 拥有该循环，且这是**唯一的写路径**——不得引入第二决策者 |
| 任何需要生成自然语言的位置（prompt 组装、摘要、标题、澄清问题） | Jev 不产文本 |

### 5. 统一失败语义与可观测性

- **选择类 fail-open**：决策模型不可用 / 超时 / 未启用 → 回退到该接入点原有的确定性行为，功能不降级。
- **循环完成类 fail-safe**：失败 → 继续或交给下一个评估器，**任何情况下不得因决策模型故障而提前终止或跳过审批**。
- 统一超时预算，且该预算必须**小于**所在回合的既有超时，避免辅助判断拖长主流程。
- 可观测：每次调用记录 provider / model / 耗时 / 答案分布 / 是否降级；
  **state 原文不得进日志、会话记录或遥测**（见 §边界与禁令 第 2 条）。

### 6. 重评触发条件

出现以下任一条时**先立项评估，再动代码**，不得以注释绕过本 ADR：

1. dotnet/extensions#7764 被受理（`IDecisionClient` 进入 MEAI 抽象）；
2. MAF #8563 或 Python #8592 合并，或 MAF 发布了决策模型相关包；
3. Jev 提供文本生成能力（则 §决策 2 的前提改变，需重新评估它能否作为 chat provider）；
4. 出现需要「第二决策者」的新场景，且不属于 §决策 4 的排除项。

前三条落地后，§决策 1 的自持契约应当**删除并改用上游类型**，而不是长期并存。

## 边界与禁令

1. **决策模型不得成为授权引擎。** 它只能**收窄**已授权的候选集，或把 Allow **升级**为 Ask/Deny；
   **绝不能产生 Allow，也绝不能绕过审批**。这是 [权限与工具审批边界](./0001-permission-vs-toolapproval-vs-filter.md)
   的直接推论：Permission/Filter 是唯一 Allow/Deny 门。
2. **Jev 是第二个外部推理边界。** 必须显式 opt-in（默认关闭）；`state` 只做最小化投影
   （不整包塞入文件内容与会话历史）；**`state` 不得写入日志、会话记录或遥测**。
3. **概率不是校准保证。** 阈值属于产品策略层配置，不得硬编码进契约或适配器；
   任何阈值都必须能在不重新编译的情况下调整。
4. **上游实验类型不进依赖面。** 不引用 `[Experimental(MAAI001)]` 的决策类型，不预依赖未受理的 MEAI API
   ——与 [MAF 集成边界](./0007-maf-integration-boundaries.md) 和
   [子代理派工边界](./0011-background-agents-delegation-boundary.md) 同款原则。
5. **只认「决策客户端」一种上游形态。** 不采纳把 TypeSafe 包装成 chat client 的路线（§决策 2）。

## 后续工作

下列条目**均不在本 ADR 实施范围**，各自独立立项。门禁：`dotnet build src/OneCode.slnx` 无新增警告
+ `dotnet test src/OneCode.slnx` 全绿。

| ID | 事项 | 一句话验收 | 状态 |
|---|---|---|---|
| D1 | 上游定型跟踪（MEAI #7764 / MAF #8563 / Python #8592） | 任一落地即按 §决策 6 重评本 ADR | 待上游 |
| D2 | Core 决策契约落地 | 契约可编译；契约中不出现任何 provider 特定类型或限制常量 | 待上游 |
| D3 | TypeSafe 决策客户端适配器 | 三原语往返 + 错误分类测试；不在适配器内静默重试 | 待上游 |
| D4 | 工具短名单接入（P0） | 决策关闭时行为与现状逐字节一致（反证） | 待立项 |
| D5 | GOAL 子目标判定级联接入（P0） | 低置信度回落 LLM；决策不可用时流程不中断 | 待立项 |
| D6 | 权限加严接入（P1） | 反证：任何决策输出都无法产生 Allow | 待立项 |

**旁路选项**：TypeSafe 官方提供的 agent skill 可作为零代码试用路径，不占用 D 系列，也不改变上述任何禁令。

## 后果

- **正面**：把「是否引入第二个决策者」的答案一次写死，避免第四次从零讨论；接入点白名单与失败语义在写代码前
  就已确定，实现阶段只需照图施工；上游未定型期间依赖面零增长，升级成本不变；
  廉价决策模型 + 强模型复核的级联路径已预留，不必在 GOAL 判定热路径上继续堆完整 LLM 往返。
- **负面**：自持契约在上游落地后需要一次删除式迁移（D2 → 改用上游类型），期间多维护一份命名镜像；
  上游若最终选择「包装成 chat client」的形态，本 ADR 需要修订甚至重立。
- **中性**：现有确定性判据（`/loop` 停止判定、压缩阈值、profile 路由）不受影响；
  决策模型默认关闭，未配置时行为与今天完全一致。

## 引用

- 上游：`dotnet/extensions#7764`、`microsoft/agent-framework#8545`、`#8562`/`#8563`、`#8592`
- TypeSafe 文档：`/concepts/system-one`、`/introduction/coding-agents`、`/api.md`、`/models.md`
- 版本证据：`D:\NuGet\Packages` 下 MEAI `10.10.0` 与 MAF `1.22.0` 三个程序集的逐符号计数（§4 表）
- 产品现状：`src/OneCode.App/Query/SessionToolSet.cs`、`src/OneCode.App/Services/Agent/GoalSubGoalJudge.cs`、
  `src/OneCode.App/Services/Agent/GoalSubGoalLoop.cs`、`src/OneCode.App/Services/Loop/IterativeLoopService.cs`、
  `src/OneCode.Core/Permissions/PermissionChecker.cs`、`src/OneCode.Core/Permissions/Yolo/YoloClassifier.cs`、
  `src/OneCode.App/Services/Memory/MemoryService.cs`、`src/OneCode.App/Services/Coordinator/TeamRequirementService.cs`、
  `src/OneCode.App/Services/StructuredChatCall.cs`、`src/OneCode.App/Services/ModelManager.cs`、
  `src/OneCode.App/Services/Compact/AutoCompactService.cs`、`src/OneCode.Infrastructure/Ai/ChatClientFactory.cs`、
  `src/OneCode.Core/Ai/IChatClientFactory.cs`、`src/OneCode.Core/Constants.cs`、
  `src/OneCode.Core/Config/ConfigModels.cs`、`src/OneCode.Core/Models/ModelCapabilities.cs`、
  `src/OneCode.Infrastructure/Config/Constants.cs`、`src/OneCode.Infrastructure/ServiceCollectionExtensions.cs`、
  `src/OneCode.Infrastructure/Agent/PipelineProfile.cs`、`src/Directory.Packages.props`
- 关联决策：[权限与工具审批边界](./0001-permission-vs-toolapproval-vs-filter.md)、
  [MAF 集成边界与禁止清单](./0007-maf-integration-boundaries.md)、
  [代理循环能力边界](./0010-agent-loop-boundaries.md)、
  [子代理派工边界](./0011-background-agents-delegation-boundary.md)
