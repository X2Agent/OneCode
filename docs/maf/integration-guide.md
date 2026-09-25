# MAF C# 接入指引

> Microsoft Agent Framework（`Microsoft.Agents.AI*`）的**机制说明 + 正确接入姿势 + OneCode 现状对照**。
> 所有结论以 `agent-framework/` 本地源码为准，不引用官方博客或二手文档。
>
> **边界**：本文不重复决策。「谁拥有 Todo / Compaction / Skills」「禁止自造清单」「提供商请求整形」等**裁决**
> 在 [MAF 集成边界与禁止清单](../adr/0007-maf-integration-boundaries.md)；压缩阈值的产品取值在
> [压缩阈值](../compact-thresholds.md)；技能系统的产品细节在 [技能系统](../skills.md)。
> 本文只回答「框架是怎么运转的、应该怎么接、我们接得对不对」。

---

## 目录

1. [版本与证据边界](#1-版本与证据边界)
2. [核心类型与执行模型](#2-核心类型与执行模型)
3. [两条装配路线](#3-两条装配路线chatclientagent-与-harnessagent)
4. [扩展点全景](#4-扩展点全景)
5. [上下文与聊天历史](#5-上下文与聊天历史)
6. [压缩](#6-压缩)
7. [工具与审批](#7-工具与审批)
8. [Skills](#8-skills)
9. [MCP](#9-mcp)
10. [Harness 其余能力](#10-harness-其余能力)
11. [可观测性](#11-可观测性)
12. [OneCode 现状对照表](#12-onecode-现状对照表)
13. [反模式与升级复查清单](#13-反模式与升级复查清单)

---

## 1. 版本与证据边界

### 1.1 两份 MAF 不是同一份

| 来源 | 标识 | 用途 |
|------|------|------|
| NuGet 包 | `Microsoft.Agents.AI` / `.Workflows` / `.Harness` = **1.22.0**；`.Mcp` / `.Hyperlight` / `.Tools.Shell` = 1.22.0 系列预发布（见 `src/Directory.Packages.props`） | **运行时真正加载的二进制** |
| 本地 checkout | `agent-framework/`，`git describe --tags` = `dotnet-1.22.0-6-g669c8b95e`（即已发布的 `dotnet-1.22.0` 再前进 6 个提交） | 阅读源码、查语义 |

本地 checkout 与 pinned 包**同基线**：均在 `dotnet-1.22.0`（checkout 额外 +6 提交，下列【已核对】路径在该区间内无差异）。
因此每条源码结论仍需标注证据等级，本文采用两档：

| 等级 | 含义 | 覆盖范围 |
|------|------|---------|
| **【已核对】** | 该目录在 `dotnet-1.22.0..HEAD` 内无差异（且 `1.21.0..1.22.0` 亦无改动），源码结论等同 **1.22.0 包**行为 | `Microsoft.Agents.AI.Harness`、`Microsoft.Agents.AI/Harness/{Loop,BackgroundAgents,Todo,FileMemory,FileAccess,FileStore}/**`、`Microsoft.Agents.AI/Compaction/**` |
| **【版本敏感】** | `1.21.0..1.22.0` 区间内有改动（实测：`ChatClient/**`、`Harness/ToolApproval/**`、`Harness/AgentMode/**`、`Skills/**`），结论以 **1.22.0 包**取证为准 | `Microsoft.Agents.AI/ChatClient/**`（审批协议装饰器被 `[BREAKING]` 改写）、`Harness/ToolApproval/**`（`ToolApprovalAgent` 被 `#8432` 重写）、`Harness/AgentMode/**`（`#8458` 新增 `AgentModeProviderOptions`）、`Microsoft.Agents.AI/Skills/**`（frontmatter 校验变严）、`OpenTelemetryAgent.cs`、`AgentExtensions.cs` |

【版本敏感】区域的结论**不能只凭源码断言**，需要在真实包上取证。本文唯一一条会改变接入决策的此类结论是
§6.4 的压缩失效，由 [`HarnessCompactionActivationTests.cs`](../../src/OneCode.Tests/HarnessCompactionActivationTests.cs)
在真实包上跑出（行为未变，失效仍未修复）。其余【版本敏感】描述（审批装饰器内部语义、agent 级遥测细节）
以 1.22.0 包源码与 §13.2 复查清单为准。

### 1.2 包名 ≠ 目录名

`Microsoft.Agents.AI.Harness` 包只有四个文件：

```
agent-framework/dotnet/src/Microsoft.Agents.AI.Harness/
├── HarnessAgent.cs
├── HarnessAgentOptions.cs
├── ChatClientHarnessExtensions.cs
└── FeatureIndex.cs
```

Harness 真正挂载的那些 provider——`TodoProvider`、`AgentModeProvider`、`FileMemoryProvider`、
`FileAccessProvider`、`BackgroundAgentsProvider`、`LoopAgent`、`ToolApprovalAgent`——全部位于
**`Microsoft.Agents.AI` 包的 `Harness/**` 子目录**。找不到类型时按目录找，别按包名找。

### 1.3 实验性 API（`MAAI001`）

大量 API 带 `[Experimental(DiagnosticIds.Experiments.AgentsAIExperiments)]`，编译期报 `MAAI001`。
框架自己在内部调用这些 API 时用 `#pragma warning disable MAAI001` 就地抑制。消费方应当**就地抑制**
（贴在使用处并注明原因），不要在 csproj 里全局 `NoWarn`——全局屏蔽会让「本次升级新增了哪些实验 API 依赖」不可见。

> OneCode 现状与此**相反**：`src/Directory.Build.props` 全局 `NoWarn` 了 `MAAI001`（§12 对照表列为**偏差**）。
> 该全局屏蔽使升级引入的新实验 API 依赖在编译期不可见；逐文件就地抑制是待整改方向。

带 `MAAI001` 的高频类型：`CompactionStrategy` 及全部子类、`CompactionProvider`、`LoopAgent` / `LoopEvaluator`、
`HarnessAgentOptions` 的 `MaxContextWindowTokens` / `MaxOutputTokens` / `CompactionStrategy` / `FileMemoryStore` /
`FileAccessStore` / `BackgroundAgents` / `AgentModeProviderOptions` / `BackgroundAgentsProviderOptions`、
`AIContextProvider.InvokingContext` 构造函数、`AIAgentExtensions.AsIChatClient`。

> 注意：`AgentModeProviderOptions.DisableModeSetTool` / `DisableModeGetTool` 与
> `HarnessAgentOptions.AgentModeProviderOptions` 属性均为 1.22 新增，以 `tag dotnet-1.22.0` 为准。

---

## 2. 核心类型与执行模型

### 2.1 类型地图

| 类型 | 角色 |
|------|------|
| `AIAgent` | 全部 Agent 的抽象基类。提供 `RunAsync` / `RunStreamingAsync` 及会话序列化 |
| `AgentSession` | 一次会话的状态载体（历史引用 + `StateBag`）。**必须由 Agent 创建**，不可跨 Agent 复用 |
| `AgentSessionStateBag` | 会话内的线程安全 KV 状态，序列化时一并落盘。各 provider 的状态都存这里 |
| `ChatClientAgent` | 基于 `IChatClient` 的标准实现 |
| `HarnessAgent` | `DelegatingAIAgent`，把 `ChatClientAgent` 包成「开箱即用的智能体外壳」 |
| `DelegatingAIAgent` | 装饰器基类；`GetService` 会向内层转发 |
| `AgentResponse` / `AgentResponseUpdate` | 非流式 / 流式响应 |
| `AgentRunOptions` / `ChatClientAgentRunOptions` | 单次运行参数 |

> 注意命名：本地源码用的是 **`AgentSession`**（不是早期版本的 `AgentThread`），响应类型是 **`AgentResponse`**
> （不是 `AgentRunResponse`）。老教程里的名字在这个版本上编译不过。

### 2.2 `AIAgent` 的运行入口

`RunAsync` / `RunStreamingAsync` 各有四个重载（无消息 / `string` / 单条 `ChatMessage` / `IEnumerable<ChatMessage>`），
前三个最终都委托到第四个。实现方只需覆写两个抽象点：

```csharp
protected abstract Task<AgentResponse> RunCoreAsync(
    IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct);

protected abstract IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
    IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct);
```

**接入要点**：

- `session` 传 `null` 时框架会**临时建一个**，也就是说这一轮不会有任何跨轮状态。要多轮对话就必须显式传 `AgentSession`。
- `AIAgent.CurrentRunContext` 是 `AsyncLocal<AgentRunContext>`，`RunAsync` 入口设置，流式路径在**每次 `yield return`
  之后重新设置**（调用方代码可能在 `await foreach` 循环体里切换上下文）。框架内部的 `AIContextProviderChatClient`
  与 `PerServiceCallChatHistoryPersistingChatClient` 全靠它拿到当前 agent/session——所以**这些装饰器脱离
  `AIAgent.RunAsync` 直接用 `IChatClient` 调用会抛 `InvalidOperationException`**。
- 框架**不校验也不消毒**任何消息内容。`system` 角色必须完全由开发者控制，`user` / `assistant` / `tool` 一律按不可信处理。

### 2.3 会话生命周期

```csharp
var session = await agent.CreateSessionAsync(ct);              // 只能由 agent 创建
var json    = await agent.SerializeSessionAsync(session, ct: ct);  // 落盘
var restored = await agent.DeserializeSessionAsync(json, ct: ct);  // 恢复
```

序列化产物含会话内容与标识，属敏感数据；反序列化等价于**接受不可信输入**（被篡改的存储可以改写消息角色来提权）。

`AgentSession.StateBag` 是所有 provider 的状态落点。**provider 实例本身不得持有会话态**——同一个 provider 实例
会服务很多会话，写在字段上必然串话。框架的约定是：provider 声明 `StateKeys`，状态存进 `StateBag`。

### 2.4 结构化输出

`AIAgent` 上有一组泛型重载（`AIAgentStructuredOutput.cs`）：

```csharp
AgentResponse<T> result = await agent.RunAsync<T>("...", session, serializerOptions, options, ct);
```

它比在 `ChatOptions.ResponseFormat` 上手写 `ChatResponseFormat.Json` 强：会带上完整 schema 并负责反序列化。
`AgentRunOptions.ResponseFormat` 是另一条更低层的通路，可被任意 `AIAgent` 实现识别（也允许被忽略）。

**OneCode 落地形态**：直连 chat client 的轻量调用采用
**"结构化请求 + 文本降级"** 三段式，统一入口 `Services/StructuredChatCall.cs`：

1. `GetResponseAsync<T>(useJsonSchemaResponseFormat: true)` 请求原生 schema，`TryGetResult` 成功即得类型化结果；
2. provider 忽略 schema（或输出带围栏）时，用**同一响应**的文本走调用方既有解析器（`ExtractJsonBlock` 等），不重试；
3. 结构化调用抛非取消异常时（已知成因：部分 OpenAI 兼容网关拒绝所有 `response_format` 变体），
   去掉 `ResponseFormat` 重试一次，回到 prompt-only 基线——提示词内已含 JSON 契约。

取证要点（影响实现选择，均已核源码）：

- `useJsonSchemaResponseFormat: false` **仍会发送** `response_format: json_object`（M.E.AI `ChatClientStructuredOutputExtensions`），
  对拒绝所有变体的网关依然被拒，因此完整重试必须用不带任何 `ResponseFormat` 的裸调用。
- `ChatResponse<T>.TryGetResult` 失败不抛异常、取末条消息文本、容忍多顶层 JSON、**不剥 markdown 围栏**——与 OneCode 的围栏解析器互补。
- 传入的 `JsonSerializerOptions` **必须显式指定 `TypeInfoResolver`**：`GetResponseAsync<T>` 内部 `MakeReadOnly()`，
  未指定的实例会在那里抛 `InvalidOperationException`，且会被第 3 段的宽捕获吞掉、伪装成"网关拒绝"。
  `StructuredChatCall` 入口有 fail-fast 守卫。

**不适用场景**：带工具循环的 Agent（如 AutoDream 整合 Agent）不设 `ResponseFormat`——schema 会约束循环内
每条助手消息，模型无法在工具调用间自由叙述；且其输出已有消毒 + 配额 + 失败降级链。见[结构化输出文本降级策略](../adr/0008-structured-output-text-fallback.md)。

---

## 3. 两条装配路线：`ChatClientAgent` 与 `HarnessAgent`

### 3.1 反序装配语义（最容易踩的一条）

`AIAgentBuilder.Build` 与 `ChatClientBuilder.Build` **都按注册的逆序应用工厂**：

> **第一个 `Use()` 注册的，是最终管道的最外层。**

```csharp
// AIAgentBuilder.Build
for (var i = this._agentFactories.Count - 1; i >= 0; i--)
{
    agent = this._agentFactories[i](agent, services);
}
```

读装配代码时必须按这个方向翻译，否则对中间件顺序的判断会整个反过来。

### 3.2 路线 A：`ChatClientAgent`

```csharp
AIAgent agent = chatClient.AsAIAgent(
    instructions: "...", name: "...", tools: [tool1, tool2]);

// 或者带完整 Options
AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions { ... });
```

默认情况下 `ChatClientAgent` 会给传入的 `IChatClient` 套一层默认装饰链（`ChatClientExtensions.WithDefaultAgentMiddleware`）：

```
ApprovalResponseBindingChatClient          ← 最外层
  → ApprovalNotRequiredFunctionBypassingChatClient
  → [InvocableFunctionBypassingChatClient]           （EnableInvocableFunctionBypassing 时）
  → FunctionInvokingChatClient
  → [MessageInjectingChatClient]                     （EnableMessageInjection 时）
  → [PerServiceCallChatHistoryPersistingChatClient]  （RequirePerServiceCallChatHistoryPersistence 时）
  → DeferredOpenTelemetryChatClient
  → 叶子 IChatClient
```

想自己完全掌控这条链，就设 `UseProvidedChatClientAsIs = true`，然后自己用 `ChatClientBuilder` 拼——
此时 `AllowConcurrentInvocation` / `DisableApproval*` / `EnableMessageInjection` 等开关**全部失效**，
必须手工调用对应的 `UseXxx()` 扩展。

### 3.3 路线 B：`HarnessAgent`（OneCode 走的这条）

`HarnessAgent` 是一个 `sealed class : DelegatingAIAgent`，它内部 `UseProvidedChatClientAsIs = true` 自行拼装整条链。
**【已核对】** 的确切装配（源码 `HarnessAgent.BuildAgent` / `BuildInnerAgent`；`HarnessAgent.cs` 在 `dotnet-1.21.0..HEAD` 无差异。
注：真正的版本敏感变化在 `Harness/ToolApproval/**`（#8432，重写的是 `ToolApprovalAgent`
内部"已浮出"登记语义，不改变它在这张表里的挂载位置））：

**Agent 装饰层（外 → 内）**

| 顺序 | 装饰器 | 启用条件 |
|------|--------|---------|
| 1（最外） | `LoopAgent` | `LoopEvaluators` 非空 |
| 2 | `ToolApprovalAgent` | `DisableToolAutoApproval != true`（默认启用） |
| 3 | `OpenTelemetryAgent` | `DisableOpenTelemetry != true`（默认启用） |
| 4（最内） | `ChatClientAgent` | 恒有 |

**ChatClient 装饰层（外 → 内）**

| 顺序 | 装饰器 | 启用条件 |
|------|--------|---------|
| 1（最外） | `ApprovalResponseBindingChatClient` | 默认启用 |
| 2 | `ApprovalNotRequiredFunctionBypassingChatClient` | 默认启用 |
| 3 | `FunctionInvokingChatClient` | 恒有，`MaximumIterationsPerRequest` 在此生效 |
| 4 | `MessageInjectingChatClient` | 恒有（Harness 硬编码 `UseMessageInjection()`） |
| 5 | `PerServiceCallChatHistoryPersistingChatClient` | 恒有（Harness 硬编码，且 Options 里写死 `RequirePerServiceCallChatHistoryPersistence = true`） |
| 6 | `AIContextProviderChatClient`（装 `CompactionProvider`） | 有压缩策略时 |
| 7（最内） | `OpenTelemetryChatClient` | `DisableOpenTelemetry != true` |

**默认 Context Provider（按加入顺序）**：`TodoProvider` → `AgentModeProvider` → `FileMemoryProvider` →
`[FileAccessProvider]` → `AgentSkillsProvider` → `[BackgroundAgentsProvider]` → **调用方的 `AIContextProviders`（追加在最后）**。

> OneCode 把审批标记 provider（`ToolApprovalMarkingContextProvider`）放进 `AIContextProviders`，因此它**排在最后**。
> 这不是风格选择：provider 链是**替换语义**（`AIContextProvider.InvokingAsync` 的返回值即下一环的输入，
> 见 `ChatClientAgent.PrepareSessionAndMessagesAsync`），放在前面就看不到后面 provider 追加的工具。

**默认工具**：`HostedWebSearchTool`（`DisableWebSearch` 关闭）。

**指令合成**：`HarnessInstructions`（`null` → `HarnessAgent.DefaultInstructions`）在前，`ChatOptions.Instructions` 在后，
以 `\n\n` 连接。想彻底不要框架指令就传 `string.Empty`（`null` 不等于「不要」）。

### 3.4 该选哪条

| 场景 | 选择 |
|------|------|
| 只要一个会调工具的对话 Agent | `ChatClientAgent` |
| 要审批 / 待办 / 工作记忆 / 技能 / 压缩这一整套"编码智能体"外壳 | `HarnessAgent` |
| 已有 `HarnessAgent`，但想关掉其中几项 | `HarnessAgentOptions.DisableXxx` + 自己的 provider 追加到 `AIContextProviders` |

`HarnessAgent` 的内置 provider 是 `new` 出来的，**没有「按类型从 DI 替换」的入口**。要换实现就是
「`DisableXxx = true` + 把自己的实例追加进 `AIContextProviders`」。这不妨碍用 DI——在 DI 工厂里构造实例再塞进 Options 即可。

---

## 4. 扩展点全景

| 扩展点 | 拦截粒度 | 典型用途 |
|--------|---------|---------|
| `AIAgentBuilder.Use(Func<AIAgent, AIAgent>)` | 整个 agent | 换掉/包住一个 Agent |
| `AIAgentBuilder.Use(sharedFunc)` | 一次 run（流式与非流式共用一个委托） | 前置/后置处理，不关心返回值 |
| `AIAgentBuilder.Use(runFunc, runStreamingFunc)` | 一次 run（两条路径分别实现） | 预算守卫、用量统计、异常恢复 |
| `AIAgentBuilder.Use(functionInvocationCallback)` | **单次函数调用** | 权限、钩子、审计、结果整形 |
| `AIAgentBuilder.UseAIContextProviders(params MessageAIContextProvider[])` | 一次 run | 给**任意** `AIAgent`（不止 `ChatClientAgent`）加消息级上下文 |
| `ChatClientBuilder.Use*` | 单次服务调用 | 提供商整形、重试、遥测 |
| `ChatClientAgentRunOptions.ChatClientFactory` | 单次 run，按请求换 `IChatClient` | 某一轮临时加装饰器 |
| `AIContextProvider` | 一次 run 的前后两阶段 | 注入指令/消息/工具，回收结果 |
| `ChatHistoryProvider` | 一次 run 的前后两阶段 | 自定义历史存储 |

### 4.1 函数调用中间件的硬性前提

```csharp
public static AIAgentBuilder Use(this AIAgentBuilder builder,
    Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>> callback)
```

工厂内部会检查 `innerAgent.GetService<FunctionInvokingChatClient>()`，取不到就**在构建时**抛
`InvalidOperationException`。也就是说：**这类中间件只能挂在已经具备函数调用能力的 Agent 上**
（`HarnessAgent` / 默认装配的 `ChatClientAgent` 都满足；纯远程 Agent 不满足）。

回调必须调用传入的 continuation，除非它打算完全替换该函数的行为。

### 4.2 `AsAIFunction` / `AsIChatClient`

- `agent.AsAIFunction(options, session)` — 把 Agent 暴露成别的 Agent 可调用的工具。返回的 `AIFunction` 是**有状态的**：
  绑定了 agent 与可选 session，**不可并发复用**。它还会把父级 `FunctionInvokingChatClient.CurrentContext` 的
  `AdditionalProperties` 透传给子 Agent。
- `agent.AsIChatClient(session, conversationId, allowNonChatClientAgents)`【版本敏感】— 反向适配。
  默认只接受 `ChatClientAgent`（或能 `GetService<ChatClientAgent>()` 出来的装饰器）。
  传 `allowNonChatClientAgents: true` 包装其它 Agent 时，**除 `ChatOptions.ResponseFormat` 外的所有 `ChatOptions`
  成员都会被静默忽略**（包括 `Tools`、`Instructions`、`Temperature`、`MaxOutputTokens`）。
  绑定了 session 的返回值**一次只能有一个在途请求**，绝不能注册成共享单例。

---

## 5. 上下文与聊天历史

### 5.1 `AIContextProvider` 的两阶段

```
run 开始 → 依次调用每个 provider 的 InvokingAsync（后一个拿到前一个的输出）→ 模型调用 → 依次调用 InvokedAsync
```

绝大多数场景只需覆写两个 `Core` 之下的方法，就能白拿默认的过滤 / 合并 / 来源标注：

| 覆写点 | 你负责 | 框架替你做 |
|--------|--------|-----------|
| `ProvideAIContextAsync` | 返回**增量**上下文 | 过滤输入消息、合并、给消息打来源标注 |
| `StoreAIContextAsync` | 处理本轮结果 | 跳过失败调用、过滤请求/响应消息 |
| `InvokingCoreAsync` / `InvokedCoreAsync` | 全部 | 无（完全接管） |

**默认合并规则**（`AIContextProvider.InvokingCoreAsync`）：

- `Instructions`：`已有 + "\n" + 新增`
- `Messages`：`已有.Concat(新增)`，新增的每条都被打上 `AgentRequestMessageSourceType.AIContextProvider` 来源
- `Tools`：`已有.Concat(新增)`

**默认过滤规则**：`ProvideAIContextAsync` 只看到 `AgentRequestMessageSourceType.External` 的消息
（即调用方原始输入，不含历史与其它 provider 注入的内容）。这一层过滤是刻意的——否则多个 provider 会互相把对方的注入
当成用户输入反复放大。

**状态**：`StateKeys` 默认是 `[GetType().Name]`。同一个 provider 类型在一个会话里出现多次时**必须覆写**，
否则两个实例会争抢同一个 `StateBag` 键。

**安全**：provider 可以注入 **任意角色** 的消息，包括 `system`。框架不做任何过滤。provider 从外部数据源
（向量库、记忆服务）取来的内容是间接提示注入的主要入口，必须由实现方自行校验。

### 5.2 `ChatHistoryProvider`

只在「AI 服务不托管历史」时才有意义。契约与 `AIContextProvider` 对称：`ProvideChatHistoryAsync` 返回历史消息
（框架自动打 `ChatHistoryProvider` 来源标注并拼在调用方消息前），`StoreChatHistoryAsync` 落库。

`ChatClientAgentOptions` 有三个冲突开关，用于「服务其实自己管历史，但你却配了 `ChatHistoryProvider`」这种情况：

| 开关 | 默认 | 行为 |
|------|------|------|
| `ClearOnChatHistoryProviderConflict` | `true` | 把 provider 置空 |
| `WarnOnChatHistoryProviderConflict` | `true` | 记警告 |
| `ThrowOnChatHistoryProviderConflict` | `true` | 抛异常 |

`HarnessAgent` 内部把 `Warn` 与 `Throw` 两个都设成 `false`（源码未注明理由；从机制上看，它硬编码的
per-service-call 持久化必然写出哨兵 ConversationId（见 §6.4），不关掉就会每轮误报冲突）。

---

## 6. 压缩

### 6.1 模型

```
CompactionMessageIndex   把消息切成"原子组"，保证 tool call ↔ tool result 不被拆散
CompactionTrigger        谓词：是否需要压缩（CompactionTriggers.Always / TokensExceed(n) / ...）
CompactionStrategy       压缩算法；带 Trigger 与 Target（Target 默认 = !Trigger）
CompactionProvider       把策略接进 AIContextProvider 管道（run 前压缩）
```

`CompactionStrategy.CompactAsync` 是模板方法，两个提前返回条件写在基类里：

```csharp
if (index.IncludedNonSystemGroupCount <= 1 || !this.Trigger(index)) { /* 跳过 */ }
```

也就是说**只有一个非系统消息组时永远不压缩**——写策略测试时必须喂够消息，否则测的是这条提前返回。

### 6.2 内置策略

| 策略 | 做什么 |
|------|--------|
| `ToolResultCompactionStrategy` | 把旧的 tool call 组折叠成简短摘要（`ToolCallFormatter` 可换） |
| `SummarizationCompactionStrategy` | 调 LLM 生成摘要替换旧消息；默认保留最近 8 组（硬下限） |
| `TruncationCompactionStrategy` | 直接丢弃最旧的非系统组 |
| `SlidingWindowCompactionStrategy` | 滑动窗口 |
| `ChatReducerCompactionStrategy` | 适配 `IChatReducer` |
| `PipelineCompactionStrategy` | 顺序串联多个策略；自身 `Trigger = Always`，每个子策略各自判定 |
| `ContextWindowCompactionStrategy` | 便捷封装：`InputBudget = ctx - maxOut`，按 0.5 / 0.8 两档跑「折叠 → 截断」 |

**安全**：`SummarizationCompactionStrategy` 与 `ChatReducerCompactionStrategy` 会把**外部生成的文本永久写进历史**。
被攻陷的摘要服务可以注入跨轮存活的指令。摘要模型的信任级别必须等同主模型。

### 6.3 三种挂载时机

1. **run 内**（`CompactionProvider`）——每次服务调用前压一次，防止工具循环撑爆上下文。
2. **落库前**——在 `ChatHistoryProvider` 写入前压。
3. **对已有存储做维护**——用静态方法 `CompactionProvider.CompactAsync(strategy, messages, logger, ct)`。

`HarnessAgent` 用的是第 1 种，且**只允许一个 owner**：策略经 `HarnessAgentOptions.CompactionStrategy` 交给 Harness，
它自己 `new CompactionProvider(...)` 挂到 chat client 链上。产品侧再挂一个会导致同一次调用被压两次，且外层状态覆盖内层。

### 6.4 ⚠️ 已验证的失效：Harness 下 in-loop 压缩只跑一次

**这是本文最重要的一条结论，已在真实 NuGet 1.22.0 上取证。**

链路：

1. `PerServiceCallChatHistoryPersistingChatClient` 在「框架自管历史」路径上，每次服务调用后执行
   `session.ConversationId = "_agent_local_chat_history"`（哨兵值，目的是让 `FunctionInvokingChatClient`
   把会话当作服务托管，从而在迭代之间清掉累积历史）。流式与非流式两条路径都会置位。
2. `CompactionProvider.InvokingCoreAsync` 的跳过条件是
   `session.GetService<ChatClientAgentSession>()?.ConversationId` **非空** → 记
   `"session managed by remote service"` 并原样返回。
3. `HarnessAgent` 把 `CompactionProvider` 挂在 `PerServiceCall` 的**内层**，而 `PerServiceCall` 是硬编码启用的。
4. `ChatClientAgentSession.ConversationId` 的 setter 是 `internal`，且**拒绝写入空值**——一旦置上哨兵，
   产品代码无法复位。

结果：**一个会话里，in-loop 压缩只在第一次服务调用时执行，之后永久跳过。**

实测数据（[`HarnessCompactionActivationTests.cs`](../../src/OneCode.Tests/HarnessCompactionActivationTests.cs)）：

| 装配 | 服务调用次数 | 压缩执行次数 |
|------|------------|------------|
| `HarnessAgent`（per-service-call 持久化开启） | 3 | **1** |
| `ChatClientAgent` + `CompactionProvider`（无 per-service-call） | 3 | **3** |

第二行是反证：拆掉 per-service-call 装饰器后，同一策略在每次服务调用上都执行——排除了「消息太少 / 触发器没命中 /
策略只挂了一次」等替代解释，把成因锁定在哨兵上。

**对 OneCode 的影响**：主路径（`AgentPipelineBuilder.BuildHarnessAgent`）显式传入 `ChatHistoryProvider`
（`TranscriptChatHistoryProvider`），因此连 Harness 的兜底也没有——Harness 只有在**自己**构造默认历史
provider 时才会给它装上 `compactionStrategy.AsChatReducer()`。产品既提供了自己的 history provider，
又依赖 in-loop 压缩，两个口子同时落空（`HarnessCompactionActivationTests` 覆盖该行为）。

**产品侧唯一的复位杠杆**：哨兵的序列化位置是会话**根**属性 `conversationId`（不在 `stateBag` 里）。
[`MafSessionInvalidator`](../../src/OneCode.App/Services/Compact/MafSessionInvalidator.cs) 在
`/compact`（full / partial）与 `/checkpoint restore` 等结构性改动后失效快照时，除剪掉 `stateBag` 中的
压缩索引外，还会**定向剔除哨兵本身**——否则「压缩索引被剪掉」与「provider 永不重建它」会同时成立。
真·远端 `ConversationId` 保留不动（服务端托管历史的会话靠它续跑），剔除只按哨兵值定向。
因此：**压缩在会话内只跑一次，但每次结构性历史改动后重新武装一次。**

**可选的应对方向**（尚未立项，此处只列出源码支持的路径，不作裁决）：

| 方向 | 代价 |
|------|------|
| 给产品的 `InMemoryChatHistoryProvider` 配上 `InMemoryChatHistoryProviderOptions.ChatReducer = strategy.AsChatReducer()` | 压缩时机从「每次服务调用前」变成「历史 provider 读取时」，语义不同 |
| 放弃 `HarnessAgent`，自行用 `ChatClientBuilder` 装配并省掉 per-service-call | 要逐项接手 approval binding / bypass / 消息注入 / 逐次历史持久化 |
| 等 MAF 上游修正 `CompactionProvider` 的哨兵判定 | 需要跟踪上游 |

无论选哪条，`HarnessCompactionActivationTests` 都会在行为改变时失败并提示复查。

---

## 7. 工具与审批

### 7.1 工具

工具就是 `Microsoft.Extensions.AI` 的 `AITool` / `AIFunction`，用 `AIFunctionFactory.Create(...)` 构造。
`ChatOptions.Tools` 里放什么，模型就能看到什么。走 §3.2 默认装饰链时，`ChatClientAgent` 还会在构造期把
`ChatOptions.Tools` 赋给 `FunctionInvokingChatClient.AdditionalTools`，使其在整个客户端生命周期内有效；
`UseProvidedChatClientAsIs = true` 的自建链（含 `HarnessAgent`）不做这一步，工具只按常规经 `ChatOptions` 下发。

### 7.2 审批协议

把工具包成 `ApprovalRequiredAIFunction`，它产生的调用就会以 `ToolApprovalRequestContent` 浮出给调用方，
调用方回 `ToolApprovalResponseContent`（或 `AlwaysApproveToolApprovalResponseContent`）。

**必须知道的框架行为：`FunctionInvokingChatClient` 的审批是全有或全无。**
只要同一个模型响应里存在一个 `ApprovalRequiredAIFunction` 调用，该响应里**所有** `FunctionCallContent`
都会被转成 `ToolApprovalRequestContent`——包括本来不需要审批的工具。两个装饰器负责善后：

| 装饰器【版本敏感】 | 解决的问题 |
|-------------------|-----------|
| `ApprovalNotRequiredFunctionBypassingChatClient` | 摘掉「不需要审批却被连坐」的请求，存进 session，下一轮自动以已批准形式回注 |
| `ApprovalResponseBindingChatClient` | 记录框架实际浮出过的每个审批请求，把回来的响应绑定到对应请求上，保证「批准的就是问过的那一个」 |

两者默认启用，**建议保持**。`ApprovalResponseBindingChatClient` 直接关系到人机审批边界的强度。

### 7.3 `ToolApprovalAgent`（Harness 默认装上）

| 能力 | 说明 |
|------|------|
| 常驻规则 | 用户回「以后都允许」后记成 `ToolApprovalRule`，存进 `StateBag`，跨 run 存活。规则分「按工具名」与「工具名 + 精确参数」两级 |
| 排队呈现 | 同一批多个待审批请求会**逐条**呈现，而不是一次全抛给用户 |
| 自动批准规则 | `ToolApprovalAgentOptions.AutoApprovalRules`，在常驻规则之后、询问用户之前求值，**先返回 `true` 的胜出** |
| 迭代上限 | `MaxAutoApprovalIterations`，默认 `ToolApprovalAgent.DefaultMaxAutoApprovalIterations = 40`。到顶后最后一轮不再自动批准，把请求交还调用方 |
| 无 session 时 | 自己建一个——否则自动批准循环重新调用内层 agent 时没有历史可依 |

**为什么需要迭代上限**：每次自动批准后的重试都是一次**全新的**内层 agent 调用，`FunctionInvokingChatClient.MaximumIterationsPerRequest`
每次都会重置，管不住这个外层循环。没有这个上限，一个反复请求「已自动批准工具」的模型可以无限计费。

**自动批准规则的安全陷阱**（源码里以 `<b>Security warning:</b>` 明写）：规则往往**只按工具名匹配**。
为 A 功能写的规则会自动批准**任何**同名工具。注册工具时必须确认名字不会与任何规则撞车。

OneCode 有两类按名规则按这个前提成立：技能的 `load_skill` / `read_skill_resource`，以及 provider 注入的
`todos_*` / `file_memory_*`（名字来自封闭名单，且 MCP 工具经 `mcp__{server}__` 前缀不会撞车）。

### 7.4 审批 vs 权限

MAF 的 ToolApproval 是**协议层**（谁来问、怎么记住、怎么绑定），不是安全门。Allow/Deny 的安全判定属于宿主
（在 OneCode 里是 `IPermissionChecker`），两者不可合并——理由见
[Permission / ToolApproval / Filter 的职责划分](../adr/0001-permission-vs-toolapproval-vs-filter.md)。

---

## 8. Skills

**【版本敏感】** `Skills/**` 在 `dotnet-1.21.0..HEAD` 有改动（frontmatter 校验、缓存说明），以下只描述 HEAD 源码；
1.21.0 行为以 tag 为准。`AgentSkillsProvider` 实现 [Agent Skills 规范](https://agentskills.io/) 的渐进披露：

| 阶段 | 机制 |
|------|------|
| 广告 | 把技能名 + 描述注入系统提示（XML 转义后填进 `{skills}` 占位符） |
| 加载 | `load_skill` 工具返回完整正文 |
| 读资源 | `read_skill_resource` |
| 跑脚本 | `run_skill_script` |

三个工具**默认都被包成 `ApprovalRequiredAIFunction`**（可用 `AgentSkillsProviderOptions` 的
`DisableLoadSkillApproval` / `DisableReadSkillResourceApproval` / `DisableRunSkillScriptApproval` 分别关闭）。
配套提供两个现成规则：

- `AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule` — 只放行 `load_skill` / `read_skill_resource`
- `AgentSkillsProvider.AllToolsAutoApprovalRule` — 连 `run_skill_script` 一起放行

### 8.1 简单构造 vs Builder

简单构造函数（传路径或传技能列表）已经自带 **去重 + 缓存**，并且 `ownsSource: true`：

```csharp
new AgentSkillsProvider(skillPath, scriptRunner, fileOptions, options, loggerFactory)
// 内部 = Deduplicating(Caching(AgentFileSkillsSource(...)))
```

需要「混合来源 / 多个脚本 runner / 过滤」才上 `AgentSkillsProviderBuilder`：

```csharp
var provider = new AgentSkillsProviderBuilder()
    .UseFileSkills(paths)
    .UseFileScriptRunner(runner)
    .UseSkills(inlineSkill)
    .UseFilter((skill, ctx) => ...)
    .UseLoggerFactory(loggerFactory)
    .Build();   // = Deduplicating(Filtering(Caching(Aggregating(...))))，ownsSource: true
```

### 8.2 所有权与释放（易错）

| 构造方式 | `ownsSource` | 谁释放 source |
|---------|-------------|--------------|
| 路径 / 技能列表构造函数 | `true` | provider |
| `AgentSkillsProviderBuilder.Build()` | `true` | provider |
| `new AgentSkillsProvider(source, ...)` | **`false`（默认）** | 调用方 |

另有一条框架不管的：**`ChatClientAgent` 不会释放它的 `AIContextProviders`**。按 run 构建的 provider（技能就是典型）
持有真实资源，必须由宿主在 run 结束后释放——包括取消、异常与流提前退出三条路径。
OneCode 用 [`AgentContextProviderLease`](../../src/OneCode.App/Services/Agent/AgentContextProviderLease.cs) 承担这个职责。

还有一条 builder 的坑：用 `UseSource(AgentSkillsSource)`（实例重载）注册的共享 source 会被**每次 `Build()`
产出的 provider 都视为己有**，先释放的那个会把 source 从其他 provider 脚下抽走。要多次 `Build()` 就用
`UseSource(Func<ILoggerFactory?, AgentSkillsSource>)` 工厂重载。

---

## 9. MCP

`Microsoft.Agents.AI.Mcp` 提供的是**桥接**，不是 MCP 协议实现（协议实现在官方 `ModelContextProtocol` SDK）：

```csharp
IReadOnlyList<AIFunction> tools = await mcpClient.ListAgentToolsWithTasksAsync(options, ct);
```

每个工具被包成 `TaskAwareMcpClientAIFunction`：对接入了
[MCP Tasks 扩展](https://modelcontextprotocol.io/extensions/tasks/overview)的服务器，它会**透明地轮询任务直至完成**；
对普通服务器则按普通内联结果处理。`McpTaskOptions` 控制轮询区间、卡死轮次上限、远端取消超时等，
非法取值在 `ListAgentToolsWithTasksAsync` 入口就抛 `ArgumentOutOfRangeException`。

技能侧的对应入口是 `AgentSkillsProviderBuilder` 的 `UseMcpSkills(client)` 扩展。

**已知缺口**：`TaskAwareMcpClientAIFunction` 没有暴露改名 API，需要给工具加命名空间前缀时只能在外面再包一层
（OneCode 用 `RenamedAIFunction` 产出 `mcp__{server}__{tool}`）。

---

## 10. Harness 其余能力

| Provider / 装饰器 | 启用条件 | 暴露的工具 / 作用 | 状态键 |
|------------------|---------|------------------|--------|
| `TodoProvider` | 默认开，`DisableTodoProvider` 关 | `todos_add` / `todos_complete` / `todos_remove` / `todos_get_remaining` / `todos_get_all` | `"TodoProvider"` |
| `FileMemoryProvider` | 默认开，`DisableFileMemory` 关 | `file_memory_write` / `_read` / `_delete` / `_ls` / `_grep` / `_replace` / `_replace_lines` | `"FileMemoryProvider"` |
| `AgentModeProvider` | 默认开，`DisableAgentModeProvider` 关 | 模式切换（内置 `plan` / `execute`） | `"AgentModeProvider"` |
| `FileAccessProvider` | **opt-in**：设 `FileAccessStore` | 工作目录读写工具 | — |
| `BackgroundAgentsProvider` | **opt-in**：设 `BackgroundAgents`（每个 agent 必须有唯一非空 `Name`） | 让**父模型**自行派发后台任务 | — |
| `LoopAgent` | 设 `LoopEvaluators`（非空） | 反复整轮重跑，直到评估器满意 | — |

几个容易误判的点：

- `FileMemoryProvider` 默认的 store 根在 `{进程当前目录}/agent-file-memory`。多项目 / 会切换工作目录的宿主
  必须显式传 `FileMemoryStore`，否则记忆会跨项目串。
- `BackgroundAgentsProvider` 把「起不起子 Agent」的决定权交给**主对话模型**。要确定性编排就别用它。
  OneCode 的决定见 [子代理派工边界](../adr/0011-background-agents-delegation-boundary.md)：不挂载（门禁绕过 / 审批不转发 /
  DAG 无对应物 / 恢复语义倒置四条硬证据），亦不用作 `AgentTool` 的底层实现——provider 公共面只有
  构造 / `StateKeys` / `GetIncompleteTasks` / `ReleaseSessionAsync`，**没有编程式起任务入口**。
  .NET 侧 `ReleaseSessionAsync` / `GetIncompleteTasks` 均已存在；Microsoft Learn 的 .NET zone
  「宿主端释放不可用」与源码不符，以本地源码为准。
- `LoopAgent` 是**最外层**装饰器，每次迭代都是一次完整 agent run（含审批与遥测）。想做「目标未达成就重跑」的循环，
  用它 + `DelegateLoopEvaluator`，不要自己写 `while (RunStreaming)`。
- `MaxIterations` 是**调用次数**上限，且达到上限时**先停后判**——最后一轮不会进入评估器。
  因此配 3 得到的是 3 次调用 + 2 次完整「评估 → 重试」。OneCode 的 GOAL 子目标与 `/loop` 都保持与配置 1:1，不做 +1 补偿。
- `TodoProvider` 的并发安全靠 per-session 锁（`ConditionalWeakTable<AgentSession, SemaphoreSlim>`）；`FileMemoryProvider`
  用的则是**实例级**写锁（`FileMemoryProvider._writeLock`，单个 `SemaphoreSlim`），写操作在 provider 实例层面全局串行，
  不是 per-session 锁——跨会话写会互相阻塞。
- `TodoProvider` / `FileMemoryProvider` 的工具**由 provider 在请求期注入**，不在装配时交给 agent 的工具目录里。
  要让它们走审批协议（而不是被权限中间件 fail-closed 拒绝），必须在 provider 链末尾补一次标记；
  OneCode 的做法与这两份名单的唯一来源见 [审批边界 ADR](../adr/0001-permission-vs-toolapproval-vs-filter.md)
  的「当前实现形态」。

---

## 11. 可观测性

MAF 有**两级**遥测，都由 `HarnessAgent` 默认开启：

| 层级 | 类型 | 位置 |
|------|------|------|
| Agent 级 | `OpenTelemetryAgent` | agent 装饰链第 3 层 |
| ChatClient 级 | `OpenTelemetryChatClient` / `DeferredOpenTelemetryChatClient` | chat client 链最内层，**位于 `FunctionInvokingChatClient` 之下** |

ChatClient 级刻意放在函数调用之下：这样 chat span 会在工具执行**之前**关闭，
`FunctionInvokingChatClient` 的 `execute_tool` span 才能挂到 agent 这个源上。

默认 `ActivitySource` 名是 `OpenTelemetryAgent.DefaultSourceName`（`"Experimental.Microsoft.Agents.AI"`）。
可以用 `UseOpenTelemetry(sourceName: "...")` 或 `HarnessAgentOptions.OpenTelemetrySourceName` 换名。

> **`ActivitySource` 没有订阅者时 `StartActivity` 返回 `null`，整条遥测链是零开销也零输出的。**
> 「开启了 OpenTelemetry」和「有遥测数据」是两件事——必须在宿主侧
> `TracerProviderBuilder.AddSource(<同名>)`（或注册 `ActivityListener`）才有人消费。

压缩另有独立的 `ActivitySource`（`CompactionTelemetry`），span 上带 `compaction.before.tokens` /
`compaction.after.tokens` / `compaction.before.messages` / `compaction.after.messages` /
`compaction.duration_ms` 等标签，调压缩问题时优先看它。

---

## 12. OneCode 现状对照表

判定三档：**符合** / **偏差**（附改法）/ **缺口**（已验证的问题）。
决策层面的边界见 [MAF 集成边界与禁止清单](../adr/0007-maf-integration-boundaries.md)，此处只做机制对照。

| 主题 | MAF 官方姿势 | OneCode 现状 | 判定 |
|------|-------------|-------------|------|
| Agent 构造 | `HarnessAgent` + Options | [`AgentPipelineBuilder.cs`](../../src/OneCode.Infrastructure/Agent/AgentPipelineBuilder.cs) 单一装配汇合处，profile 驱动 | 符合 |
| 方法命名 | — | `BuildHarnessAgent` 产出 `HarnessAgent`，命名与产物一致 | 符合 |
| Agent 装配汇合 | 单一装配点最省心 | [`AgentPipelineBuilder.BuildHarnessAgent`](../../src/OneCode.Infrastructure/Agent/AgentPipelineBuilder.cs) 覆盖 Main / Forked / Team 成员三条路径；AutoDream 后台服务直接 `new HarnessAgent(...)` + [`OneCodeToolMiddleware.Apply`](../../src/OneCode.Infrastructure/Agent/OneCodeToolMiddleware.cs) | **偏差（有意）**：AutoDream 无交互会话、无审批 broker、无编辑事务，走完整装配只会挂上无意义的门；它复用的是**共享安全门**（安全不变量 + 结果预算）而非装配函数，故两条路径的安全门不会漂移 |
| 中间件顺序 | 第一个 `Use` 最外层 | 源码顺序即装配顺序，注释已写明 | 符合 |
| 函数调用中间件 | 必须挂在有 FICC 的 agent 上 | 全部挂在 `HarnessAgent` 上 | 符合 |
| Harness opt-out | `DisableXxx` + 追加自己的 provider | [`OneCodeHarnessDefaults.cs`](../../src/OneCode.Infrastructure/Agent/OneCodeHarnessDefaults.cs) 设 3 个（AgentMode / AgentSkills / WebSearch），Todo / FileMemory 按 profile | 符合 |
| 实验性 API 抑制 | 就地 `#pragma warning disable MAAI001`，勿在 csproj 全局 `NoWarn` | [`src/Directory.Build.props`](../../src/Directory.Build.props) 全局 `NoWarn` 含 `MAAI001` | **偏差**：全局屏蔽使「升级新增了哪些实验 API 依赖」编译期不可见；方向是逐文件就地抑制（见 §1.3） |
| 待办清单 | `TodoProvider` 五工具 + 每轮注入清单消息 | 原样采用。三分工且有唯一权威：`todos_*` = 模型自查清单；`TaskTool`（get/list/stop/output）= 宿主执行记录；批准计划 Build run 中 `UpdatePlanStep` = 权威跟踪器——分工由 [`PlanExecutionProtocol.ChecklistOwnershipLine`](../../src/OneCode.App/Services/PlanMode/PlanExecutionProtocol.cs) 在每次执行的协议块中固定陈述，否则每轮注入的清单消息会把计划步骤镜像进 `todos_*`（双重记账）或让模型漏报步骤状态 | 符合 |
| 计划/执行循环 | `TodoCompletionLoopEvaluator` + `LoopAgent`（`Modes=["execute"]`，`MaxIterations` 有界）跑到待办清零 | 交互 BUILD 保持单轮（用户旁观流式输出；且 pending tool approval 无人解析时 LoopAgent 会直接停止，见 [`AgentPipelineAssembly.cs`](../../src/OneCode.App/Services/Agent/AgentPipelineAssembly.cs) 注释）；GOAL 子目标用 `LoopAgent` + `DelegateLoopEvaluator` 语义 judge。`TodoCompletionLoopEvaluator` **未接入**（`OneCodeHarnessDefaultsTests` 守卫 `LoopEvaluators` 为 null） | **偏差（有意）**：headless / YOLO 需要「持续运行到待办清零」时应在此接入，交互路径不接 |
| 审批协议 | binding + bypass 默认开 | 均未关闭；`harnessOwnsToolApproval` 阻止第二套 `UseToolApproval` | 符合 |
| 审批标记 | `ApprovalRequiredAIFunction` | [`ToolApprovalMarker.cs`](../../src/OneCode.Infrastructure/Agent/ToolApprovalMarker.cs) 按 `ToolMetadataRegistry` 在装配期包装产品工具目录，[`ToolApprovalMarkingContextProvider.cs`](../../src/OneCode.Infrastructure/Agent/ToolApprovalMarkingContextProvider.cs) 在请求期（provider 链末位）包装 provider 注入的工具；名单与元数据统一由 [`HarnessProviderTools.cs`](../../src/OneCode.Infrastructure/Agent/HarnessProviderTools.cs) 提供 | 符合 |
| 自动批准规则 | 只按工具名匹配，需防撞名 | [`AutoApprovalRulesFactory.cs`](../../src/OneCode.Infrastructure/Agent/AutoApprovalRulesFactory.cs) 从权限模型派生 + 技能只读规则 + provider 注入工具按名放行（[`HarnessProviderTools.AutoApprovalRule`](../../src/OneCode.Infrastructure/Agent/HarnessProviderTools.cs)） | 符合 |
| 迭代上限 | `MaximumIterationsPerRequest` 管 FICC 单请求轮数 | 与 `PipelineOptions.MaxToolCalls` **同一个数值**，但一个管轮数、一个管工具计数 | **符合（有意同源）**：工具调用数 ≥ 迭代轮数，以 `MaxToolCalls` 作迭代上限是保守上界，不会提前截断；避开新增调优参数。代价是两者无法分别调优 |
| 压缩 owner | 单一 owner | 策略经 `HarnessAgentOptions.CompactionStrategy` 交给 Harness，产品不再挂 provider | 符合 |
| 压缩生效 | 每次服务调用前压一次 | **一个会话只压一次**（§6.4，已取证）；`/compact`、`/checkpoint restore` 等结构性改动后由 `MafSessionInvalidator` 定向剔除哨兵重新武装 | **缺口** |
| 摘要守卫 | — | [`GuardedSummarizationCompactionStrategy.cs`](../../src/OneCode.Infrastructure/Agent/GuardedSummarizationCompactionStrategy.cs) 按字符串匹配 MAF 的 `[Summary unavailable]` / `[Summary]` 字面量 | **缺口（框架无公开判据）**：`SummarizationCompactionStrategy.cs:210,215` 为方法内联字面量，无公开常量/标志可替代——守卫是必要适配；MAF 改字面量即静默失效 → 保留在 §13 升级复查清单 |
| Context provider 释放 | 框架不负责 | [`AgentContextProviderLease.cs`](../../src/OneCode.App/Services/Agent/AgentContextProviderLease.cs) 按 run 释放 | 符合 |
| provider 会话态 | 存 `StateBag`，不存实例字段 | [`SessionStateExtensions.cs`](../../src/OneCode.Infrastructure/Agent/SessionStateExtensions.cs) 统一封装 | 符合 |
| 会话持久化 | `SerializeSessionAsync` / `DeserializeSessionAsync` | [`AgentSessionPersistence.cs`](../../src/OneCode.App/Services/Agent/AgentSessionPersistence.cs) + epoch 守卫，压缩后作废旧快照 | 符合 |
| 聊天历史契约 | `ChatHistoryProvider` 承载历史读写 | [`TranscriptChatHistoryProvider.cs`](../../src/OneCode.App/Services/Agent/TranscriptChatHistoryProvider.cs) 桥接会话转录：历史**读**经框架契约（provider 排除本轮已落库的 user 消息），**写**仍归事件溯源转录；宿主不注入历史消息、也不清空框架历史 | 符合 |
| Skills | builder 自带聚合/缓存/去重，`ownsSource: true` | [`SkillProviderFactory.cs`](../../src/OneCode.App/Services/Skills/SkillProviderFactory.cs) 用官方 builder，按 run 构建并经 lease 释放 | 符合 |
| MCP 桥接 | `ListAgentToolsWithTasksAsync` | [`McpConnectionManager.cs`](../../src/OneCode.App/Services/Mcp/McpConnectionManager.cs) 调官方扩展，外包 `RenamedAIFunction` 加前缀 | 符合 |
| FileMemory 根目录 | 默认进程当前目录，多项目须显式指定 | [`FileMemoryStorePaths.cs`](../../src/OneCode.Infrastructure/Agent/FileMemoryStorePaths.cs) 绑定工作目录 | 符合 |
| 可观测性 | 需宿主 `AddSource` 才有订阅者 | Harness 两级遥测均开启，但 `src/` 下**没有任何** `AddSource` / `TracerProvider` / `ActivityListener`，也没有引用 OpenTelemetry 包 | **缺口**：当前 span 全部为 `null`，零输出。要么接一个导出器，要么把「OpenTelemetry 是工具调用可观测性来源」这类注释改成实情 |
| `AsAIFunction` / `AsIChatClient` | 子 Agent 暴露成工具 / 反向适配 | 未使用（子 Agent 走产品 `IAgentRunner` 确定性编排） | 符合（有意为之） |
| BackgroundAgents | `HarnessAgentOptions.BackgroundAgents` / `BackgroundAgentsProvider` | 未采用——不挂载任何路径，也不用作 `AgentTool`/`ParallelAgentsTool` 底层实现（决策与四条硬证据见 [子代理派工边界](../adr/0011-background-agents-delegation-boundary.md)） | 符合（有意为之；[MAF 集成边界](../adr/0007-maf-integration-boundaries.md) 禁令不变） |
| 决策模型（Jev 等） | `IDecisionClient` 决策抽象（MEAI 提案，上游未受理） | 未接入——不引用 MAF `[Experimental(MAAI001)]` 决策类型，也不预依赖未受理的 MEAI API；现有辅助判断（工具短名单、GOAL 子目标判定、记忆相关性）统一复用同一个 `IChatClient` | 符合（有意为之；抽象归属、接入点白名单与重评触发条件见[决策模型接入](../adr/0012-decision-model-integration-jev.md)） |
| 结构化输出 | `RunAsync<T>` / `ResponseFormat` / `GetResponseAsync<T>` | [`StructuredChatCall.cs`](../../src/OneCode.App/Services/StructuredChatCall.cs) 三段式（schema 请求 → 同响应文本降级 → 无格式重试），GoalDecomposer / ClarificationQuestionGenerator 已接入；AutoDream 工具循环 Agent 有意保持 prompt-only | 符合（见 §2.4 与[结构化输出文本降级策略](../adr/0008-structured-output-text-fallback.md)） |
| 循环上限 | `LoopAgentOptions.MaxIterations` 限制 agent **调用次数** | GOAL 子目标 1:1 映射 `GoalLoopDefaults.MaxAttemptsPerSubGoal`；`/loop` 1:1 映射 `loop.maxIterations`。上限强停发生在评估**之前**，因此 N 次调用 = 最多 N-1 次完整“评估→重试” | 符合（语义已在校验层与文档中固化，见[代理循环边界](../adr/0010-agent-loop-boundaries.md)） |
| Loop 装配层序 | 用 `HarnessAgentOptions.LoopEvaluators` 时 `LoopAgent` 由 Harness 挂在**最外层**（`HarnessAgent.cs` 先注册该装饰器 → 位于 Harness 自身 `UseToolApproval` + `UseOpenTelemetry` **之上**，即每轮独立审批、独立 trace） | 不设 `LoopEvaluators`（[`OneCodeHarnessDefaultsTests`](../../src/OneCode.Tests/OneCodeHarnessDefaultsTests.cs) 守卫为 null）；`/loop` 与 GOAL 子目标在 [`AgentPipelineBuilder.Build()`](../../src/OneCode.Infrastructure/Agent/AgentPipelineBuilder.cs) 的 run middleware **之外**自建 `LoopAgent`，层级为 `LoopAgent → BudgetGuard / UsageTracking / … → HarnessAgent → FICC` | **偏差（有意）**：预算按**轮**校验，比 MAF 原生路径更严；代价是 `LoopAgent` 落在 Harness 遥测覆盖之外，且内层 agent 以 `SuppressToolApproval` 装配（无审批中间件、无原生审批标记），自主性由 [`LoopCommand`](../../src/OneCode.App/Commands/LoopCommand.cs) 的自主权限模式门 + `PermissionChecker` 保证 |

---

## 13. 反模式与升级复查清单

### 13.1 框架层面的反模式

| 反模式 | 后果 |
|--------|------|
| 在 `AIContextProvider` / `ChatHistoryProvider` 的实例字段里存会话态 | 多会话串话。状态一律进 `StateBag` |
| 同一 provider 类型挂多个实例却不覆写 `StateKeys` | 两个实例抢同一个状态键 |
| 同时挂两个 `CompactionProvider` | 同一次调用压两次，外层状态覆盖内层 |
| Harness 已有审批时再 `UseToolApproval` | 两套排队与常驻规则互相干扰 |
| 脱离 `AIAgent.RunAsync` 直接调用带 `AIContextProviderChatClient` / `PerServiceCall` 的 `IChatClient` | `CurrentRunContext` 为空 → `InvalidOperationException` |
| 自动批准规则依赖工具名唯一性却不校验撞名 | 同名工具被无声放行，绕过人机审批边界 |
| 把 session 绑定的 `AsIChatClient` 注册成共享单例 | 并发不支持 + 历史跨用户泄漏 |
| 用 `HarnessInstructions = null` 表达「不要框架指令」 | `null` 意为「用 MAF 默认」；要空须传 `string.Empty` |
| 在 `AdditionalProperties` 里手写提供商线协议键 | 适配器根本不读，功能从未生效而测试全绿（详见[集成边界文档](../adr/0007-maf-integration-boundaries.md)） |
| 自己写 `while (RunStreaming)` 外层目标循环 | 已有 `LoopAgent` + `LoopEvaluator` |

### 13.2 MAF 升级复查清单

升级 MAF 版本时逐条执行。当前 pinned 为 `dotnet-1.22.0`，以下条目即按它复查：

1. **核对实际版本**：读 `project.assets.json`，不要用旧 artifacts 或本地 NuGet 缓存代替。
2. **审批协议**【版本敏感】：重跑「暂停 → 恢复 → 审批响应绑定」与「只回显响应、不回放请求」两组用例
   （`TeamToolApprovalBridgeTests`）。1.22（`#8432`）把「只承认框架自身记录过的请求」作为授权依据（1.21.0
   仍允许回放历史请求），并**同时**改写了 `ToolApprovalAgent` 与 `ApprovalResponseBindingChatClient` 两侧的
   "已浮出"登记语义（整批登记 → 只登记真正交给调用方者；上限轮返回前补登记），用例必须覆盖上限轮与排队出队
   两条路径，不能只测 binding 一侧。
3. **压缩装配**：重跑 [`HarnessCompactionActivationTests.cs`](../../src/OneCode.Tests/HarnessCompactionActivationTests.cs)。
   若失败，说明上游改了哨兵判定 —— 这是**好消息**，需要把断言从「只压一次」改成「每次都压」。
4. **摘要守卫字面量**：核对 MAF `SummarizationCompactionStrategy` 的 `[Summary unavailable]` / `[Summary]`
   是否仍是原样，以及空摘要 / 超长摘要是否仍按原语义提交。变了就同步
   [`GuardedSummarizationCompactionStrategy.cs`](../../src/OneCode.Infrastructure/Agent/GuardedSummarizationCompactionStrategy.cs)。
5. **装饰链顺序**：对照本文 §3.3 的两张顺序表重读 `HarnessAgent.cs`，确认层数与相对位置没变。
6. **默认 provider 集合**：确认 `HarnessAgent` 的 `BuildContextProviders` 没有新增默认 provider
   （新增的会自动挂到所有 profile 上）。
7. **Options 字段增删**：`HarnessAgentOptions` / `ChatClientAgentOptions` 新增开关时，评估默认值是否符合产品预期
   ——MAF 的默认普遍是「开」。1.22 已知新增：`AgentModeProviderOptions`（`DisableModeSetTool` /
   `DisableModeGetTool`，与 [MAF 集成边界](../adr/0007-maf-integration-boundaries.md)「AgentMode 归宿主」同向，
   可评估替换自建 opt-out）；`Skills/**` 的 frontmatter 校验与缓存隔离键（回归 `SkillDiscoveryRuleTests`）。
8. **状态键**：确认 provider 的 `StateKeys` 没变，否则已持久化的会话恢复后状态丢失。
9. **遥测**【版本敏感】：`OpenTelemetryAgent` 与 `AgentExtensions` 在 HEAD 已有改动，agent 级遥测结论需重新核对。
10. **BackgroundAgents**：核对 `Harness/BackgroundAgents/**` 的工具名（`background_agents_*`）、状态键与
    `ReleaseSessionAsync` 语义是否变化；**并确认上游是否暴露编程式任务入口**（如 `StartTaskAsync`）——
    若暴露，按 [子代理派工边界](../adr/0011-background-agents-delegation-boundary.md) 的后续工作定义重评 F4-F6 能否改用框架能力。
11. **决策模型 API**：核对 `Microsoft.Extensions.AI.Abstractions` 与 `Microsoft.Agents.AI.Abstractions`
    是否出现 `IDecisionClient` / `DecisionQuestion` / `DecisionResponse` 等决策类型（按 pinned 程序集逐符号计数，
    勿用本地 checkout 为已发布包行为作证）。一旦出现即按
    [决策模型接入](../adr/0012-decision-model-integration-jev.md) §决策 6 重评，并删除该 ADR §决策 1 的自持契约、
    改用上游类型。

---

## 参考

- MAF 源码：`agent-framework/dotnet/src/`（只读 checkout，版本见 §1.1）
- 集成决策与禁令：[MAF 集成边界与禁止清单](../adr/0007-maf-integration-boundaries.md)
- 后台响应（Background Responses）评估：[暂不集成，方案存档](../adr/0009-background-responses-assessment.md)
- 结构化输出采用策略：[结构化请求 + 文本降级](../adr/0008-structured-output-text-fallback.md)
- 审批与权限的职责划分：[Permission / ToolApproval / Filter](../adr/0001-permission-vs-toolapproval-vs-filter.md)
- 压缩阈值取值：[压缩阈值](../compact-thresholds.md)
- 技能系统：[技能系统](../skills.md)
- 团队编排模式：[Team 编排模式选型](../team-modes.md)
