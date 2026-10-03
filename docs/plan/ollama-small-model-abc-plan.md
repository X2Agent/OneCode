# Ollama / 小模型适配 A·B·C 实现计划

本文件是**计划**，不是当前行为说明。文中列出的改动尚未实施；每项给出文件路径、行锚点、测试清单与验证命令，
供评审与排期。评审通过后按 **B → A → C** 顺序落地代码（顺序理由见文末「实施顺序与排期」）。

缺口分析共 14 项，本计划只展开三项：

| 项 | 主题 | 性质 |
|---|---|---|
| **B** | `MaxOutputTokens` 升级越过真实上下文窗口 | 纯 bug 修复 |
| **A** | Ollama 采样基线 | 显式默认基线 |
| **C** | 畸形 tool-call 回放修复 | 补齐唯一未覆盖路径 |

不在本计划范围：**D**（用能力 profile 取代散落的 provider 分支，需独立 ADR）、**E**（prompt 分层 / 工具 schema 预算，依赖 D 的结论）。

全部行号基于撰写时的 `main`；实现前若文件已变动，以实际内容为准。

---

## B. MaxOutputTokens 升级越过真实上下文窗口（纯 bug 修复）

### 问题

`MaxOutputTokensDecorator` 在收到 `ChatFinishReason.Length` 时，无条件把 `MaxOutputTokens` 升级到常量 `65_536`
（`src/OneCode.Infrastructure/Ai/MaxOutputTokensDecorator.cs:18`、升级分支 49–53）。在 8K / 16K 窗口的小模型上，
这次"升级"必然**再次**被服务端以 `Length` 截断，于是白白烧掉 `RecoveryLimit = 3` 次恢复机会：
升级 → 仍 Length → 进 continuation 循环 → 再 3 次失败 → 返回半截响应。用户看到的是"小模型答一半就停"。

同时 `MainAgentRunner.BuildChatOptions`（`src/OneCode.App/Services/Agent/MainAgentRunner.cs:346`）的缺省值 `?? 4096`
与 `ModelContextDefaults.ResolveOutputReservation(window, 8_192)`（`src/OneCode.Core/Models/ModelContextDefaults.cs`）是两套独立规则，
后者才是 `docs/compact-thresholds.md` 承诺的"窗口 1/4 收敛"规则。`TokenBudget`（`src/OneCode.App/Services/Compact/TokenBudget.cs:52-61`）
还各自复制了一份 Ollama 判定 + 收敛逻辑。

### 改动清单

1. **`src/OneCode.Infrastructure/Ai/MaxOutputTokensDecorator.cs`**
   - 主构造加可选参数 `int? contextWindow = null`。
   - 新增 `private int? ResolveEscalationCap()`：`contextWindow is not > 0` → `EscalatedMaxTokens`（保持既有行为）；
     否则 `Math.Min(EscalatedMaxTokens, contextWindow.Value - 1)`。
   - 升级分支改为：cap 为 `null` 或 `<= 当前 MaxOutputTokens`（即窗口根本放不下升级）时**跳过升级**，
     直接走 continuation 恢复；否则按 cap 升级。注释说明"窗口未知时保持原行为，窗口已知时不再越过窗口"。
   - XML 文档补一句该参数语义与 `docs/compact-thresholds.md` 的关系。

2. **`src/OneCode.App/Services/Agent/CompactionStrategyFactory.cs`**
   - `BuildAsync` / `BuildForWorkerAsync` 的返回元组从 `(CompactionStrategy, string? ProviderId)` 扩为
     `(CompactionStrategy, string? ProviderId, int ContextWindow)`，把私有 `ResolveContextWindow` 已经算出的窗口交出去
     （当前算了但丢弃，正是本项缺失的那一环）。
   - 同步更新 XML 文档里的 tuple 说明。

3. **`src/OneCode.App/Services/Agent/MainAgentRunner.Pipeline.cs`**
   - 第 21–22 行的解构补第三个值；第 36 行 `new MaxOutputTokensDecorator(_chatClient)` → `new MaxOutputTokensDecorator(_chatClient, contextWindow)`。

4. **`src/OneCode.App/Services/Agent/ForkedAgentRunner.cs:119-121`**
   - `BuildForWorkerAsync(...)` 解构补第三项（值未使用处直接解构忽略）。

5. **`src/OneCode.App/Services/Coordinator/TeamAgentFactory.cs:82-86`**
   - 同样解构补第三项并注入；第 121 行 `MaxOutputTokens = 4096` 改为走共享收敛规则。

6. **`src/OneCode.App/Services/Agent/MainAgentRunner.cs:346`**
   - `MaxOutputTokens = options.MaxOutputTokens ?? 4096` → 委托
     `ModelContextDefaults.ResolveOutputReservation(contextWindow, 8_192)`，`contextWindow` 由第 2 步的元组带下来
     （存到 runner 的单次运行态字段，或直接传给 `BuildChatOptions`）。

7. **`src/OneCode.App/Services/Compact/TokenBudget.cs:52-61`**
   - 删除私有的 `ResolveOutputReservation` 与 `IsLocalOllama` 副本，统一调用 `ModelContextDefaults` 的单条规则
     （单类 500 行约束下这是净减行数）。

### 测试

- **新增 `src/OneCode.Tests/MaxOutputTokensDecoratorTests.cs`（新建）**
  - `window = 8192`：首次 Length 后请求里的 `MaxOutputTokens` 必须 `<= 8191`，且不再出现 65536。
  - `window = 16_384`：同上，cap 夹到 16383。
  - `window = 128_000`：cap 仍是 65536（不因有窗口就变小）。
  - `window = null`：保持原行为，升级到 65536（反证：窗口未知不能改变既有路径）。
  - `window = 4096`（放不下任何升级）：不升级，直接进 continuation；`RecoveryLimit` 用尽后返回 Length 响应。
  - 全部用例断言调用方传入的 `ChatOptions.MaxOutputTokens` 在返回后已还原（覆盖现有 `finally` 语义）。
- **修改 `src/OneCode.Tests/ThinkingOptionsContractTests.cs:86-92`**
  - `BuildChatOptions_ThinkingDisabled_KeepsTheDefaultOutputCapAndNoReasoning` 断言 `4096` → 改为断言
    `ModelContextDefaults.ResolveOutputReservation(window, 8_192)` 的结果（Theory 覆盖 32768 / 128000 / 0）。
- **扩展 `src/OneCode.Tests/ModelContextDefaultsTests.cs`**：补 `window = 8192 → 2048`、`window = 16384 → 4096` 两组
  Theory，与 B 的窗口语义对齐。
- **新增 tuple 契约测试**（新建 `src/OneCode.Tests/CompactionStrategyFactoryTests.cs`）：
  `BuildAsync` 返回的 `ContextWindow` 等于其内部 `ResolveContextWindow` 的结果。

### 文档

- **`docs/compact-thresholds.md:27`** 就地改正"主 Agent 输出上限缺省 8,192，Worker 缺省 4,096"与 Ollama 1/4 规则，
  补上"Length 恢复的升级目标会被真实窗口夹取"这一句。

### 验证

```powershell
dotnet build src/OneCode.slnx        # 不得新增警告
dotnet test src/OneCode.slnx --filter "FullyQualifiedName~MaxOutputTokensDecorator|FullyQualifiedName~ModelContextDefaults|FullyQualifiedName~ThinkingOptions|FullyQualifiedName~DocFactConsistency"
```

---

## A. Ollama 采样基线（1 个新配置键 + 3 个硬编码 pin）

### 问题

`ProviderAwareDecorator.InjectOllamaStrategy`（`src/OneCode.Infrastructure/Ai/ProviderAwareDecorator.cs:177-189`）
目前只注入 `NumCtx` 与 `Think`。其余采样参数走 Ollama 服务端默认：chat 默认 `temperature ≈ 0.8`，
对 tool-call JSON 来说熵太高——参数名幻觉、缺字段、类型漂移都直接变成畸形调用。`ollamaContextWindow` 已经证明
"小模型需要显式基线"这条路是走得通的，采样参数是同一问题的另一半。

**已定的配置面**：只新增 **1 个**键 `ollama.temperature`；`TopP` / `TopK` / `RepeatPenalty`
按 Ollama 自身默认值（0.9 / 40 / 1.1）**在代码里显式 pin**，不做配置项——它们是防默认值漂移的锚，不是用户旋钮。

### 改动清单

1. **`src/OneCode.Core/Constants.cs:51` 附近**
   `ConfigKeys` 增加 `public const string OllamaTemperature = "ollama.temperature";`
2. **`src/OneCode.Core/Config/ConfigModels.cs:133` 之后**
   `["ollama.temperature"] = new("ollama.temperature", ActivationMode.RestartRequired, ConfigValueKind.Double, BuiltInDefault: 0.15);`
   `RestartRequired` 的理由：`ProviderAwareDecorator` 在 DI 单例里构建一次，改值不重建就不生效。
3. **`src/OneCode.Core/Config/AppSettings.cs:23` 之后**
   `public double OllamaTemperature { get => Get(CoreConstants.ConfigKeys.OllamaTemperature, 0.15); init => … }`
4. **`src/OneCode.App/Query/ChatClientServiceCollectionExtensions.cs:68` 附近**
   与 `ollamaNumCtx` 并列取 `var ollamaTemperature = isOllama ? settings.OllamaTemperature : (double?)null;`
   一并传入 `CreateWithDecorators(...)`。
5. **`src/OneCode.Infrastructure/Ai/ChatClientFactory.cs:45-68`**
   `CreateWithDecorators` 增加 `double? ollamaTemperature = null`；第 63 行
   `new ProviderAwareDecorator(retryClient, providerId, ollamaNumCtx)` → 追加第四个实参。
6. **`src/OneCode.Infrastructure/Ai/ProviderAwareDecorator.cs`**
   - 构造参数追加 `double? ollamaTemperature = null`（保持 ≤8 参数约束）。
   - `InjectOllamaStrategy(options, numCtx, temperature)`：先按现值设 `OllamaOption.Temperature`（仅当 `> 0`），
     再无条件 pin `TopP = 0.9` / `TopK = 40` / `RepeatPenalty = 1.1`，最后保持 `NumCtx` / `Think` 原逻辑。
   - 三个 pin 值上方加注释说明"等于 Ollama 服务端默认，显式写出是为了让请求体自解释、并防未来默认值漂移"。
   - 只用 SDK 的 `AddOllamaOption(OllamaOption.X, v)` 扩展点（类注释 13–21 行明确禁止裸 wire key 塞 `AdditionalProperties`）。
   - 温度取值低（0.15）的理由写进注释：0.8 是对话默认值，代码 / tool-call JSON 需要低熵。

### 测试

- **扩展 `src/OneCode.Tests/ProviderWireContractTests.cs`**（复用已有 `CreateOllamaSut(int? numCtx)` 与 `Body(capture)` 夹具）
  - `Ollama_SamplingBaseline_IsSerializedAsNativeOptions`：断言请求体 `options.temperature == 0.15`、
    `top_p == 0.9`、`top_k == 40`、`repeat_penalty == 1.1`。
  - `Ollama_TemperatureOverride_IsHonored`：`CreateOllamaSut(temperature: 0.05)` → `temperature == 0.05`。
  - 反证 `NonOllamaProvider_EmitsNoOllamaOptions`：HTTP provider 路径下这些键**一个都不出现**。
  - 与既有 `Ollama_ReasoningOption_IsSerializedAsNativeThinkOption`（217–230）风格保持一致。
- **配置层测试**：`SettingDescriptors` 新键可被 `/config set ollama.temperature 0.3` 接受并按 `Double` 强制转换；
  非法值走既有 `FormatException` 路径（`ConfigModels.Coerce`）。

### 文档

- **`docs/settings.md`** 四处就地更新：第 52 行"重启后生效"清单、第 151 行 Ollama 配置表加一行、
  第 315 行示例 JSON、第 354 行"配置项与代码的对应关系"表补 `ollama.temperature` 行。
- 在 Ollama 小节注释块补"为什么只有 temperature 可配"，避免后人再加 `top_p` / `top_k` 旋钮。

### 验证

```powershell
dotnet build src/OneCode.slnx
dotnet test src/OneCode.slnx --filter "FullyQualifiedName~ProviderWireContract|FullyQualifiedName~Config|FullyQualifiedName~DocFactConsistency"
```

### 手工验证（本地 Ollama）

设 `provider: "ollama"` + `ollama.temperature: 0.15`，连跑 5 轮含工具调用的对话，抓 Ollama 服务端日志确认
`options` 块含上述四个键；再把温度调到 0.8 对比畸形 tool-call 比例（走 `docs/testing/manual-checklist.md` 对应条目）。

---

## C. 畸形 tool-call 修复（只做入站回放，出站已由框架覆盖）

### 问题

`ConversationMessageMapper.ParseToolArguments`（`src/OneCode.App/Session/ConversationMessageMapper.cs:63-77`）
在 `JsonException` 时只打一条 `LogWarning` 然后 `return null`，于是历史里留下
`FunctionCallContent(callId, name, null)` —— 工具"被调用了但没有参数"，且**永远不会被结果闭合**。
重放这条历史时，协议层看到的是一个开了口子的批次，模型侧只能反复重试或直接崩。

**关键边界：出站畸形调用已经由框架覆盖，不在本项范围。**

- 函数不存在 / 被拒 / 不可调用：MEAI `FunctionInvocationProcessor` 会合成错误 `FunctionResultContent`。
- 参数绑定失败（类型不匹配等）：`SafetyInvariantMiddleware`（`src/OneCode.Infrastructure/Middleware/SafetyInvariantMiddleware.cs`）
  捕获 `ArgumentException` 并返回 `ToolResult.Error("[TOOL ERROR] Tool '…' was called with invalid arguments: …")`，
  模型可自愈——已有测试先例 `src/OneCode.Tests/SafetyInvariantMiddlewareTests.cs:178`。
- 唯一未被修复的路径是**入站回放**（历史里的脏数据），也就是本项。

### 设计要点

- 修复形状必须是"**为该 `callId` 合成一条错误 `FunctionResultContent`**"，不能只是丢掉 call。
  理由：`MessageApiInvariantHelper.GetAtomicGroups`（`src/OneCode.App/Services/Compact/MessageApiInvariantHelper.cs`）
  把"assistant tool-call 消息 + 其后的 ToolResult 连续段"视为**不可分割的压缩组**（注释已写明
  "Even malformed legacy batches stay indivisible during compaction"）。单独丢 call 会撕裂压缩边界，
  合成 result 才能让整组闭合、保持转录自洽。
- 与 provider 无关：畸形调用是**模型能力问题，不是"本地"问题**。不按 provider 分支。
- 有界修复预算：`ToolProtocolException`（`src/OneCode.App/Services/Compact/ToolProtocolValidator.cs`）
  保留为兜底终止条件，在修复预算耗尽后仍然抛出。
- `ToolProtocolValidator` 是 DI **单例**、被 `MainAgentRunner` 与 `BuildRunGate` 共享。
  修复**不得改动单例的内部状态**（不缓存、不重置计数），预算存在调用方。

### 改动清单

1. **`src/OneCode.App/Session/ConversationMessageMapper.cs`**
   - `ToChatHistory` 保持现有主循环，产出消息列表后调用一次收尾修复：
     对每个没有对应 `FunctionResultContent` 的 `FunctionCallContent`，
     按其在消息列表中的位置追加一条合成的错误 result 消息（内容形如
     `"[TOOL ERROR] Arguments for '<name>' were unreadable in session history and have been reset. Re-issue the call with valid arguments."`，
     措辞对齐 `SafetyInvariantMiddleware` 的 `[TOOL ERROR]` 前缀，让模型认得这是可自愈信号）。
   - 抽 `internal static int CloseUnresolvedToolCalls(List<ChatMessage> history, ILogger? logger)`：
     返回合成条数（供预算判定），保证主循环单一职责、类不超行。
   - `ParseToolArguments` 的 `LogWarning` 保留并补上 callId + toolName，便于定位脏数据来源。
2. **修复预算闸门（`src/OneCode.App/Services/Agent/MainAgentRunner.cs:122-131` 现有 gate 附近）**
   - 现有流程 `Validate` → log → `throw new ToolProtocolException` 保持不变。
   - 统计本次历史中由修复合成的 result 数量；超过常量 `MaxReplayedToolCallRepairs`（建议 3）时**不修**、
     直接让校验抛异常——脏历史无限增殖说明会话已不可信。常量与理由写进注释。
3. **仅在确有必要时**在 `src/OneCode.Infrastructure/Agent/AgentPipelineBuilder.cs`（安装顺序 321–448，
   参考 `PromptTooLongRecoveryRunMiddleware` 的 350–352 位次）加一个 function-level 中间件把修复文本回灌给模型；
   如果合成 result 本身已足够（预期如此），**不加**——避免无用中间件。以第 1 步的端到端测试结果决定。
4. **不留兼容层**：不新增"跳过脏 tool-call"的开关或配置项。

### 测试

- **新增 `src/OneCode.Tests/ConversationMessageMapperTests.cs`（新建）**
  - `CorruptToolArguments_AreClosedWithSyntheticErrorResult`：`ToolUseBlock.Input = "{not json"` →
    产出 history 中该 callId 有配对的 `FunctionResultContent`，文本含 `[TOOL ERROR]`；
    **反证**：断言结果里**没有**任何"无参数且无结果"的悬空 call。
  - `ValidToolArguments_AreNotTouched`：正常 JSON 路径不产生任何合成 result（防止过度修复）。
  - `UnclosedCall_AfterRealToolResult_IsNotDuplicated`：已有真 result 的 call 不被追加第二条。
  - `MultipleCorruptCalls_AreAllClosed`：多个脏 call 全部闭合。
  - 断言风格对齐 `SafetyInvariantMiddlewareTests.cs:178`。
- **扩展 `src/OneCode.Tests/ToolProtocolValidatorTests.cs`**
  - `RepairedHistory_PassesValidationClean`：把上一步产出的 history 喂给 `ToolProtocolValidator.Validate`，
    断言不抛 `ToolProtocolException`（证明修复后协议自洽）。
  - `RepairBudgetExceeded_StillThrows`（若第 2 步落地）：脏 call 数量超预算时保留原有抛异常行为。
- **`MessageApiInvariantHelper` 侧**：断言修复后历史的原子组边界与修复前一致（call 与其 result 同组），
  防止将来有人把合成 result 放到别的消息里。

### 文档

- 无独立文档。修复语义写进 `ConversationMessageMapper` 的 XML 注释；
  若第 3 步真加中间件，同步 `docs/maf/integration-guide.md:279` 的扩展点表。

### 验证

```powershell
dotnet build src/OneCode.slnx
dotnet test src/OneCode.slnx --filter "FullyQualifiedName~ConversationMessageMapper|FullyQualifiedName~ToolProtocolValidator|FullyQualifiedName~MessageApiInvariant"
```

### 手工验证

用会产出畸形 tool-call 的小模型（或注入脏 `ToolUseBlock.Input` 的 fixture 会话）重放一次，
确认：TUI 正常进入新一轮、模型收到 `[TOOL ERROR]` 后**重发**该工具调用并成功，而不是卡死或刷屏。

---

## 全量验证（三项都做完后）

```powershell
dotnet restore src/OneCode.slnx
dotnet build   src/OneCode.slnx     # 无新增警告
dotnet test    src/OneCode.slnx     # 全绿
```

外加 `docs/testing/manual-checklist.md` 中与配置重启生效、Ollama 会话、工具调用相关的条目逐项手测。

**不需要**重新生成 `src/OneCode.Tests/ServiceRegistrationSnapshot.approved.txt`：
`MaxOutputTokensDecorator` 与 `ProviderAwareDecorator` 都不是 DI 注册项（分别在
`MainAgentRunner.Pipeline.cs:36` / `TeamAgentFactory.cs:86` 与 `ChatClientFactory.CreateWithDecorators` 内手工组装），
A/B/C 都不新增任何 DI 注册。

## 实施顺序与排期

**顺序 B → A → C**：B 是纯 bug 修复、无设计争议、影响面最小；A 不到 30 行且随时可回滚；C 价值最高但改动面最大。
B 与 A 互不依赖，可并行；C 也独立，可在两者之后单独推进。

| 项 | 风险 | 回滚 |
|---|---|---|
| B | 窗口变小后升级被跳过，理论上少一次"翻盘"机会 | 单参数 `contextWindow`，传 `null` 即完全回到旧行为 |
| A | 低温度可能降低自然语言回复的多样性 | 键可改回 0.8；代码里 3 个 pin 删掉即回到纯服务端默认 |
| C | 合成 result 进入历史会持久化，需确认不会污染真实转录 | 合成 result 只在**回放视图**产出，不写回 session 存储；由 `ToChatHistory` 的只读性质保证 |

## 明确不做

- **D**：用能力 profile 取代散落的 provider 分支——需要独立 ADR 定夺抽象归属。
- **E**：prompt 分层 / 工具 schema 预算——依赖 D 的结论。
- 不为 A 新增 `top_p` / `top_k` / `repeat_penalty` 配置项（已定为代码内 pin）。
- 不为 C 增加"跳过脏 tool-call"的用户可见开关。
