# OneCode Hook 系统设计与扩展性方案

> 定位：设计文档（非实施计划）。所有结论已逐文件对照源码核实。
> 相关代码：`src/OneCode.Core/Hooks/`（契约）、`src/OneCode.App/Services/Hooks/`（实现）、`src/OneCode.Infrastructure/Middleware/HookMiddleware.cs`（MAF 集成）、`docs/hooks.md`（现有文档）。

---

## 一、现状分析

### 1.1 架构概述

「事件 × 执行器」二维生命周期钩子系统：

```
hooks.json（用户级 priority 100 / 项目级 priority 200 双层覆盖）
  → HookConfigBootstrapper 启动加载 → HookRegistry（(Event, Matcher) 二维索引，lock 保护线程安全）
  → 业务触发点调用 IHookExecutionService.FireAsync(payload, matcherValue)
      ├─ HookPolicyService 工作区信任门控
      ├─ GlobHookMatcher 两维过滤 → Priority 升序串行执行
      ├─ 按 HookType 分发 IHookExecutor（Command / Notification / Http）
      └─ HookResultAggregator 聚合 → once hook 清理
```

### 1.2 设计优点（应保持的部分）

| 优点 | 说明 |
|------|------|
| 二维模型正交 | 「何时触发」（事件+matcher）与「如何执行」（HookType）解耦 |
| 声明式配置 | hooks.json 双层覆盖，与 prompts 三层覆盖模式一致 |
| 安全门控前置 | 不受信工作区全部静默跳过，防恶意仓库投毒 |
| 单 hook 故障隔离 | 单条未知类型已优雅跳过（`HookConfigBootstrapper.cs:59-73`），Bootstrap 外层另有兜底 catch（`AppStartupService.cs:51-60`） |
| Core 契约下沉 | `IHookExecutionService` 在 Core，Infrastructure 可注入而不反向依赖 App |
| 结果聚合语义清晰 | 布尔 OR / 列表累加 / 字符串 last-write-wins |

### 1.3 问题清单（已对照源码核实）

#### A 级：正确性缺陷（实现与承诺不符）

| # | 问题 | 位置 |
|---|------|------|
| A1 | **Stop 阻断未落地**。docs 承诺 exit code 2 可阻止停止，但 `FireHookAsync` 丢弃返回值；且 Stop 触发于 transcript 持久化之后（`FinalizeAsync:388`），纠偏续跑需重构终结段 | `QueryStreamEngine.Tools.cs:190-201`、`QueryStreamEngine.cs:367-410` |
| A2 | **`HookEvent.Notification` 死事件**。枚举与元数据存在，零触发点 | `HookEventMetadata.cs:33-36` |
| A3 | **UserPromptSubmit 语义偏差**。主对话路径无真实触发点；唯一调用是 GoalDecomposer 内部阶段（decompose/replan/sub-decompose 的 LLM 调用感知），且聚合结果（additionalContext）被丢弃 | `GoalDecomposer.cs:340-358` |
| A4 | **once hook 失败也被移除**，重试语义丢失 | `HookExecutionService.cs:61-67` |
| A5 | **StopFailure 的 matcher 完全失效**。`FireHookAsync` 不传 matcherValue → 配置 `"matcher": "rate_limit\|server_error"` 的告警 hook 永不匹配；payload 无 error 分类字段，runException 未映射即丢弃。元数据宣称的 7 个错误类别全部无法工作 | `QueryStreamEngine.cs:357-361` |
| A6 | **Pre/PostCompact 的 matcher 同样失效**。两处触发均不传 matcherValue，`"matcher": "manual"/"auto"` 形同虚设；CompactService 无法区分手动/自动来源（`CompactAsync` 无 trigger 参数）；另 PostCompact payload 未填 Cwd（`{{Cwd}}` 渲染为空） | `CompactService.cs:157-178`、`PromptTooLongRecoveryRunMiddleware.cs:99-104,148-153` |
| A7 | **SessionEnd 覆盖缺口**。全仓库仅显式 close 一处触发（固定值 `"close"`）；TUI 正常退出/进程结束路径完全不触发——依赖 SessionEnd 的场景（如会话日报推送）不可靠 | `SessionManager.cs:446` |
| A8 | **元数据与实现漂移**。SessionStart 元数据列 `startup/resume/clear/compact`，实际触发值为 `startup/resume/switch`（clear/compact 从未触发）；SessionEnd 元数据列 4 种 reason 全部不存在；docs/hooks.md:181 写 decompose/judge，实际 stage 名为 goal-decomposer/goal-replanner/goal-sub-decomposer | `HookEventMetadata.cs:40-43,59-62` |

> 根因归纳：A1/A5/A6/A3 同源——**触发方与 FireAsync 之间没有「matcher 值必须传递」的契约**，各业务点各自为政；工具类事件（Pre/PostToolUse，经 HookMiddleware 统一入口）是唯一 matcher 正确工作的路径。

#### B 级：扩展性瓶颈（本方案核心议题）

| # | 问题 | 位置 |
|---|------|------|
| B1 | **执行器分发硬编码，违反 OCP**。三个 `[FromKeyedServices]` 固定注入，新增 HookType 改 4 处，与 src/AGENTS.md §11.4 冲突；`NotificationHookExecutor` 的 IEnumerable 注入已是正确示范 | `HookExecutionService.cs:19-34` |
| B2 | **通知渠道接入需改源码**。新渠道须写 C# + 改 DI + 重编译；IM 机器人差异仅在 payload 格式与签名算法，可配置化 | `ServiceCollectionExtensions.Business.cs:193-196` |
| B3 | **签名算法不可组合**。HMAC 变体散落在各 Provider 的 `ComputeSign`（飞书 key=`timestamp+"\n"+secret`/msg 空、企微 key=secret/msg=`timestamp+"\n"+secret`），基类 `BuildSignedUrl` 只支持固定 query 放置 | `WebhookNotificationProviderBase.cs:134-145` |
| B4 | **Plugin 优先级段（300+）有名无实** | `docs/hooks.md` §9.1 |

#### C 级：健壮性与体验短板

| # | 问题 | 位置 |
|---|------|------|
| C1 | webhookUrl / secret 明文存储 | `HookSettingsConfig.cs:57-63` |
| C2 | HTTP 出站无 resilience 策略 | `HttpHookExecutor.cs` 等 |
| C3 | 无热重载；幂等仅在 `AppStartupService._warmedUp`（`:28,32`），Bootstrapper 自身重复调用会重复注册——热重载必须整体重建 | `AppStartupService.cs:53` |
| C4 | 串行执行阻塞主流程，无逃生通道（ADR 移除异步队列属有意取舍） | `HookExecutionService.ExecuteAndAggregateAsync` |
| C5 | `shell` 字段被忽略，Windows 固定 cmd.exe | `CommandHookExecutor.cs:110-114` |
| C6 | 用户级 hook 也受项目工作区信任门控，全局 hook 在陌生目录失效 | `HookPolicyService.cs:18-29` |
| C7 | 模板字段贫乏。归因注意：`ToolError` 已在 Payload（`HookPayload.cs:16`），缺口在渲染器白名单（8 字段）；`{{DurationMs}}` 类需新增字段+触发点填充 | `HookTemplateRenderer.cs:35-46` |
| C8 | 无执行历史/计数，排障靠翻日志 | `HooksCommand.cs` |
| C9 | 静默跳过无诊断：未知事件名被 `Enum.TryParse` 静默丢弃、文件级失败仅一条 Warning | `HookSettingsLoader.cs:43-57` |

---

## 二、优化建议

按「先正确、再扩展、后体验」排序。

### P0 正确性修复

1. **A1 → Stop 纠偏续跑**（已决策，见 §3.1 方案细节）。
2. **A5/A6 → matcher 值传递契约修复**（一次修一类根因）：
   - `FireHookAsync` 增加带 `actualMatcherValue` 与 payload 扩展字段的变体；
   - StopFailure：新增轻量异常→类别映射器（rate_limit/auth_failed/billing/invalid_request/server_error/max_output_tokens/unknown，参考 HTTP status + 异常类型；若 `MainAgentRunResult.Error`(AgentProblemDetails) 可得则优先取其 type），类别同时进 matcher 值与 payload；
   - Compact：`CompactAsync` 增加 `trigger` 参数（manual/auto 由调用方传入——`/compact` 命令为 manual，AutoCompactService 路径为 auto），透传给两个 Fire 点并补 PostCompact 的 Cwd；
   - PromptTooLongRecovery 的 compact 触发同样传 `auto`。
3. **A3/A2 → 事件接线与语义修正**：
   - 主路径 UserPromptSubmit 触发点定在 `QueryStreamEngine.StreamInteractiveAsync:107-128`（原始文本 `userPrompt` 在手、historyMessages 装配权在手，可将 `AdditionalContexts` 以 system/user 消息并入本轮输入）；不复用丢弃返回值的 helper；
   - GoalDecomposer 迁移到新枚举成员 `GoalStageInvoke`（stage 名作 matcher 值；同步元数据注册表，格式见 `HookEventMetadataRegistry.All`；全库无 HookEvent 穷举 switch，改动面可控）；
   - Notification 事件先落地权限挂起点：`ApprovalBroker.ForQuery:39-49`（Main 路径咽喉，ToolName 在手，matcher 值=`permission_prompt`）；idle_prompt 需新增空闲计时（无现成检测），降级为后续项。
4. **A4**：once 仅在成功后移除。
5. **A8**：元数据注册表与 docs 同步修正（SessionStart 实际值集含 switch；StopFailure 类别对齐新映射器输出；GoalStageInvoke 条目；docs/hooks.md 事件表全量复核）。
6. **C9**：加载诊断补齐（未知事件名 Warning 含字段名；文件级失败升级为 `/hooks` 概览可见状态；可选：日志补行号）。

### P1 扩展性与安全（主体见第三章）

7. **B1**：`HookExecutionService` 改注入 `IEnumerable<IHookExecutor>` 自建字典（照抄 `NotificationHookExecutor` 模式）。保留枚举分发键。
8. **B2/B3**：声明式 Provider 定义（§3.3）。
9. **A7 → SessionEnd 覆盖扩展**：应用退出生命周期（IHost stopping / TUI exit path）补发 SessionEnd（reason=prompt_input_exit/other），使会话日报类场景可靠。
10. **C1/C2**：`${ENV_VAR}` 展开 + `dpapi:` 前缀；HTTP 客户端挂 `AddStandardResilienceHandler`（重试 3 次、指数退避、熔断 0.5）。

### P2 体验增强

11. **C3**：热重载——FileSystemWatcher 参考 `CodeIndexHotReloader`（Timer 去抖 + pending 合并 + 可重入生命周期），防抖后整体重建 Registry。**硬约束：禁止增量重调 Bootstrap**（无幂等保护，会重复注册）。
12. **C4**：`"async": true` opt-in 后台有界队列（容量 32，溢出丢弃记 Warning）；结果不参与聚合阻断。
13. **C5**：`shell` 字段生效（缺省维持现状）。
14. **C7**：模板扩充分两类——`{{ToolError}}` 只扩渲染器白名单；`{{DurationMs}}/{{TerminalReason}}/{{Model}}` 需新增 Payload 字段并在触发点填充。
15. **C8**：环形缓冲 100 条执行记录，`/hooks history`、`/hooks providers` 子命令。
16. **C6**：`allowUserHooksInUntrustedWorkspace` 开关（默认 false），仅豁免通知/http 类。

---

## 三、核心设计

### 3.1 Stop 纠偏续跑（P0-1，已决策方案 B）

**选型**：engine 层方案（不动 MainAgentRunner）。理由：runner 层方案需给已依赖 13+ 服务的 MainAgentRunner 再注入 hook 服务，且 Stop 触发时机前移偏离现有次序；engine 层改动局部、hook 归属一致。

```
StreamCoreAsync 尾段改造：
while (true)
{
    新建 Channel + runTask（每轮必须新建——runner 结束即 TryComplete）
    StartRun → AwaitRunAsync
    若失败走 OnRunFailedAsync 后 break
    FinalizeAsync 拆分：持久化/Plan 收尾照旧，
    Stop hook → BlockingErrors 为空 ? break（继续原终结流程）
              : 连续阻断数 < 上限 ? currentFeedback = stderr 文案; continue（以增量 user 消息 + 同一 session 重入）
              : 转 TUI 警告后 break
}
```

- **死循环防护**：连续阻断上限常量 3，超限转 TUI 警告并照常结束；
- 反馈消息追加为 `ChatRole.User`（复用 runner 审批续跑的既有模式，`MainAgentRunner.cs:166-214` 已验证该路径）；
- TUI 展示阻断原因；
- 顺带收益：将 `ResolveTerminalOutcome` 提前至 Stop 触发前，TerminalReason 进入 payload 并作为 matcher 值（支撑 §四 S11 替代方案）。

### 3.2 分层接入能力矩阵

| 接入方式 | 适用场景 | 用户成本 | 本期 |
|---------|---------|---------|------|
| `http` 执行器 | 无签名/简单 Header 签名的 webhook、CI 触发 | 纯配置 | 已有 |
| **声明式 Provider** | 钉钉/Slack/Discord/自建网关等「固定端点+特定 payload+签名变体」 | 纯配置 | **新增** |
| 编译型 `INotificationProvider` | OAuth、多步交互等复杂集成 | C# + 1 行 DI | 已有，成本因 B1 降低 |
| `command` 执行器 | 本地脚本、CLI 工具 | 纯配置 | 已有 |

> 明确不做 DLL 插件加载（已决策）。二进制级扩展由编译型 Provider 承担。

### 3.3 声明式 Provider（实现级设计）

#### 配置文件与覆盖

三层：`<AppContext.BaseDirectory>/notification-providers.json`（内置默认，Content 打包）< `~/.onecode/` < `<cwd>/.onecode/`。同名**整条替换**（高层胜出，Info 日志；非字段合并）。由 Bootstrapper 同批加载；热重载同 hooks.json。

#### Schema（以钉钉为例）

```jsonc
{
  "dingtalk": {
    "displayName": "钉钉机器人",
    "method": "POST",
    "url": "https://oapi.dingtalk.com/robot/send",
    "body": { "msgtype": "text", "text": { "content": "{{Text}}" } },
    "signing": {                        // 或 {"preset":"dingtalk"} 糖
      "keyTemplate": "{Secret}",
      "messageTemplate": "{Timestamp}\n{Secret}",
      "encoding": "base64-urlencoded",  // base64 | hex | base64-urlencoded
      "placement": "query",             // v1 实现 query | header；body-field 预留
      "paramName": "sign",
      "timestampParamName": "timestamp"
    },
    "success": { "field": "errcode", "equals": 0 }   // 缺省仅看 HTTP 2xx；v1 仅顶层字段比对
  }
}
```

- **签名原语库**（已对照内置实现核实）：飞书=key=`{Timestamp}\n{Secret}`/msg 空/base64；企微=key=`{Secret}`/msg=`{Timestamp}\n{Secret}`/base64；钉钉同企微但 base64-urlencoded。preset 名固定三选一。变量集 v1 仅 `{Secret}/{Timestamp}`（Unix 秒），禁止表达式求值——配置不能成为代码执行通道。
- **两级渲染**：第一级（executor 层）`config.Message` 经 `HookTemplateRenderer` 以 Payload 字段渲染 → `Text`（该机制 http 类型已在 URL/Header/Body 三处使用，`HttpHookExecutor.cs:49,74,80`，引擎直接复用）；第二级 body/headers 模板以 Payload 字段 + `{Text}/{Title}` 渲染。签名模板用单花括号，命名空间隔离。
- **运行时形态**：`NotificationProviderDefinition`（Core record 集，入 `HookSerializerContext` 源生成）→ `NotificationProviderDefinitionLoader`（三层加载+校验：url 必须 https、缺 body 且无 preset 跳过+Warning、未知 preset 跳过+Warning）→ `NotificationProviderRegistry`（定义优先、编译型兜底，同名声明式胜出）→ `DeclarativeNotificationProvider : INotificationProvider`（唯一引擎类，复用 `WebhookNotificationProviderBase` 传输骨架；其 `BuildSignedUrl` 泛化以支持自定义参数名与 header 放置）。
- **迁移路径**：feishu/wechat_work 以签名向量测试证明逐字节一致后迁为内置默认 JSON 定义，编译类删除；独立 PR，本期双轨并存。

---

## 四、使用场景全景

| # | 场景 | 事件 × 执行器 | 现状 |
|---|------|--------------|------|
| S1 | 任务完成 IM 通知（飞书/企微/钉钉/Slack） | Stop + notification | ✅ 前两者；后两者待声明式层 |
| S2 | 失败告警值班 | StopFailure + notification | ⚠️ 依赖 P0-2（error matcher 当前失效） |
| S3 | 危险命令阻断 | PreToolUse + command(exit 2) | ✅ |
| S4 | 合规审计留痕 | PostToolUse(matcher *) + http | ✅ |
| S5 | CI/CD 联动 | SessionStart / Stop + http | ✅ |
| S6 | 写后自动 format/lint | PostToolUse(Write\|Edit) + command | ✅ 组合即得 |
| S7 | 会话结束日报推送 | SessionEnd + command | ⚠️ 依赖 P1-A7（退出路径当前不触发） |
| S8 | 外部知识注入（stdout additionalContext 回注） | UserPromptSubmit + command | ⚠️ 依赖 P0-3 |
| S9 | 权限审批推送 IM | Notification(permission_prompt) | 🔜 P0-3 落地出站侧；闭环回传远期 |
| S10 | Stop hook 纠偏（未跑测试则强制继续） | Stop 阻断 + command | 🔜 P0-1 落地后可用 |
| S11 | ~~成本阈值告警~~ → **预算终止通知** | Stop(matcher=budget_exceeded) + notification | 见下方决策 |
| S12 | 子代理完成通报 | 🆕 SubagentStop（远期） | ❌ |
| S13 | 指标导出 OTLP/PushGateway | Stop/PostToolUse + http | ✅ 组合即得 |

**关于费用事件的决策（D5）：不新增独立的 CostThresholdExceeded 事件。**

依据（已核实现有设施）：
1. **预算强制已存在**：主对话 `MaxBudgetUsd` + BudgetGuard 中间件短路（终因标记 budget_exceeded，`QueryStreamEngine.ResolveTerminalOutcome:486-491`）；GOAL 模式 `goal.maxCostUsd`（默认 $5）+ 双级预警 TUI 事件。超限必然走到回合结束 → **Stop 事件天然覆盖**。
2. **缺口只是「可区分原因」**：当前 Stop payload 无 TerminalReason，无法写「仅预算耗尽时通知」。修复方式廉价：P0-1 顺带把 TerminalReason 作为 Stop 的 matcher 值与 payload 字段，S11 即变为一条普通配置（`"stop":[{"matcher":"budget_exceeded","hooks":[...notification...]}]`）。
3. **精度边界如实标注**：token 维度计量精确；USD 维度依赖 ModelCatalog 定价表（`CostTracker.SyncPricingFromCatalog` 已支持热同步，Automation 层定期刷新），自定义代理/未收录模型价格可能缺失或过期——这正是用户反馈「费用计算不准」的来源。在定价精度改善之前，不宜让自动化链路依赖美元阈值；token 维度如需告警可用同一模式扩展（远期）。

---

## 五、安全考量

| 关注点 | 设计 |
|--------|------|
| 配置≠代码通道 | 签名仅固定变量集+参数化模板；URL 仅 https |
| SSRF | providers 文件受工作区信任门控（同 hooks.json） |
| 密钥 | `${ENV_VAR}` 展开 + `dpapi:` 保护值；建议 .gitignore，文档明确不入库 |
| 覆盖攻击 | 项目级覆盖用户级 Provider——受信前提下可接受（风险面与项目级 hooks 一致），文档说明 |
| 重试放大 | resilience 上限 3 次 + 总超时，防故障渠道雪崩 |
| 纠偏死循环 | 连续阻断上限 3 次 |

## 六、决策记录

| # | 事项 | 结论 |
|---|------|------|
| D1 | 扩展机制 | 两层递进（OCP + 声明式 Provider），不做 DLL 插件 |
| D2 | Stop 阻断处置 | 纠偏续跑（含防护），非删文档/仅提示 |
| D3 | Provider 覆盖语义 | 整条替换，高层胜出 |
| D4 | 纠偏续跑落点 | engine 层方案（每轮新建 Channel/runTask），不动 MainAgentRunner 构造 |
| D5 | 费用事件 | 不新增；Stop+TerminalReason matcher 覆盖，精度边界如实标注 |
| 开放 | async hook vs ADR 张力 | opt-in + 不参与聚合规避；落地前补 ADR 附录 |
| 开放 | 入站 webhook（审批闭环） | 出站 only 本期范围外，未来独立模块 |

## 七、验证计划

1. **签名向量测试**：飞书/企微/钉钉官方示例（固定 timestamp+secret）逐字节断言——同时守护内置 Provider 与 Declarative 引擎/preset 展开；
2. **Loader 测试**：三层整条替换、无效定义跳过不影响他条、非 https 拒绝、preset 展开；
3. **Registry 测试**：同名解析顺序（定义>编译型）、实例缓存；
4. **Declarative 端到端**：fake HttpMessageHandler 断言签名参数/body/success 分支（errcode≠0、非 JSON body 判失败）;
5. **OCP 重构**：现有 HookExecutionServiceTests 全绿即验收；
6. **Stop 纠偏**：阻断→增量 user 消息续跑同一 session；连续 3 次转警告结束；StopFailure 路径不受影响；budget_exceeded matcher 可命中；
7. **matcher 契约回归**：StopFailure 类别过滤生效（构造 rate_limit 异常断言仅匹配对应 hook）、Compact manual/auto 过滤生效；
8. **once 修复**：失败不移除、成功移除；
9. **热重载（P2）**：两次重载后 Registry 数量不变；
10. **手动验收**：`/hooks providers` 全渠道列出；声明式复刻钉钉对接真实群机器人冒烟；修改 hooks.json 不重启生效（P2）。

## 八、落地路径（供实施参考）

1. **PR-1（P0 正确性）**：matcher 传递契约修复（StopFailure 分类器 + Compact trigger 参数）；Stop 纠偏续跑（engine 层 while + TerminalReason 前置）；UserPromptSubmit 主路径接线 + GoalStageInvoke 迁移；Notification 权限挂起触发；once 修复；元数据/docs 同步；加载诊断。
2. **PR-2（B1）**：执行器 IEnumerable 注入重构，现有测试全绿即验收。
3. **PR-3（B2/B3）**：Definition + Loader + Registry + Declarative 引擎 + 内置默认文件；「声明式复刻钉钉」为首个验收（对照官方签名向量）。
4. **PR-4（P1）**：SessionEnd 退出覆盖 + 密钥展开 + resilience handler。
5. **PR-5+（P2）**：热重载 / async hook / `/hooks history`·`providers` / shell 生效 / 模板扩充，各自独立。
6. 全程同步 `docs/hooks.md`、`docs/settings.md` 与 `/hooks` 输出。
