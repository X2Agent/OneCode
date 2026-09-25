# Hook 模块（Hook Module）

> 本文档介绍 OneCode Hook 子系统的设计思路、模块总览与使用方式。架构决策与实现细节参见 [Hook 模块架构设计](./adr/0005-hook-module-design.md)。

---

## 目录

- [1. 概述](#1-概述)
- [2. 设计思路](#2-设计思路)
- [3. 模块总览](#3-模块总览)
- [4. 拦截点清单与边界](#4-拦截点清单与边界)
- [5. 执行器类型](#5-执行器类型)
- [6. 配置文件](#6-配置文件)
- [7. 使用介绍](#7-使用介绍)
- [8. 模板插值字段](#8-模板插值字段)
- [9. 优先级与作用域](#9-优先级与作用域)
- [10. 策略与安全](#10-策略与安全)
- [11. 扩展指南](#11-扩展指南)
- [12. 调试与排查](#12-调试与排查)
- [13. 相关文档](#13-相关文档)

---

## 1. 概述

OneCode 的 Hook 模块是核心扩展机制，允许用户在 Agent 执行的关键**拦截点**注入自定义逻辑，用于 CI/CD 集成、安全策略、自动化通知、审计日志等场景。

模块按"拦截点 × 执行器"两个维度组织：

| 维度 | 含义 | 取值 |
|------|------|------|
| **拦截点（HookInterceptionPoint）** | 何时触发 | 6 种拦截点（input / pre_model_call / post_model_call / pre_tool_call / post_tool_call / output） |
| **执行器（HookType）** | 如何执行 | 3 种执行器（Command / Notification / Http） |

Hook 系统通过 `~/.onecode/hooks.json` 与 `<cwd>/.onecode/hooks.json` 声明式配置，无需修改代码即可扩展。

> **边界**：Hook 是**控制平面**（策略裁决 / 阻断 / 改写），不是遥测平面。会话生命周期、Stop 纠偏、上下文压缩、
> Goal 编排、权限审批通知与异常收尾由 OneCode 宿主负责，**不属于 Hook 拦截范围**——详见 [§4 边界](#4-拦截点清单与边界)。

---

## 2. 设计思路

### 2.1 核心原则

1. **声明式配置**：通过 `hooks.json` 描述钩子，无需重新编译或修改源码
2. **执行器策略模式**：每种 `HookType` 对应一个 `IHookExecutor` 实现，新增执行器只需实现接口 + DI 注册
3. **拦截点 × Matcher 二维过滤**：拦截点决定何时触发，matcher 决定是否匹配（如 `tool_name == "Bash"`）
4. **优先级串行执行**：同一拦截点下多个 hook 按 priority 升序串行执行，结果聚合
5. **安全优先**：工作区不受信任或策略禁用时一律不触发；pre 类拦截点异常 fail-closed
6. **可观测可调试**：`/hooks` 命令实时展示注册项、策略状态与拦截点清单

### 2.2 Hook 的生命周期

```
OneCode 运行时
  │
  ├─ 启动 ──────────▶ HookConfigHotReloader.BootstrapAndStartWatching
  │                   HookConfigBootstrapper 加载 hooks.json → 注册到 HookRegistry
  │                   → 开始监视配置目录
  │
  ├─ 配置变更 ──────▶ HookConfigHotReloader（FileSystemWatcher + 500ms 防抖）
  │                   HookConfigBootstrapper.Build 快照 → HookRegistry.ReplaceAll 原子整体交换
  │                   （解析失败 / 异常时保留上一次有效配置；代次 Generation +1）
  │
  ├─ 拦截点 ────────▶ IHookExecutionService.FireAsync(payload)
  │                   │
  │                   ├─ 策略前置检查（工作区信任）
  │                   ├─ matcher 过滤（拦截点 + matcherValue 两维）
  │                   ├─ priority 升序排序
  │                   ├─ 串行执行 IHookExecutor（全部匹配项都执行）
  │                   ├─ 结果聚合（HookResultAggregator）
  │                   └─ 清理 once hook
  │
  └─ 关闭 ──────────▶ (Registry 随进程退出释放)
```

### 2.3 与 MAF 管道的集成

`pre_tool_call` / `post_tool_call` 两个拦截点通过 `HookMiddleware` 接入 MAF（Microsoft.Agents.AI）函数调用管道：

| 拦截点 | 集成点 | 行为 |
|------|--------|------|
| `pre_model_call` / `post_model_call` | `ModelCallHookDecorator`（`OneCode.Infrastructure/Ai/`，包装 `ChatClient`，位于 `FunctionInvokingChatClient` 下方） | 纯审计：流式交付零缓冲（update 逐条透传，post 审计以旁路副本收集）；无活跃 hook 时跳过 fire |
| `pre_tool_call` | `HookMiddleware`（在 `AgentPipelineBuilder` 中 `.Use()` 注册） | 阻断时返回 `ToolResult.Error` 使该次工具调用失败（不调用 `ctx.Terminate`，保留批次完整性）；异常 fail-closed |
| `post_tool_call` | 同上（next 之后） | 不消费 result，仅做通知/审计；异常 fail-soft |

其余拦截点由 `QueryStreamEngine` 在外部输入（`input`）与最终响应交付（`output`）边界触发。

---

## 3. 模块总览

```text
                         ┌─────────────────────────────────────────────┐
                         │           hooks.json (声明式配置)            │
                         │   ~/.onecode/hooks.json      (priority 100)  │
                         │   <cwd>/.onecode/hooks.json  (priority 200)  │
                         └──────────────────────┬──────────────────────┘
                                                │ 启动加载
                                                ▼
                         ┌─────────────────────────────────────────────┐
                         │         HookConfigBootstrapper               │
                         │   HookSettingsLoader 解析 → 注册到 Registry  │
                         └──────────────────────┬──────────────────────┘
                                                │
                                                ▼
                         ┌─────────────────────────────────────────────┐
                         │             HookRegistry                     │
                         │   按 (Point, Matcher) 二维索引              │
                         │   O(1) 拦截点查找 + Glob 模式匹配           │
                         └──────────────────────┬──────────────────────┘
                                                │ GetMatchesForPoint
                                                ▼
  ┌──────────────────┐                ┌─────────────────────────────────┐
  │ HookPolicyService│◀──策略检查──── │     HookExecutionService        │
  │  · 工作区信任    │                │  · 策略前置检查                 │
  │                  │                │  · matcher 过滤                 │
  │                  │                │  · priority 排序                │
  │                  │                │  · 串行执行 + 结果聚合          │
  └──────────────────┘                └──────────────┬──────────────────┘
                                                     │ 按 HookType 分发
                          ┌──────────────────────────┼──────────────────────────┐
                          │                          │                          │
                          ▼                          ▼                          ▼
              ┌──────────────────┐      ┌──────────────────────┐    ┌──────────────────────┐
              │ CommandHookExec  │      │ NotificationHookExec │    │   HttpHookExecutor   │
              │ (CliWrap 外部    │      │ (Provider 策略分发)  │    │ (IHttpClientFactory) │
              │  进程 + stdin)   │      │                      │    │                      │
              └──────────────────┘      └──────────┬───────────┘    └──────────────────────┘
                                                   │ IEnumerable<INotificationProvider>
                                       ┌───────────┴───────────┐
                                       │                       │
                                       ▼                       ▼
                            ┌──────────────────┐   ┌──────────────────────┐
                            │ FeishuProvider   │   │ WeChatWorkProvider   │
                            │ (飞书机器人)     │   │ (企业微信群机器人)   │
                            └──────────────────┘   └──────────────────────┘
```

### 3.1 核心抽象（`OneCode.Core/Hooks/`）

| 类型 | 职责 |
|------|------|
| `HookInterceptionPoint` / `HookInterceptionPoints` | 6 + 2 个拦截点枚举与协议词汇（线格式名、matcher 字段、开放范围、旧事件迁移映射） |
| `HookType` | 3 种执行器类型枚举（Command / Notification / Http） |
| `HookPayload` | 钩子数据载荷，传递给执行器的完整上下文 |
| `HookRegistration` | 钩子注册项（Name / Point / Matcher / Priority / Once / ExecutorType / TimeoutMs / Config） |
| `HookConfig` | 单个 Hook 的配置（公共字段 + 类型特有字段） |
| `HookMatcherGroup` | 匹配器分组：一个 matcher pattern 下的一组 hook 配置 |
| `HookResult` / `AggregatedHookResult` | 执行结果 / 聚合结果 |
| `HookResultAggregator` | 多个 HookResult 合并为单个 AggregatedHookResult |
| `HookPointMetadata` / `HookPointMetadataRegistry` | 拦截点元数据与 matcher 语义的权威声明（用于 UI 展示和文档生成） |
| `HookTypeParser` | 字符串 → HookType 解析（单一事实源） |
| `IHookExecutor` | 执行器接口，按 HookType 分发 |
| `IHookExecutionService` | 执行服务契约（Core 层接口，App 层实现） |
| `INotificationProvider` / `NotificationMessage` / `NotificationSendResult` | 通知渠道接口（Core 层） |

### 3.2 App 层实现（`OneCode.App/Services/Hooks/`）

| 类型 | 职责 |
|------|------|
| `HookRegistry` | 钩子注册表，按 (Point, Matcher) 二维索引；`ReplaceAll` 支持热重载原子整体替换并递增 `Generation` |
| `HookExecutionService` | 执行服务实现，策略前置 + 过滤 + 排序 + 串行执行 + 聚合 |
| `HookPolicyService` | 策略控制（工作区信任检查：当前目录是否在 `trustedDirectories` 中） |
| `GlobHookMatcher` | Glob 风格通配符匹配器 |
| `HookSettingsLoader` | 从独立 `hooks.json` 加载配置（matcher-group 格式；已迁移 / 不再支持的节点名给出诊断、JSON 错误含行号） |
| `HookConfigBootstrapper` | 启动加载器 + 快照构建器：`Bootstrap`（启动注册）与 `Build`（纯读取快照，热重载共用），并把各文件加载状态写入诊断 |
| `HookConfigHotReloader` | 配置热重载器：监视 hooks.json / notification-providers.json 变更，防抖后整体重建注册表（last-good 保护），Dispose 停止监视 |
| `HookLoadDiagnostics`（`HookFileLoadDiagnostics.cs`） | 进程级最近一次 Bootstrap 的各文件加载状态，`/hooks` 概览展示 |
| `CommandHookExecutor` | Command 类型执行器（CliWrap + stdin JSON + exit code 语义） |
| `NotificationHookExecutor` | Notification 类型执行器（经 `NotificationProviderRegistry` 渠道解析） |
| `HttpHookExecutor` | Http 类型执行器（IHttpClientFactory + 模板插值） |
| `NotificationProviderDefinitionLoader` | 声明式 Provider 定义加载器（`notification-providers.json`；https 校验、preset 展开、无效条目跳过 + 警告） |
| `NotificationProviderRegistry` | 通知渠道解析中枢：声明式定义优先、编译型兜底，同名声明式胜出（实例缓存） |
| `WebhookNotificationProviderBase` | Webhook 通知渠道基类（飞书/企微共享流程） |
| `DeclarativeNotificationProvider` | 声明式渠道唯一引擎类（消息渲染 → 签名 → 请求 → 成功判定） |
| `FeishuNotificationProvider` | 飞书机器人通知 Provider（编译型） |
| `WeChatWorkNotificationProvider` | 企业微信群机器人通知 Provider（编译型） |
| `HookTemplateRenderer` | `{{Field}}` 模板插值渲染器（支持 8 个字段，未知字段保持原样） |
| `HookSecretExpander` | 敏感字段展开器（`dpapi:` → DPAPI 解密；否则 `${ENV_VAR}` 环境变量） |
| `HostStopSessionCloseService` | 宿主停止时兜底关闭前台会话的 `IHostedService`（随 `AddHookServices` 注册） |
| `HookSerializerContext` | JSON Source Generator（高频序列化性能） |

### 3.3 集成点（`OneCode.Infrastructure/Middleware/`）

| 类型 | 职责 |
|------|------|
| `HookMiddleware` | MAF 函数调用管道中间件，触发 pre_tool_call / post_tool_call |

业务侧触发点：

| 模块 | 触发的拦截点 |
|------|-----------|
| `QueryStreamEngine` | `input`（进运行循环前，可阻断）、`output`（最终响应交付前，可阻断） |
| `ModelCallHookDecorator`（Infrastructure/Ai，包装 ChatClient） | `pre_model_call` / `post_model_call`（模型调用边界，纯审计） |
| `HookMiddleware`（Infrastructure） | `pre_tool_call` / `post_tool_call`（MAF 函数调用管道） |

> **不再由 Hook 覆盖的产品能力**（保留宿主实现，非拦截点）：会话启动/结束（`SessionManager`）、
> Stop 纠偏与预算终结（`QueryStreamEngine`）、上下文压缩（`CompactService` / `PromptTooLongRecoveryRunMiddleware`）、
> Goal 编排（`GoalDecomposer`）、权限审批通知（`ApprovalBroker`）、异常分类告警（可观测性平面）。
> 这些路径在 `hooks.json` 中写旧事件名会得到明确诊断，不会被静默忽略。

---

## 4. 拦截点清单与边界

6 种拦截点，每种有对应的 matcher 字段和可选值（权威声明见 `HookPointMetadataRegistry`）：

| 拦截点 | 触发时机 | matcher 字段 | 可选值 | 可阻断 |
|------|---------|-------------|--------|--------|
| `input` | 外部输入进入 agent run 前（不写会话历史） | 无 matcher | — | ✅ deny |
| `pre_model_call` | 一次模型请求发送前 | `model_id`（glob） | 模型标识（如 `gpt-*` / `claude-*`） | ❌（纯审计） |
| `post_model_call` | 一次模型完整响应返回后（工具调用执行前） | `model_id`（glob） | 同上 | ❌（纯审计） |
| `pre_tool_call` | 工具调用执行前 | `tool_name`（glob） | Bash / Edit / Write / Read / Grep / Glob / Task / `todos_*`（Harness 待办工具） / `mcp__*`（MCP 工具名为 `mcp__{server}__{tool}`）等 | ✅ deny |
| `post_tool_call` | 工具调用成功或失败返回后 | `tool_name`（glob） | 同上 | ❌ |
| `output` | 最终响应交付调用方前 | 无 matcher | — | ✅ deny |

> **model 节点是纯审计语义**：`pre_model_call` / `post_model_call` 的裁决结果（含 exit code 2）被丢弃，
> 不阻断、不改写模型调用——需要阻断的策略应挂在 `input` / `pre_tool_call` / `output`。

**退出码约定**（Command 类型）：

| 退出码 | 语义 | stdout / stderr 行为 |
|--------|------|---------------------|
| `0` | 成功 | stdout 为 JSON 则解析为 `HookResult`；非 JSON 作为 `Message` |
| `2` | 阻断（deny） | stderr 显示给模型或用户，阻止后续操作 |
| 其他 | 非阻断错误 | stderr 仅显示给用户，不阻止后续操作 |

**边界（刻意不做）**：

| 能力 | 归属 | 说明 |
|------|------|------|
| 会话启动 / 结束 | `SessionManager` | 产品会话边界 ≠ agent run 边界，不冒充 `agent_startup` / `agent_shutdown` |
| Stop 纠偏续跑 | `QueryStreamEngine` | `output` deny 直接终结本轮，不重入；纠偏、预算终结由宿主负责 |
| 上下文压缩 | Harness / `CompactService` | 标准协议无 compact 节点 |
| Goal 编排 | `GoalDecomposer` | 直连模型路径，不构造 agent 生命周期 |
| 权限审批通知 | `ApprovalBroker` | 审批平面 ≠ Hook 控制平面 |
| 异常分类告警 | 可观测性 | 被动遥测，不是策略裁决 |
| `pre_tool_call` 参数改写（transform） | 未建 | 首期仅裁决契约：deny 走工具错误载荷，不写回工具参数 |
| `output` 响应改写（transform） | 未建 | `output` deny 直接终结本轮，不写回最终响应内容 |
| `evaluate_only` 执行模式 | 未建 | 无「只评估不阻断」开关；阻断与否由 exit code 2 裁决 |
| 统一拦截记录摘要流 | 未建 | 审计由 hook 自身（通知 Provider / 外部命令）落地，无统一的拦截记录摘要流 |

---

## 5. 执行器类型

### 5.1 Command（`type: "command"`）

通过 CliWrap 执行外部进程，stdin 传入 JSON 格式的 `HookPayload`。

**配置字段**（`type` / `timeout` / `once` / `priority` / `statusMessage` 是三种执行器类型的公共字段，下面各节不再重复）：

| 字段 | 类型 | 说明 |
|------|------|------|
| `type` | `string?` | 执行器类型（`command` / `notification` / `http`），省略时按 `command` 处理 |
| `command` | `string` | **必填**。Shell 命令（Windows: `cmd.exe /c`；Unix: `/bin/sh -c`） |
| `timeout` | `int?` | 超时毫秒数，默认 5000 |
| `once` | `bool` | 只执行一次：**成功执行后**自动移除（Blocking 送达阻断裁决同样移除，防止同一拦截点被反复触发）；异常 / 取消 / 执行器缺失视为未完成，保留待下次触发 |
| `priority` | `int?` | 优先级（越小越先执行），默认按配置目录推导 |
| `statusMessage` | `string?` | 通知消息标题（仅 Notification 类型消费，见 §5.2）；Command / Http 类型忽略 |

**stdin Payload 示例**（`HookPayload` 的 JSON 序列化）：

```json
{
  "point": "PreToolCall",
  "sessionId": "abc123",
  "cwd": "/home/user/project",
  "toolName": "Bash",
  "toolInput": { "command": "rm -rf /" },
  "timestamp": "2026-07-18T10:00:00+00:00"
}
```

**stdout 返回 JSON 控制**（exit code 0 时）：

```json
{
  "message": "展示给用户的消息",
  "additionalContext": "注入到本轮输入的额外信息（仅 input 拦截点消费）"
}
```

**阻断**（exit code 2）：

```json
{
  "blockingError": { "error": "dangerous command", "command": "check-dangerous.py" }
}
```

**示例**：

```json
{
  "type": "command",
  "command": "python3 ~/.onecode/scripts/audit.py"
}
```

### 5.2 Notification（`type: "notification"`）

通过 `INotificationProvider` 策略分发到外部消息系统（飞书/企业微信等）。

**配置字段**：

| 字段 | 类型 | 说明 |
|------|------|------|
| `provider` | `string` | **必填**。Provider 名称：`feishu` / `wechat_work` |
| `webhookUrl` | `string` | **必填**。渠道提供的接入地址 |
| `secret` | `string?` | 签名密钥（HMAC-SHA256，可选） |
| `message` | `string` | 消息内容模板，支持 `{{field}}` 插值 |
| `statusMessage` | `string?` | 通知消息标题（作为 Provider 的 `Title`；声明式 Provider 模板可用 `{{Title}}` 引用） |
| `timeout` | `int?` | 超时毫秒数，默认 5000 |

**敏感字段保护（`${ENV_VAR}` / `dpapi:`）**：`webhookUrl` / `secret`（Notification）、`url` / `headers` 值（Http）、声明式定义的 `url` 均支持两种保密形式，运行时由 `HookSecretExpander` 展开（先展开敏感字段，再做 `{{field}}` 模板插值）：

- **`${ENV_VAR}`**：展开进程环境变量，未定义的变量展开为空串。如 `"secret": "${FEISHU_SIGN_SECRET}"`；
- **`dpapi:` 前缀**：值为 Windows DPAPI（CurrentUser）保护的 Base64 串，运行时解密。生成方式（PowerShell）：

```powershell
$bytes = [Text.Encoding]::UTF8.GetBytes('your-plain-secret')
[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect($bytes, $null, 'CurrentUser'))
# → hooks.json 中写 "secret": "dpapi:<上一步输出的 Base64>"
```

解密失败按非阻断错误处理（不会泄露明文）。建议含密钥的配置文件加入 `.gitignore`，不入库。

**示例**：

```json
{
  "type": "notification",
  "provider": "feishu",
  "webhookUrl": "https://open.feishu.cn/open-apis/bot/v2/hook/xxx",
  "secret": "your-sign-secret",
  "message": "[OneCode] 拦截点 {{Point}} 触发于 {{Timestamp}}"
}
```

**支持的 Provider**：

| Provider 名称 | 渠道 | 消息格式 | 签名算法 |
|--------------|------|---------|---------|
| `feishu` | 飞书机器人 | `{"msg_type":"text","content":{"text":"..."}}` | HMAC-SHA256(key = timestamp + "\n" + secret, msg = "") → Base64 |
| `wechat_work` | 企业微信群机器人 | `{"msgtype":"text","text":{"content":"..."}}` | HMAC-SHA256(key = secret, msg = timestamp + "\n" + secret) → Base64 |
| `dingtalk` | 钉钉机器人（声明式内置默认，见 §5.4） | `{"msgtype":"text","text":{"content":"..."}}` | HMAC-SHA256(key = timestamp(ms) + "\n" + secret, msg = "") → Base64 → URL 编码 |

### 5.3 Http（`type: "http"`）

通用 HTTP 调用，用于 webhook 通知、CI/CD 触发、自定义服务集成、审计回调等场景。

**配置字段**：

| 字段 | 类型 | 说明 |
|------|------|------|
| `url` | `string` | **必填**。请求目标 URL，支持 `{{field}}` 插值；亦支持敏感字段展开（`${ENV_VAR}` / `dpapi:`，见 §5.2） |
| `method` | `string?` | HTTP 方法（GET / POST / PUT / DELETE / PATCH），默认 POST |
| `headers` | `Dictionary<string, string>?` | 自定义请求头，值支持 `{{field}}` 插值与敏感字段展开（如 `"Authorization": "Bearer ${CI_TOKEN}"`） |
| `body` | `string?` | 请求体模板（POST/PUT/PATCH/DELETE 使用），支持 `{{field}}` 插值 |
| `timeout` | `int?` | 超时毫秒数，默认 5000 |

> **HTTP 韧性（刻意不重试）**：Notification 与 Http 的出站调用**不做自动重试、无熔断**——`timeout`（默认 5s）是单次调用的唯一超时权威，失败一律降级为 NonBlockingError（不阻断主流程）；`once` Hook 失败后保留注册，下次事件自然补发。理由：出站 POST 目标（自定义 webhook/CI 触发）幂等性无法保证，重试决策交给配置方业务层。

**示例**：

```json
{
  "type": "http",
  "method": "POST",
  "url": "https://ci.example.com/api/trigger",
  "headers": {
    "Authorization": "Bearer {{Token}}",
    "X-Point": "{{Point}}"
  },
  "body": "{\"point\":\"{{Point}}\",\"tool\":\"{{ToolName}}\",\"cwd\":\"{{Cwd}}\",\"timestamp\":\"{{Timestamp}}\"}"
}
```

**与 Notification 的区别**：

| 维度 | Http | Notification |
|------|------|--------------|
| 目标 | 通用 HTTP 调用（自定义 URL/Method/Headers/Body） | 消息推送业务场景（飞书/企微等固定渠道格式） |
| 灵活性 | 高（完全自定义） | 低（渠道特定格式） |
| 签名 | 自行通过 headers 实现 | Provider 内置 HMAC 签名 |
| 响应解析 | 仅判断 HTTP 状态码 | 解析渠道特定响应字段（code/errcode 等） |

### 5.4 声明式 Provider（`notification-providers.json`）

对于「固定端点 + 特定 payload + 签名变体」类渠道（钉钉/自建网关等），无需写 C#——用 JSON 定义描述渠道，由唯一引擎类 `DeclarativeNotificationProvider` 在运行时执行。

**文件位置与三层覆盖**（同名整条替换，高层胜出，Info 日志）：

```
<AppContext.BaseDirectory>/notification-providers.json   ← 内置默认（随应用打包，当前含 dingtalk）
~/.onecode/notification-providers.json                   ← 用户级
<cwd>/.onecode/notification-providers.json               ← 项目级（优先级最高）
```

**Schema**（以钉钉为例，内置默认即此定义）：

```jsonc
{
  "dingtalk": {
    "displayName": "钉钉机器人",
    "method": "POST",
    "url": "https://oapi.dingtalk.com/robot/send",
    "body": { "msgtype": "text", "text": { "content": "{{Text}}" } },
    "signing": { "preset": "dingtalk" },
    "success": { "field": "errcode", "equals": 0 }
  }
}
```

| 字段 | 说明 |
|------|------|
| `displayName` | 渠道显示名（诊断展示用） |
| `method` | HTTP 方法，默认 POST |
| `url` | **必填**。默认端点，必须 `https://`；发送时 hooks.json 的 `webhookUrl` 优先 |
| `headers` | 附加请求头；值支持消息模板 |
| `body` | **必填**。JSON 请求体模板；字符串叶子支持消息模板，其他类型保持原样 |
| `signing` | 签名定义，或 `{"preset": "..."}` 简写（见下）；preset 存在时其余 `signing` 字段被忽略 |
| `success` | 响应成功判定：顶层字段 `field` 与 `equals` 相等即成功；缺省仅看 HTTP 2xx |

**签名定义**（`signing` 完整写法）：

| 字段 | 说明 |
|------|------|
| `keyTemplate` / `messageTemplate` | HMAC key / message 模板；变量仅 `{Secret}` / `{Timestamp}` / `{TimestampMs}`，禁止表达式求值 |
| `encoding` | `base64`（默认）/ `base64-urlencoded`（钉钉）/ `hex` |
| `placement` | `query`（默认）/ `header`（header 放置时签名与时间戳自动附加为请求头） |
| `paramName` | 签名参数名，默认 `sign` |
| `timestampParamName` | 时间戳参数名，默认 `timestamp` |
| `timestampMs` | `true` 时 URL / Header 中的时间戳参数取毫秒（钉钉要求毫秒）；签名模板变量 `{Timestamp}` 恒为 Unix 秒，毫秒用 `{TimestampMs}` |

**签名 preset**（官方算法固化为三选一）：

| preset | 渠道 | 算法 |
|--------|------|------|
| `feishu` | 飞书 | HMAC(key = `timestamp秒\nsecret`, msg = "") → Base64 |
| `wechat_work` | 企业微信 | HMAC(key = `secret`, msg = `timestamp秒\nsecret`) → Base64 |
| `dingtalk` | 钉钉 | HMAC(key = `timestamp毫秒\nsecret`, msg = "") → Base64 → URL 编码 |

**消息模板字段**（body 字符串叶子与 headers 值）：`{{Text}}` / `{{Title}}` / `{{Event}}`（拦截点线格式名，如 `pre_tool_call`）/ `{{Timestamp}}`（`yyyy-MM-dd HH:mm:ss`）；单花括号写法同样接受，未知占位符保持原样。第一级 payload 渲染（`config.message` 的 `{{Point}}` 等 `HookPayload` 字段）仍由 Notification 执行器完成。

**校验规则**：url 非 https / 缺 body / 未知 preset 的定义跳过并记录 Warning，不影响同文件其他条目；JSON 错误含行号。

**渠道解析顺序**：声明式定义优先，编译型 `INotificationProvider` 兜底，同名声明式胜出（`NotificationProviderRegistry`）。OAuth、多步交互等复杂集成仍走编译型（见 §11.2）。

---

## 6. 配置文件

### 6.1 文件位置与优先级

| 文件 | 作用域 | 基础优先级 |
|------|--------|-----------|
| `~/.onecode/hooks.json` | 用户级（全局） | 100 |
| `<cwd>/.onecode/hooks.json` | 项目级 | 200 |

两个文件都会被加载，项目级 hook 与用户级 hook 共存，按 priority 升序串行执行。

### 6.2 文件格式

`hooks.json` 的根对象即为 hooks 内容（按拦截点名分组），支持两种格式：

#### 新格式（推荐）：matcher-group

每个拦截点下是 matcher 分组数组，每个分组包含 `matcher` 和 `hooks`：

```json
{
  "pre_tool_call": [
    {
      "matcher": "Bash",
      "hooks": [
        {
          "type": "command",
          "command": "echo 'About to run Bash'",
          "timeout": 3000
        }
      ]
    },
    {
      "matcher": "Write|Edit",
      "hooks": [
        {
          "type": "notification",
          "provider": "feishu",
          "webhookUrl": "https://open.feishu.cn/open-apis/bot/v2/hook/xxx",
          "message": "即将写入文件: {{Cwd}}"
        }
      ]
    }
  ],
  "post_tool_call": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "http",
          "method": "POST",
          "url": "https://audit.example.com/api/tool",
          "body": "{\"tool\":\"{{ToolName}}\",\"point\":\"{{Point}}\"}"
        }
      ]
    }
  ],
  "pre_model_call": [
    {
      "matcher": "gpt-*",
      "hooks": [
        {
          "type": "command",
          "command": "echo 'model request'"
        }
      ]
    }
  ],
  "output": [
    {
      "hooks": [
        {
          "type": "notification",
          "provider": "wechat_work",
          "webhookUrl": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx",
          "message": "AI 响应完成于 {{Timestamp}}"
        }
      ]
    }
  ]
}
```

#### 旧格式（平铺）——已不再支持

`HookSettingsLoader` 只解析 matcher-group 格式。拦截点下直接平铺 hook 配置数组（无 `matcher`/`hooks` 包装）的旧格式会被跳过（反序列化后 `Hooks` 为空），不会自动包装为 `matcher=""`。

#### 已废弃事件名——加载期诊断

旧版 OneCode 专有事件名不再有效，加载时给出**明确诊断**（不静默跳过）：

| 旧事件名 | 诊断 |
|---------|------|
| `PreToolUse` / `PostToolUse` / `UserPromptSubmit` | 提示迁移到 `pre_tool_call` / `post_tool_call` / `input` |
| `SessionStart` / `SessionEnd` / `Stop` / `StopFailure` / `PreCompact` / `PostCompact` / `GoalStageInvoke` / `Notification` | 提示"不再属于 Hook 拦截范围"，并说明对应能力仍由宿主负责 |

### 6.3 Matcher 语法

`matcher` 字段使用 Glob 风格通配符：

| Pattern | 语义 | 示例 |
|---------|------|------|
| `""` 或 `"*"` | 匹配所有（wildcard） | 任何 `tool_name` 都触发 |
| `"Bash"` | 精确匹配（大小写不敏感） | 仅 `tool_name == "Bash"` 触发 |
| `"Bash*"` | 前缀通配 | `Bash` / `BashRun` 都触发 |
| `"*Tool"` | 后缀通配 | `MyTool` / `Tool` 都触发 |
| `"Write\|Read"` | 管道分隔多值 | `Write` 或 `Read` 触发 |
| `"  Write  \|  Read  "` | 自动 trim 空白 | 等价于 `Write\|Read` |

> **注意**：matcher 字段名因拦截点而异（工具节点为 `tool_name`，模型节点为 `model_id`）。详见 [§4 拦截点清单与边界](#4-拦截点清单与边界)。无 matcher 的拦截点（`input` / `output`）使用 `""` 或 `"*"`。

### 6.4 策略控制

当前没有 `hooks.*` 策略开关配置项。唯一的策略控制是**工作区信任**：Hook 仅在当前工作目录位于 `settings.json` 的 `trustedDirectories` 列表（含子目录）中时才会触发（见 [§10.1](#101-工作区信任)）。

### 6.5 热重载

`hooks.json` 与 `notification-providers.json` 支持运行时热重载，修改后无需重启 OneCode（PR-5 C3）。

**机制**（`HookConfigHotReloader`）：

1. `AppStartupService` 预热时经 `BootstrapAndStartWatching` 完成初始 Bootstrap 并开始监视用户（`~/.onecode`）与项目（`.onecode`）配置目录顶层（目录不存在则跳过该层，无热重载）
2. 文件变更（Changed / Created / Deleted / Renamed）经 **500ms 防抖**合并后触发一次重建
3. 重建走 `HookConfigBootstrapper.Build` 产出不可变快照，再经 `HookRegistry.ReplaceAll` **原子整体交换**（不做增量 Bootstrap）
4. 编程注册的 hook（名称不带 `config:` 前缀，如插件 API 注册项）在整体交换中原样保留
5. 成功后同步刷新 `HookLoadDiagnostics`（`/hooks` 概览实时反映最新状态）与 `NotificationProviderRegistry` 声明式定义

**失败语义（last-good 保护）**：

| 场景 | 行为 |
|------|------|
| 任一层 hooks.json 解析失败（JSON 损坏 / IO 错误 / 根节点非对象） | 保留上一次有效配置，诊断记录 `Failed`，LogWarning |
| 构建过程抛出意外异常 | 保留上一次有效配置，LogError |
| hooks.json 被删除 | 正常路径：清空该层 config hook（NotFound 参与整体交换） |
| 已废弃事件名 / 内部生命周期拦截点名 | 非致命：跳过该条目并写入加载诊断（已废弃事件名给出迁移提示，内部生命周期拦截点提示不在开放范围） |
| 未知 `type`（HookType） | 非致命：跳过该条目并记 Warning 日志（不写入加载诊断），其余条目照常生效 |

> **注意**：`config:` 前缀注册名是热重载区分配置 hook 与编程注册 hook 的依据，自定义 hook 的注册名请勿使用该前缀。

---

## 7. 使用介绍

### 7.1 `/hooks` 命令

| 子命令 | 语法 | 说明 |
|--------|------|------|
| 无参数 | `/hooks` | 概览：持久 hook 数 + 配置代次 + 工作区信任状态 + 配置文件路径 + 各 hooks.json 最近一次加载诊断（状态 / hook 数 / 解析错误） |
| `list` / `ls` | `/hooks list` | 完整 hook 列表（按 source 分组：Managed / User / Project / Plugin） |
| `events` | `/hooks events` | 可用拦截点列表（含 matcher 字段） |
| `status` | `/hooks status` | Hook 策略状态（工作区信任） |

`/hooks` 概览示例：

```text
Hooks: agent interception points

  Persistent hooks: 3
  Config generation: 1

  Workspace trusted: yes

Config files:
  ~/.onecode/hooks.json         (user-level, priority 100)
  .onecode/hooks.json           (project-level, priority 200)

Load diagnostics (last bootstrap):
  ~/.onecode: Loaded (2 hooks)
  <cwd>/.onecode: NotFound (0 hooks)

Subcommands: /hooks list | /hooks events | /hooks status
```

`/hooks list` 示例：

```text
User hooks:
  pre_tool_call:
   [100] Command    config:pre_tool_call:Command:abc123
             command: echo 'About to run Bash'
  post_tool_call:
   [100] Http       config:post_tool_call:Http:def456

Project hooks:
  output:
   [200] Notification config:output:Notification:ghi789
```

### 7.2 典型场景示例

#### 场景 1：危险命令阻断（pre_tool_call + Command）

阻止 `rm -rf /` 等危险命令执行：

```json
{
  "pre_tool_call": [
    {
      "matcher": "Bash",
      "hooks": [
        {
          "type": "command",
          "command": "python3 ~/.onecode/scripts/block-dangerous.py",
          "timeout": 2000
        }
      ]
    }
  ]
}
```

`block-dangerous.py` 读取 stdin JSON，检测到危险命令时 exit 2：

```python
import json, sys
payload = json.load(sys.stdin)
cmd = payload.get("toolInput", {}).get("command", "")
if "rm -rf /" in cmd:
    sys.stderr.write("Blocked: dangerous command detected")
    sys.exit(2)
sys.exit(0)
```

#### 场景 2：工具执行后飞书通知（post_tool_call + Notification）

```json
{
  "post_tool_call": [
    {
      "matcher": "Bash",
      "hooks": [
        {
          "type": "notification",
          "provider": "feishu",
          "webhookUrl": "https://open.feishu.cn/open-apis/bot/v2/hook/xxx",
          "secret": "your-sign-secret",
          "message": "[OneCode] 工具 {{ToolName}} 执行完成 @ {{Timestamp}}"
        }
      ]
    }
  ]
}
```

#### 场景 3：审计回调（post_model_call + Http）

```json
{
  "post_model_call": [
    {
      "matcher": "gpt-*",
      "hooks": [
        {
          "type": "http",
          "method": "POST",
          "url": "https://ci.example.com/api/onecode/model-response",
          "headers": {
            "Authorization": "Bearer ci-token-xxx"
          },
          "body": "{\"session\":\"{{SessionId}}\",\"cwd\":\"{{Cwd}}\",\"ts\":\"{{Timestamp}}\"}"
        }
      ]
    }
  ]
}
```

#### 场景 4：模型调用审计（pre_model_call + Command）

```json
{
  "pre_model_call": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "command",
          "command": "echo '[audit] model request' >> ~/.onecode/audit.log",
          "timeout": 2000
        }
      ]
    }
  ]
}
```

#### 场景 5：最终响应出口管控（output + Notification）

```json
{
  "output": [
    {
      "hooks": [
        {
          "type": "notification",
          "provider": "wechat_work",
          "webhookUrl": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx",
          "message": "✅ OneCode 响应完成 @ {{Timestamp}}"
        }
      ]
    }
  ]
}
```

> 压缩审计、会话启动通知、错误停止告警等能力已移出 Hook 拦截范围（见 [§4 边界](#4-拦截点清单与边界)），
> 请改用可观测性通道或宿主产品能力。

### 7.3 编程式触发 Hook

业务模块可通过注入 `IHookExecutionService` 直接触发拦截点：

```csharp
public class MyService
{
    private readonly IHookExecutionService? _hooks;

    public MyService(IHookExecutionService? hooks) => _hooks = hooks;

    public async Task DoWorkAsync(CancellationToken ct)
    {
        if (_hooks is null) return;

        var payload = new HookPayload
        {
            Point = HookInterceptionPoint.Input,
            Cwd = Environment.CurrentDirectory,
            UserMessage = "some user prompt",
        };

        // actualMatcherValue 为对应拦截点的 matcher 字段值
        // （如 pre_tool_call 传 tool_name，pre_model_call 传 model_id）
        await _hooks.FireAsync(payload, actualMatcherValue: null, ct: ct);
    }
}
```

### 7.4 编程式注册 Hook

运行时动态注册 hook（如插件系统）：

```csharp
public class MyPlugin
{
    private readonly HookRegistry _registry;

    public MyPlugin(HookRegistry registry) => _registry = registry;

    public void RegisterHook()
    {
        _registry.Register(new HookRegistration
        {
            Name = "my-plugin:audit",
            Point = HookInterceptionPoint.PostToolCall,
            Matcher = "Bash",
            Priority = 150,  // User 级
            ExecutorType = HookType.Command,
            TimeoutMs = 3000,
            Config = new HookConfig
            {
                Type = "command",
                Command = "echo 'Bash executed'",
            },
        });
    }
}
```

---

## 8. 模板插值字段

`Notification` 和 `Http` 类型执行器支持 `{{Field}}` 模板插值；可插值字段限于下表 8 个（`HookPayload` 的其余字段不参与插值）：

| 字段 | 类型 | 说明 |
|------|------|------|
| `{{Point}}` | `string` | 拦截点线格式名（如 `pre_tool_call`） |
| `{{SessionId}}` | `string` | 会话 ID |
| `{{Cwd}}` | `string` | 当前工作目录 |
| `{{ToolName}}` | `string` | 工具名称（仅工具相关拦截点） |
| `{{UserMessage}}` | `string` | 用户消息（input 拦截点） |
| `{{AgentId}}` | `string` | Agent ID |
| `{{AgentType}}` | `string` | Agent 类型 |
| `{{Timestamp}}` | `string` | 触发时间戳（格式 `yyyy-MM-dd HH:mm:ss`） |

**插值规则**：
- 未知字段保持原样（如 `{{Unknown}}` 不被替换）
- 字段值为 null 时替换为空字符串
- 大小写敏感（必须与上表完全一致）

**示例**：

```json
{
  "url": "https://api.example.com/{{Point}}",
  "headers": { "X-Tool": "{{ToolName}}" },
  "body": "{\"cwd\":\"{{Cwd}}\",\"ts\":\"{{Timestamp}}\"}"
}
```

---

## 9. 优先级与作用域

### 9.1 优先级范围约定

| 优先级范围 | 来源 | 说明 |
|-----------|------|------|
| `0-99` | Managed（系统内置） | 系统级 hook |
| `100-199` | User（用户级） | `~/.onecode/hooks.json` 加载，默认 priority 100 |
| `200-299` | Project（项目级） | `<cwd>/.onecode/hooks.json` 加载，默认 priority 200 |
| `300+` | Plugin（插件） | 插件运行时注册 |

### 9.2 执行顺序

同一拦截点下多个匹配的 hook 按 `priority` **升序**串行执行（数值越小越先执行）。执行结果通过 `HookResultAggregator` 聚合：

| 字段 | 聚合策略 |
|---------|---------|
| `BlockingErrors` / `AdditionalContexts`（列表） | 累加 |
| `Message`（字符串） | last-write-wins（单个 hook 的 `Message` 优先于其 `SystemMessage`，跨 hook 取最后一个非空值） |

### 9.3 Once Hook

`once: true` 的 hook 执行一次后自动从 Registry 移除，适合"仅首次触发"的场景（如初始化提示）。

---

## 10. 策略与安全

### 10.1 工作区信任

Hook 仅在**受信任工作区**中触发。`HookPolicyService.IsCurrentWorkspaceTrusted()` 检查当前工作目录是否在 `settings.json` 的 `TrustedDirectories` 列表中（支持子目录继承）。

未受信任的工作区中所有 hook 都不会触发，避免恶意仓库通过 `hooks.json` 执行任意命令。

### 10.2 策略开关

当前没有 `disableAll` / `allowManagedOnly` / `strictPluginOnly` 等策略开关。工作区信任（§10.1）是唯一的策略门控。

### 10.3 异常处理策略

| 场景 | 策略 | 说明 |
|------|------|------|
| Pre-hook（pre_tool_call）异常 | **fail-closed** | 异常转为 `ToolResult.Error` 使该次工具调用失败（不调用 `ctx.Terminate`，保留批次完整性） |
| Post-hook（post_tool_call）异常 | **fail-soft** | 仅记日志，保留原工具结果返回 |
| 单个 hook 执行器异常 | 隔离 | 异常被吞掉记 Warning，其他 hook 继续执行 |
| `OperationCanceledException` | 透传 | 保留取消信号，不吞掉 |

### 10.4 超时保护

每个 hook 独立超时（默认 5000ms，通过 `timeout` 字段配置）。超时后：
- Command 类型：返回 `NonBlockingError` 结果
- Http 类型：返回 `NonBlockingError` 结果
- Notification 类型：返回 `NonBlockingError` 结果

超时不影响其他 hook 的执行。

---

## 11. 扩展指南

> **DI 注册类真相源**：`src/OneCode.App/Services/Hooks/HookServiceCollectionExtensions.cs` 的 `AddHookServices()`。
> 按下述步骤增改代码即可，无需修改其他文件（组合根 `OneCodeApp.Create` 已调用 `AddHookServices`）。

### 11.1 新增执行器类型

如需支持新的执行器类型（如 `Webhook` / `Redis`）：

1. **扩展 `HookType` 枚举**（`OneCode.Core/Hooks/HookTypes.cs`）：

```csharp
public enum HookType
{
    Command,
    Notification,
    Http,
    Webhook,  // 新增
}
```

2. **更新 `HookTypeParser`**（`OneCode.Core/Hooks/HookTypeParser.cs`）：

```csharp
public static HookType Parse(string? type) => type?.ToLowerInvariant() switch
{
    "command" => HookType.Command,
    "notification" => HookType.Notification,
    "http" => HookType.Http,
    "webhook" => HookType.Webhook,  // 新增
    null => HookType.Command,
    _ => throw new ArgumentException($"Unknown hook type: '{type}'. Valid values: command, notification, http, webhook."),
};
```

3. **实现 `IHookExecutor`**（新文件放 `OneCode.App/Services/Hooks/`）——参照已有的 `CommandHookExecutor`：

```csharp
public sealed class WebhookHookExecutor : IHookExecutor
{
    public HookType Type => HookType.Webhook;

    public async Task<HookResult?> ExecuteAsync(
        HookPayload payload, HookConfig config, CancellationToken ct)
    {
        // 实现调用逻辑；null 表示无结果
        return null;
    }
}
```

4. **DI 注册**（`src/OneCode.App/Services/Hooks/HookServiceCollectionExtensions.cs` 的 `AddHookServices`）：

```csharp
services.AddSingleton<IHookExecutor, WebhookHookExecutor>();
```

> **分发机制**：`HookExecutionService` 经 `IEnumerable<IHookExecutor>` 注入，按每个执行器的 `Type` 属性分发——
> **不是** Keyed Services。新增执行器只需追加一行 `AddSingleton<IHookExecutor, X>()`。

### 11.2 新增通知渠道

如需支持新的通知渠道，**优先用声明式定义**（零代码，见 §5.4）——固定端点 + 特定 payload + 签名变体类渠道（钉钉/自建网关等）只需在 `~/.onecode/notification-providers.json` 写一条定义即可，钉钉已随应用内置默认。仅 OAuth、多步交互等复杂集成才需要写 C#：

1. **实现 `INotificationProvider`**（推荐继承 `WebhookNotificationProviderBase`）：

```csharp
public sealed class SlackNotificationProvider(HttpClient httpClient, ILogger<SlackNotificationProvider>? logger = null)
    : WebhookNotificationProviderBase(httpClient, logger)
{
    public override string Name => "slack";
    // ... 渠道特定 payload / 签名 / 响应解析
}
```

2. **DI 注册**（`src/OneCode.App/Services/Hooks/HookServiceCollectionExtensions.cs` 的 `AddHookServices`）：

```csharp
services.AddSingleton<INotificationProvider, SlackNotificationProvider>();
services.AddHttpClient<SlackNotificationProvider>();
```

3. **使用**（`hooks.json`）：

```json
{
  "type": "notification",
  "provider": "slack",
  "webhookUrl": "https://hooks.slack.com/services/xxx",
  "message": "拦截点 {{Point}} 触发 @ {{Timestamp}}"
}
```

> 编译型与声明式并存时，`NotificationProviderRegistry` 按名称解析：声明式定义优先，同名声明式胜出。

### 11.3 新增拦截点

拦截点集合与 AGENT-HOOKS-0.1 协议对齐，**不再自由扩展**。如需新增拦截点（如 `OnTokenBudgetExceeded` / `PreFileWrite`）：

1. **扩展 `HookInterceptionPoint` 枚举**（`OneCode.Core/Hooks/HookInterceptionPoint.cs`）并同步 `HookInterceptionPoints` 的线格式名、matcher 字段与开放集合
2. **更新 `HookPointMetadataRegistry`**（`OneCode.Core/Hooks/HookPointMetadata.cs`）添加拦截点元数据
3. **在业务模块触发**：注入 `IHookExecutionService` 并调用 `FireAsync`

> 新增拦截点前先确认它确实是 **agent execution seam**（输入 / 模型 / 工具 / 输出四类边界之一）。
> 产品会话生命周期、压缩、Goal 编排、审批通知、异常告警不属于协议拦截范围，不应伪装成新拦截点。

---

## 12. 调试与排查

### 12.1 Hook 未触发

排查步骤：

1. **运行 `/hooks status`** 检查策略状态：
   - `Workspace trusted: NO` → 工作区未受信任，需在 `settings.json` 的 `TrustedDirectories` 中添加当前目录

2. **运行 `/hooks list`** 确认 hook 已注册：
   - 未出现 → 检查 `hooks.json` 路径与 JSON 语法（注意：只支持 matcher-group 格式，见 §6.2）

3. **检查 matcher**：
   - 运行 `/hooks events` 确认拦截点的 matcher 字段名
   - 确认 `actualMatcherValue` 与 matcher pattern 匹配（如 `Bash` vs `bash` 大小写不敏感）

4. **确认拦截点已在产品路径接线**：`input` / `output` 只在 `QueryStreamEngine` 主链路生效；
   `pre_tool_call` / `post_tool_call` 只对走 `AgentPipelineBuilder` 的工具型 Agent 生效
   （Main / Worker / Team / Goal 子目标）。`GoalDecomposer` 等直连模型路径不构造 agent 生命周期，
   **不触发**任何拦截点。

### 12.2 Hook 执行失败

- **Command 类型**：手动执行 `command` 字段，确认退出码与输出
- **Http 类型**：检查 URL 可达性、Headers 格式、Body JSON 合法性
- **Notification 类型**：检查 `webhookUrl` 有效、`secret` 正确、Provider 名称匹配

### 12.3 Hook 阻断未生效

- 仅 `input` / `pre_tool_call` / `output` 支持阻断（exit code 2）；
  `pre_model_call` / `post_model_call` / `post_tool_call` 是审计语义，deny（exit code 2）不会改变主流程
- 确认 exit code 为 `2`（其他非零退出码视为非阻断错误）
- 查看 stderr 输出（阻断时显示给模型或用户）

### 12.4 日志查看

Hook 执行相关日志通过 `ILogger` 输出，关键日志类别：

| 日志类别 | 关键消息 |
|---------|---------|
| `HookExecutionService` | `Hook execution skipped: workspace not trusted`（Debug 级） |
| `HookExecutionService` | `Hook '{Name}' execution error` |
| `CommandHookExecutor` | `Command hook execution failed`（超时以结果消息 `Command hook timed out after {N}ms` 呈现，不写日志） |
| `HttpHookExecutor` | `HTTP hook timed out after {N}ms` / `HTTP hook execution failed` |
| `NotificationHookExecutor` | `Notification provider '{Provider}' not registered` |
| `HookConfigBootstrapper` | `Bootstrapped {Count} hooks total` |

---

## 13. 相关文档

- [Hook 模块架构设计](./adr/0005-hook-module-design.md) — 架构决策、数据模型、实现细节
- [设置文档 - Hooks 配置](./settings.md#hooks-配置) — `hooks.json` 与策略开关
- [命令文档 - /hooks](./commands.md#hooks) — `/hooks` 命令完整说明
- [Permission vs ToolApproval vs Filter](./adr/0001-permission-vs-toolapproval-vs-filter.md) — MAF 函数调用管道中 Permission / ToolApproval / 观测性中间件的职责边界与数据流
