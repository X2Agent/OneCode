# Hook 模块（Hook Module）

> 本文档介绍 OneCode Hook 子系统的设计思路、模块总览与使用方式。架构决策与实现细节参见 [ADR 0005: Hook 模块架构设计](./adr/0005-hook-module-design.md)。

---

## 目录

- [1. 概述](#1-概述)
- [2. 设计思路](#2-设计思路)
- [3. 模块总览](#3-模块总览)
- [4. 事件清单](#4-事件清单)
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

OneCode 的 Hook 模块是核心扩展机制，允许用户在 Agent 生命周期的关键节点注入自定义逻辑，用于 CI/CD 集成、安全策略、自动化通知、审计日志等场景。

模块按"事件 × 执行器"两个维度组织：

| 维度 | 含义 | 取值 |
|------|------|------|
| **事件（HookEvent）** | 何时触发 | 10 种生命周期事件（PreToolUse / PostToolUse / Notification / UserPromptSubmit / SessionStart / Stop / StopFailure / PreCompact / PostCompact / SessionEnd） |
| **执行器（HookType）** | 如何执行 | 3 种执行器（Command / Notification / Http） |

Hook 系统通过 `~/.onecode/hooks.json` 与 `<cwd>/.onecode/hooks.json` 声明式配置，无需修改代码即可扩展。

---

## 2. 设计思路

### 2.1 核心原则

1. **声明式配置**：通过 `hooks.json` 描述钩子，无需重新编译或修改源码
2. **执行器策略模式**：每种 `HookType` 对应一个 `IHookExecutor` 实现，新增执行器只需实现接口 + DI 注册
3. **事件 × Matcher 二维过滤**：事件决定何时触发，matcher 决定是否匹配（如 `tool_name == "Bash"`）
4. **优先级串行执行**：同一事件下多个 hook 按 priority 升序串行执行，结果聚合
5. **安全优先**：工作区不受信任或策略禁用时一律不触发；Pre-hook 异常 fail-closed
6. **可观测可调试**：`/hooks` 命令实时展示注册项、策略状态与事件清单

### 2.2 Hook 的生命周期

```
OneCode 运行时
  │
  ├─ 启动 ──────────▶ HookConfigBootstrapper
  │                   加载 hooks.json → 注册到 HookRegistry
  │
  ├─ 生命周期事件 ──▶ IHookExecutionService.FireAsync(payload)
  │                   │
  │                   ├─ 策略前置检查（全局禁用 / 工作区信任）
  │                   ├─ matcher 过滤（event + matcherValue 两维）
  │                   ├─ 策略过滤（allowManagedOnly）
  │                   ├─ priority 升序排序
  │                   ├─ 串行执行 IHookExecutor
  │                   ├─ 结果聚合（HookResultAggregator）
  │                   └─ 清理 once hook
  │
  └─ 关闭 ──────────▶ (Registry 随进程退出释放)
```

### 2.3 与 MAF 管道的集成

`PreToolUse` / `PostToolUse` 两个事件通过 `HookMiddleware` 接入 MAF（Microsoft.Agents.AI）函数调用管道：

| 事件 | 集成点 | 行为 |
|------|--------|------|
| `PreToolUse` | `HookMiddleware`（在 `AgentPipelineBuilder` 中 `.Use()` 注册） | 阻断时返回 `ToolResult.Error` + `ctx.Terminate`；异常 fail-closed |
| `PostToolUse` | 同上（next 之后） | 不消费 result，仅做通知/审计；异常 fail-soft |

其他事件（`SessionStart` / `Stop` / `PreCompact` 等）由对应业务模块直接调用 `IHookExecutionService.FireAsync` 触发。

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
                         │   按 (Event, Matcher) 二维索引               │
                         │   O(1) 事件查找 + Glob 模式匹配              │
                         └──────────────────────┬──────────────────────┘
                                                │ GetMatchesForEvent
                                                ▼
  ┌──────────────────┐                ┌─────────────────────────────────┐
  │ HookPolicyService│◀──策略检查──── │     HookExecutionService        │
  │  · 工作区信任    │                │  · 策略前置检查                 │
  │  · disableAll    │                │  · matcher 过滤 + 策略过滤      │
  │  · allowManaged  │                │  · priority 排序                │
  │  · strictPlugin  │                │  · 串行执行 + 结果聚合          │
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
| `HookEvent` | 10 种生命周期事件枚举 |
| `HookType` | 3 种执行器类型枚举（Command / Notification / Http） |
| `HookPayload` | 钩子数据载荷，传递给执行器的完整上下文 |
| `HookRegistration` | 钩子注册项（Name / Event / Matcher / Priority / Once / ExecutorType / TimeoutMs / Config） |
| `HookConfig` | 单个 Hook 的配置（公共字段 + 类型特有字段） |
| `HookMatcherGroup` | 匹配器分组：一个 matcher pattern 下的一组 hook 配置 |
| `HookResult` / `AggregatedHookResult` | 执行结果 / 聚合结果 |
| `HookResultAggregator` | 多个 HookResult 合并为单个 AggregatedHookResult |
| `HookEventMetadata` / `HookEventMetadataRegistry` | 事件元数据（用于 UI 展示和文档生成） |
| `HookTypeParser` | 字符串 → HookType 解析（单一事实源） |
| `IHookExecutor` | 执行器接口，按 HookType 分发 |
| `IHookExecutionService` | 执行服务契约（Core 层接口，App 层实现） |
| `INotificationProvider` / `NotificationMessage` / `NotificationSendResult` | 通知渠道接口（Core 层） |

### 3.2 App 层实现（`OneCode.App/Services/Hooks/`）

| 类型 | 职责 |
|------|------|
| `HookRegistry` | 钩子注册表，按 (Event, Matcher) 二维索引 |
| `HookExecutionService` | 执行服务实现，策略前置 + 过滤 + 排序 + 串行执行 + 聚合 |
| `HookPolicyService` | 策略控制（工作区信任、disableAll、allowManagedOnly、strictPluginOnly） |
| `GlobHookMatcher` | Glob 风格通配符匹配器 |
| `HookSettingsLoader` | 从 `hooks.json` 加载配置（支持新旧两种格式） |
| `HookConfigBootstrapper` | 启动加载器，从用户/项目配置目录注册 |
| `CommandHookExecutor` | Command 类型执行器（CliWrap + stdin JSON + exit code 语义） |
| `NotificationHookExecutor` | Notification 类型执行器（Provider 策略分发） |
| `HttpHookExecutor` | Http 类型执行器（IHttpClientFactory + 模板插值） |
| `WebhookNotificationProviderBase` | Webhook 通知渠道基类（飞书/企微/钉钉共享流程） |
| `FeishuNotificationProvider` | 飞书机器人通知 Provider |
| `WeChatWorkNotificationProvider` | 企业微信群机器人通知 Provider |
| `HookSerializerContext` | JSON Source Generator（支持 AOT + 高频序列化性能） |

### 3.3 集成点（`OneCode.Infrastructure/Middleware/`）

| 类型 | 职责 |
|------|------|
| `HookMiddleware` | MAF 函数调用管道中间件，触发 PreToolUse / PostToolUse |

业务侧触发点：

| 模块 | 触发的事件 |
|------|-----------|
| `SessionManager` | `SessionStart`（startup / resume / switch）、`SessionEnd` |
| `ChatService` | `Stop`、`StopFailure` |
| `GoalDecomposer` | `UserPromptSubmit`（decompose / judge 阶段） |
| `CompactService` | `PreCompact`、`PostCompact` |

---

## 4. 事件清单

10 种生命周期事件，每种事件有对应的 matcher 字段和可选值：

| 事件 | 触发时机 | matcher 字段 | 可选值 | 可阻断 |
|------|---------|-------------|--------|--------|
| `PreToolUse` | 工具调用执行前 | `tool_name` | Bash / Write / Read / Grep / Glob / WebFetch / WebSearch / Task / todos_add | ✅ exit code 2 |
| `PostToolUse` | 工具调用成功执行后 | `tool_name` | 同上 | ❌ |
| `Notification` | 发送通知时 | `notification_type` | permission_prompt / idle_prompt / auth_success | ❌ |
| `UserPromptSubmit` | 用户提交 prompt 后 | 无 matcher | — | ❌ |
| `SessionStart` | 新会话启动时 | `source` | startup / resume / clear / compact | ❌ |
| `Stop` | AI 响应结束前 | 无 matcher | — | ✅ exit code 2 |
| `StopFailure` | API 错误导致 turn 结束时 | `error` | rate_limit / auth_failed / billing / invalid_request / server_error / max_output_tokens / unknown | ❌ |
| `PreCompact` | 对话压缩前 | `trigger` | manual / auto | ❌ |
| `PostCompact` | 对话压缩后 | `trigger` | manual / auto | ❌ |
| `SessionEnd` | 会话结束时 | `reason` | clear / logout / prompt_input_exit / other | ❌ |

**退出码约定**（Command 类型）：

| 退出码 | 语义 | stdout / stderr 行为 |
|--------|------|---------------------|
| `0` | 成功 | stdout 为 JSON 则解析为 `HookResult`；非 JSON 作为 `Message` |
| `2` | 阻断（仅 PreToolUse / Stop 生效） | stderr 显示给模型或用户，阻止后续操作 |
| 其他 | 非阻断错误 | stderr 仅显示给用户，不阻止后续操作 |

---

## 5. 执行器类型

### 5.1 Command（`type: "command"`）

通过 CliWrap 执行外部进程，stdin 传入 JSON 格式的 `HookPayload`。

**配置字段**：

| 字段 | 类型 | 说明 |
|------|------|------|
| `command` | `string` | **必填**。Shell 命令（Windows: `cmd.exe /c`；Unix: `/bin/sh -c`） |
| `timeout` | `int?` | 超时毫秒数，默认 5000 |
| `once` | `bool` | 是否只执行一次后自动移除 |
| `priority` | `int?` | 优先级（越小越先执行），默认按配置目录推导 |
| `statusMessage` | `string?` | 状态展示消息 |

**stdin Payload 示例**（`HookPayload` 的 JSON 序列化）：

```json
{
  "event": "PreToolUse",
  "sessionId": "abc123",
  "cwd": "/home/user/project",
  "toolName": "Bash",
  "toolInput": { "command": "rm -rf /" },
  "timestamp": "2026-07-18T10:00:00Z"
}
```

**stdout 返回 JSON 控制**（exit code 0 时）：

```json
{
  "message": "展示给用户的消息",
  "preventContinuation": false,
  "additionalContext": "注入到 LLM 上下文的额外信息"
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
| `statusMessage` | `string?` | 消息标题（部分渠道支持） |
| `timeout` | `int?` | 超时毫秒数，默认 5000 |

**示例**：

```json
{
  "type": "notification",
  "provider": "feishu",
  "webhookUrl": "https://open.feishu.cn/open-apis/bot/v2/hook/xxx",
  "secret": "your-sign-secret",
  "message": "[OneCode] 事件 {{Event}} 触发于 {{Timestamp}}"
}
```

**支持的 Provider**：

| Provider 名称 | 渠道 | 消息格式 | 签名算法 |
|--------------|------|---------|---------|
| `feishu` | 飞书机器人 | `{"msg_type":"text","content":{"text":"..."}}` | HMAC-SHA256(key = timestamp + "\n" + secret, msg = "") → Base64 |
| `wechat_work` | 企业微信群机器人 | `{"msgtype":"text","text":{"content":"..."}}` | HMAC-SHA256(key = secret, msg = timestamp + "\n" + secret) → Base64 |

### 5.3 Http（`type: "http"`）

通用 HTTP 调用，用于 webhook 通知、CI/CD 触发、自定义服务集成、审计回调等场景。

**配置字段**：

| 字段 | 类型 | 说明 |
|------|------|------|
| `url` | `string` | **必填**。请求目标 URL，支持 `{{field}}` 插值 |
| `method` | `string?` | HTTP 方法（GET / POST / PUT / DELETE / PATCH），默认 POST |
| `headers` | `Dictionary<string, string>?` | 自定义请求头，值支持 `{{field}}` 插值 |
| `body` | `string?` | 请求体模板（POST/PUT/PATCH/DELETE 使用），支持 `{{field}}` 插值 |
| `timeout` | `int?` | 超时毫秒数，默认 5000 |

**示例**：

```json
{
  "type": "http",
  "method": "POST",
  "url": "https://ci.example.com/api/trigger",
  "headers": {
    "Authorization": "Bearer {{Token}}",
    "X-Event": "{{Event}}"
  },
  "body": "{\"event\":\"{{Event}}\",\"tool\":\"{{ToolName}}\",\"cwd\":\"{{Cwd}}\",\"timestamp\":\"{{Timestamp}}\"}"
}
```

**与 Notification 的区别**：

| 维度 | Http | Notification |
|------|------|--------------|
| 目标 | 通用 HTTP 调用（自定义 URL/Method/Headers/Body） | 消息推送业务场景（飞书/企微等固定渠道格式） |
| 灵活性 | 高（完全自定义） | 低（渠道特定格式） |
| 签名 | 自行通过 headers 实现 | Provider 内置 HMAC 签名 |
| 响应解析 | 仅判断 HTTP 状态码 | 解析渠道特定响应字段（code/errcode 等） |

---

## 6. 配置文件

### 6.1 文件位置与优先级

| 文件 | 作用域 | 基础优先级 |
|------|--------|-----------|
| `~/.onecode/hooks.json` | 用户级（全局） | 100 |
| `<cwd>/.onecode/hooks.json` | 项目级 | 200 |

两个文件都会被加载，项目级 hook 与用户级 hook 共存，按 priority 升序串行执行。

### 6.2 文件格式

`hooks.json` 的根对象即为 hooks 内容（按事件名分组），支持两种格式：

#### 新格式（推荐）：matcher-group

每个事件下是 matcher 分组数组，每个分组包含 `matcher` 和 `hooks`：

```json
{
  "preToolUse": [
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
  "postToolUse": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "http",
          "method": "POST",
          "url": "https://audit.example.com/api/tool",
          "body": "{\"tool\":\"{{ToolName}}\",\"event\":\"{{Event}}\"}"
        }
      ]
    }
  ],
  "sessionStart": [
    {
      "matcher": "startup",
      "hooks": [
        {
          "type": "command",
          "command": "echo 'Session started'"
        }
      ]
    }
  ],
  "stop": [
    {
      "matcher": "",
      "hooks": [
        {
          "type": "notification",
          "provider": "wechat_work",
          "webhookUrl": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx",
          "message": "AI 响应结束于 {{Timestamp}}"
        }
      ]
    }
  ]
}
```

#### 旧格式（兼容）：平铺

每个事件下直接是 hook 配置数组，自动包装为 `matcher=""`（匹配所有）：

```json
{
  "preToolUse": [
    { "type": "command", "command": "echo 'Before tool'" }
  ],
  "postToolUse": [
    { "type": "command", "command": "echo 'After tool'" }
  ]
}
```

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

> **注意**：matcher 字段名因事件而异（如 PreToolUse 为 `tool_name`，SessionStart 为 `source`）。详见 [事件清单](#4-事件清单)。无 matcher 的事件（UserPromptSubmit / Stop）使用 `""` 或 `"*"`。

### 6.4 策略开关（settings.json）

Hook 策略开关在 `settings.json` 中配置（以 `hooks` 开头的键会被白名单接受）：

| 键 | 类型 | 默认值 | 作用 |
|----|------|--------|------|
| `hooks.disableAll` | `bool` | `false` | 禁用所有 hook |
| `hooks.allowManagedOnly` | `bool` | `false` | 仅允许系统级 hook（优先级 0-99） |
| `hooks.strictPluginOnly` | `bool` | `false` | 严格插件模式，仅允许插件 hook |

---

## 7. 使用介绍

### 7.1 `/hooks` 命令

| 子命令 | 语法 | 说明 |
|--------|------|------|
| 无参数 | `/hooks` | 概览：持久 hook 数 + 策略状态 + 配置文件路径 |
| `list` / `ls` | `/hooks list` | 完整 hook 列表（按 source 分组：Managed / User / Project / Plugin） |
| `events` | `/hooks events` | 可用 hook 事件列表（含 matcher 字段） |
| `status` | `/hooks status` | Hook 策略状态（工作区信任 / 禁用 / managed-only / strict-plugin） |

`/hooks` 概览示例：

```text
Hooks: lifecycle hook system

  Persistent hooks: 3

  Workspace trusted: yes

Config files:
  ~/.onecode/hooks.json         (user-level, priority 100)
  .onecode/hooks.json           (project-level, priority 200)

Subcommands: /hooks list | /hooks events | /hooks status
```

`/hooks list` 示例：

```text
User hooks:
  PreToolUse:
    ✗[100] Command    config:PreToolUse:Command:abc123
             command: echo 'About to run Bash'
  PostToolUse:
   [100] Http       config:PostToolUse:Http:def456

Project hooks:
  Stop:
   [200] Notification config:Stop:Notification:ghi789
```

> `✗` 表示该 hook 被策略过滤（如 `allowManagedOnly` 模式下用户级 hook 被禁用）。

### 7.2 典型场景示例

#### 场景 1：危险命令阻断（PreToolUse + Command）

阻止 `rm -rf /` 等危险命令执行：

```json
{
  "preToolUse": [
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

#### 场景 2：工具执行后飞书通知（PostToolUse + Notification）

```json
{
  "postToolUse": [
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

#### 场景 3：会话启动触发 CI（SessionStart + Http）

```json
{
  "sessionStart": [
    {
      "matcher": "startup",
      "hooks": [
        {
          "type": "http",
          "method": "POST",
          "url": "https://ci.example.com/api/onecode/start",
          "headers": {
            "Authorization": "Bearer ci-token-xxx"
          },
          "body": "{\"session\":\"{{SessionId}}\",\"cwd\":\"{{Cwd}}\"}"
        }
      ]
    }
  ]
}
```

#### 场景 4：压缩前后审计（PreCompact / PostCompact）

```json
{
  "preCompact": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "command",
          "command": "echo '[audit] compact starting' >> ~/.onecode/audit.log"
        }
      ]
    }
  ],
  "postCompact": [
    {
      "matcher": "*",
      "hooks": [
        {
          "type": "command",
          "command": "echo '[audit] compact done' >> ~/.onecode/audit.log"
        }
      ]
    }
  ]
}
```

#### 场景 5：错误停止告警（StopFailure + Notification）

```json
{
  "stopFailure": [
    {
      "matcher": "rate_limit|server_error",
      "hooks": [
        {
          "type": "notification",
          "provider": "wechat_work",
          "webhookUrl": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx",
          "message": "⚠️ OneCode 错误停止 ({{Event}}) @ {{Timestamp}}"
        }
      ]
    }
  ]
}
```

### 7.3 编程式触发 Hook

业务模块可通过注入 `IHookExecutionService` 直接触发生命周期事件：

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
            Event = HookEvent.UserPromptSubmit,
            Cwd = Environment.CurrentDirectory,
            UserMessage = "some user prompt",
        };

        // actualMatcherValue 为对应事件的 matcher 字段值
        // （如 PreToolUse 传 tool_name，SessionStart 传 source）
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
            Event = HookEvent.PostToolUse,
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

`Notification` 和 `Http` 类型执行器支持 `{{Field}}` 模板插值，字段来自 `HookPayload`：

| 字段 | 类型 | 说明 |
|------|------|------|
| `{{Event}}` | `string` | 事件名称（如 `PreToolUse`） |
| `{{SessionId}}` | `string` | 会话 ID |
| `{{Cwd}}` | `string` | 当前工作目录 |
| `{{ToolName}}` | `string` | 工具名称（仅工具相关事件） |
| `{{UserMessage}}` | `string` | 用户消息（UserPromptSubmit 事件） |
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
  "url": "https://api.example.com/{{Event}}",
  "headers": { "X-Tool": "{{ToolName}}" },
  "body": "{\"cwd\":\"{{Cwd}}\",\"ts\":\"{{Timestamp}}\"}"
}
```

---

## 9. 优先级与作用域

### 9.1 优先级范围约定

| 优先级范围 | 来源 | 说明 |
|-----------|------|------|
| `0-99` | Managed（系统内置） | 系统级 hook，`allowManagedOnly` 模式下仍允许 |
| `100-199` | User（用户级） | `~/.onecode/hooks.json` 加载，默认 priority 100 |
| `200-299` | Project（项目级） | `<cwd>/.onecode/hooks.json` 加载，默认 priority 200 |
| `300+` | Plugin（插件） | 插件运行时注册 |

### 9.2 执行顺序

同一事件下多个匹配的 hook 按 `priority` **升序**串行执行（数值越小越先执行）。执行结果通过 `HookResultAggregator` 聚合：

| 字段类型 | 聚合策略 |
|---------|---------|
| 布尔字段（`PreventContinuation` / `Retry`） | OR（任一为 true 则结果为 true） |
| 列表字段（`BlockingErrors` / `AdditionalContexts`） | 累加 |
| 字符串字段（`Message` / `StopReason` / `InitialUserMessage`） | last-write-wins |
| `UpdatedInput` / `UpdatedMcpToolOutput` | last-write-wins |

### 9.3 Once Hook

`once: true` 的 hook 执行一次后自动从 Registry 移除，适合"仅首次触发"的场景（如初始化提示）。

---

## 10. 策略与安全

### 10.1 工作区信任

Hook 仅在**受信任工作区**中触发。`HookPolicyService.IsCurrentWorkspaceTrusted()` 检查当前工作目录是否在 `settings.json` 的 `TrustedDirectories` 列表中（支持子目录继承）。

未受信任的工作区中所有 hook 都不会触发，避免恶意仓库通过 `hooks.json` 执行任意命令。

### 10.2 策略开关

| 开关 | 行为 |
|------|------|
| `hooks.disableAll = true` | 全局禁用所有 hook，`FireAsync` 直接返回空结果 |
| `hooks.allowManagedOnly = true` | 仅允许 priority < 100 的系统级 hook，过滤用户级与项目级 |
| `hooks.strictPluginOnly = true` | 严格插件模式（预留，当前仅展示状态） |

### 10.3 异常处理策略

| 场景 | 策略 | 说明 |
|------|------|------|
| Pre-hook（PreToolUse）异常 | **fail-closed** | 异常转为 `ToolResult.Error` + `ctx.Terminate`，阻止工具执行 |
| Post-hook（PostToolUse）异常 | **fail-soft** | 仅记日志，保留原工具结果返回 |
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

### 11.1 新增执行器类型

如需支持新的执行器类型（如 `Webhook` / `Lambda` / `Redis`）：

1. **扩展 `HookType` 枚举**（`OneCode.Core/Hooks/HookTypes.cs`）：

```csharp
public enum HookType
{
    Command,
    Notification,
    Http,
    Lambda,  // 新增
}
```

2. **更新 `HookTypeParser`**（`OneCode.Core/Hooks/HookTypeParser.cs`）：

```csharp
public static HookType Parse(string? type) => type?.ToLowerInvariant() switch
{
    "command" => HookType.Command,
    "notification" => HookType.Notification,
    "http" => HookType.Http,
    "lambda" => HookType.Lambda,  // 新增
    _ => HookType.Command,
};
```

3. **实现 `IHookExecutor`**（`OneCode.App/Services/Hooks/LambdaHookExecutor.cs`）：

```csharp
public sealed class LambdaHookExecutor : IHookExecutor
{
    public HookType Type => HookType.Lambda;

    public async Task<HookResult?> ExecuteAsync(
        HookPayload payload, HookConfig config, CancellationToken ct)
    {
        // 实现调用逻辑
        return null;
    }
}
```

4. **DI 注册**（`ServiceCollectionExtensions.Business.cs`）：

```csharp
services.AddSingleton<IHookExecutor, LambdaHookExecutor>();
```

### 11.2 新增通知渠道

如需支持新的通知渠道（如钉钉 / Slack / Discord）：

1. **实现 `INotificationProvider`**（推荐继承 `WebhookNotificationProviderBase`）：

```csharp
public sealed class DingTalkNotificationProvider(HttpClient httpClient, ILogger<DingTalkNotificationProvider>? logger = null)
    : WebhookNotificationProviderBase(httpClient, logger)
{
    public override string Name => "dingtalk";
    protected override string CodeFieldName => "errcode";
    protected override string MsgFieldName => "errmsg";
    protected override string ProviderDisplayName => "DingTalk";

    protected override object BuildPayload(NotificationMessage message) => new
    {
        msgtype = MsgTypeText,
        text = new { content = message.Text },
    };

    protected override string ComputeSign(string timestamp, string secret)
    {
        // 钉钉签名算法
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(timestamp + "\n" + secret);
        return ComputeHmacSha256Base64(keyBytes, messageBytes);
    }
}
```

2. **DI 注册**（`ServiceCollectionExtensions.Business.cs`）：

```csharp
services.AddSingleton<INotificationProvider, DingTalkNotificationProvider>();
services.AddHttpClient<DingTalkNotificationProvider>();
```

3. **使用**（`hooks.json`）：

```json
{
  "type": "notification",
  "provider": "dingtalk",
  "webhookUrl": "https://oapi.dingtalk.com/robot/send?access_token=xxx",
  "secret": "your-sign-secret",
  "message": "事件 {{Event}} 触发 @ {{Timestamp}}"
}
```

### 11.3 新增生命周期事件

如需新增生命周期事件（如 `OnTokenBudgetExceeded` / `PreFileWrite`）：

1. **扩展 `HookEvent` 枚举**（`OneCode.Core/Hooks/HookEvent.cs`）
2. **更新 `HookEventMetadataRegistry`**（`OneCode.Core/Hooks/HookEventMetadata.cs`）添加事件元数据
3. **在业务模块触发**：注入 `IHookExecutionService` 并调用 `FireAsync`

---

## 12. 调试与排查

### 12.1 Hook 未触发

排查步骤：

1. **运行 `/hooks status`** 检查策略状态：
   - `Workspace trusted: NO` → 工作区未受信任，需在 `settings.json` 的 `TrustedDirectories` 中添加当前目录
   - `All hooks disabled: YES` → `hooks.disableAll` 被设为 true
   - `Managed-only mode: YES` → 仅允许 priority < 100 的 hook

2. **运行 `/hooks list`** 确认 hook 已注册：
   - 未出现 → 检查 `hooks.json` 路径与 JSON 语法
   - 出现但带 `✗` 标记 → 被策略过滤（见上一步）

3. **检查 matcher**：
   - 运行 `/hooks events` 确认事件的 matcher 字段名
   - 确认 `actualMatcherValue` 与 matcher pattern 匹配（如 `Bash` vs `bash` 大小写不敏感）

### 12.2 Hook 执行失败

- **Command 类型**：手动执行 `command` 字段，确认退出码与输出
- **Http 类型**：检查 URL 可达性、Headers 格式、Body JSON 合法性
- **Notification 类型**：检查 `webhookUrl` 有效、`secret` 正确、Provider 名称匹配

### 12.3 Hook 阻断未生效

- 仅 `PreToolUse` 和 `Stop` 事件支持阻断（exit code 2）
- 确认 exit code 为 `2`（其他非零退出码视为非阻断错误）
- 查看 stderr 输出（阻断时显示给模型或用户）

### 12.4 日志查看

Hook 执行相关日志通过 `ILogger` 输出，关键日志类别：

| 日志类别 | 关键消息 |
|---------|---------|
| `HookExecutionService` | `Hook execution skipped: hooks globally disabled` / `workspace not trusted` |
| `HookExecutionService` | `Hook '{Name}' execution error` |
| `CommandHookExecutor` | `Command hook execution failed` / `Command hook timed out` |
| `HttpHookExecutor` | `HTTP hook timed out` / `HTTP hook execution failed` |
| `NotificationHookExecutor` | `Notification provider '{Provider}' not registered` |
| `HookConfigBootstrapper` | `Bootstrapped {Count} hooks total` |

---

## 13. 相关文档

- [ADR 0005: Hook 模块架构设计](./adr/0005-hook-module-design.md) — 架构决策、数据模型、实现细节
- [设置文档 - Hooks 配置](./settings.md#hooks-配置) — `hooks.json` 与策略开关
- [命令文档 - /hooks](./commands.md#hooks) — `/hooks` 命令完整说明
- [ADR 0001: Permission vs ToolApproval vs Filter](./adr/0001-permission-vs-toolapproval-vs-filter.md) — HookMiddleware 在 MAF 管道中的位置
