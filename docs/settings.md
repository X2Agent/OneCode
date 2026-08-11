# OneCode 配置项参考

本文档列出 `settings.json` 中所有合法配置项及其作用。配置文件只接受文档声明的键；未知键、点号属性名和类型错误都会导致该次加载失败，并保留上一份有效快照。分组配置必须使用嵌套 JSON，例如 `autodream.enabled` 写为 `{ "autodream": { "enabled": true } }`。

## 配置文件位置

OneCode 支持两级配置文件：

| 级别 | 路径 | 作用域 |
|---|---|---|
| 全局 | `~/.onecode/settings.json` | 所有项目共享，存储 API 密钥、信任目录等 |
| 项目级 | `<cwd>/.onecode/settings.json` | 仅当前项目生效，覆盖全局同名键 |

项目级配置优先于全局配置。`allowedDirectories` 等项目特定配置应写入项目级文件，避免跨项目污染。

---

## 配置优先级

OneCode 由 `ConfigManager` 统一按以下优先级解析（高 → 低）：

1. **会话覆盖**：只影响当前进程，不持久化
2. **环境变量** `ONECODE_*`：只读覆盖，设置页不会回写
3. **项目级** `settings.json`：`<cwd>/.onecode/settings.json`
4. **用户级** `settings.json`：`~/.onecode/settings.json`
5. **内置默认值**

所有业务组件只读取统一的 `ConfigSnapshot`，不再自行读取 `ONECODE_API_KEY`、`ONECODE_MODEL` 等环境变量。

配置写入采用显式作用域 Patch，只修改目标文件中本次提交涉及的键；不会把项目值或环境变量值复制进用户配置。

### `/config` 命令

```text
/config list
/config get <key>
/config set <user|project|session> <key> <value>
/config remove <user|project|session> <key>
```

`remove` 删除指定作用域中的覆盖值，使该键恢复继承下一级配置。旧的无作用域 `/config set <key> <value>` 语法不再支持。

在交互式 TUI 中执行 `/config` 会打开设置弹窗：底部提供“保存”和“取消”按钮，也可使用 `Ctrl+S` 保存、`Esc` 取消。API Key 使用单个掩码输入框显示当前有效值；内容不变时不会重复写入，修改后替换当前作用域密钥，清空后保存则删除当前作用域密钥并恢复继承。其他配置如需恢复继承，使用显式 `/config remove <scope> <key>`，不在常规设置表单中提供任意键删除入口。

### 生效时机

| 类型 | 配置项 | 语义 |
|---|---|---|
| 立即生效 | `showThinking`、信任/额外目录 | 保存后立即更新当前进程 |
| 下次操作生效 | `model`、`fastModel`、思考参数、`maxTurns`、通知 | 不影响正在执行的请求，下一次操作读取新快照 |
| 重启后生效 | `provider`、`baseUrl`、`apiKey`、`ollamaContextWindow` | `IChatClient` 构造时固化，重启后应用 |

---

## 配置项一览

### API / 模型提供方

| 键 | 类型 | 默认值 | 作用 |
|---|---|---|---|
| `apiKey` | `string` | — | LLM 服务商 API 密钥。可被环境变量 `ONECODE_API_KEY` 覆盖 |
| `baseUrl` | `string` | — | 自定义 API 基址，用于代理或自托管端点。可被 `ONECODE_BASE_URL` 覆盖 |
| `provider` | `string` | `anthropic` | 模型提供方标识：`anthropic` / `openai` / `ollama`。可被 `ONECODE_PROVIDER_OVERRIDE` 覆盖 |
| `model` | `string` | — | 主模型 ID（如 `claude-sonnet-4-6`）。可被 `ONECODE_MODEL` 覆盖 |
| `fastModel` | `string` | — | 辅助/快速模型 ID，供 hooks、记忆提取、下一步提示建议等轻量调用使用。未配置时回退到主模型。推荐通过 `/fastmodel` 命令调整，内部通过 `ModelManager.GetFastModel()` 消费 |

**示例：**

```json
{
  "apiKey": "sk-ant-...",
  "provider": "anthropic",
  "model": "claude-sonnet-4-6",
  "fastModel": "claude-haiku-4-6"
}
```

### 权限（Permissions）

| 键 | 类型 | 默认值 | 级别 | 作用 |
|---|---|---|---|---|
| `permissionMode` | `string` | `default` | 全局 | 权限策略模式，见下表 |
| `trustedDirectories` | `string[]` | `[]` | 全局 | 已信任项目列表，首次进入时不再弹信任提示。存储用户已确认信任的目录，子目录隐式继承 |
| `allowedDirectories` | `string[]` | `[]` | 项目级 | 工作区允许访问的额外目录白名单（路径遍历防护）。工具可读写 cwd 外的这些目录。通过 `/add-dir --persist` 写入项目级配置 |
| `hasTrustAccepted` | `bool` | `false` | 全局 | 是否已接受当前项目根目录信任 |

> **trustedDirectories vs allowedDirectories**：
> - `trustedDirectories`：**信任记忆**。记住用户已确认信任的项目目录，避免重复弹窗。全局存储，不参与运行时路径校验。
> - `allowedDirectories`：**额外目录白名单**。允许工具访问 cwd 之外的目录（如 monorepo 子项目）。项目级存储，参与运行时路径校验。
>
> 两者职责不同，不应合并。若合并会导致 `/add-dir` 添加的目录被当作“已信任目录”，从而在直接启动该目录时跳过信任确认，绕过安全门禁。

**`permissionMode` 可选值：**

| 值 | 说明 |
|---|---|
| `default` | 默认模式，所有敏感操作需用户确认 |
| `bypassPermissions` | 跳过所有权限检查（危险，仅限可信环境） |
| `plan` | 计划模式，只读分析，不执行任何写操作 |
| `auto` | YOLO 自动分类模式，启用 LLM 安全分类器自动判断 |
| `acceptEdits` | 文件写入 + 常规 Shell 自动放行 |

### 会话约束（Conversation）

| 键 | 类型 | 默认值 | 作用 |
|---|---|---|---|
| `maxTurns` | `int` | `100` | 单次会话最大对话轮数 |
| `maxBudgetUsd` | `double` | `10.0` | 单次会话最大花费上限（美元），用于预算熔断 |

### 功能开关（Features）

| 键 | 类型 | 默认值 | 作用 |
|---|---|---|---|
| `nextPromptSuggesterEnabled` | `bool` | `true` | 是否生成下一步输入建议 |
| `notificationsEnabled` | `bool` | `false` | 是否启用本地任务完成通知 |

详细日志由运行标志 `ONECODE_VERBOSE` 控制，不属于持久化配置。

### Web 搜索

| 键 | 类型 | 默认值 | 作用 |
|---|---|---|---|
| `webSearchProvider` | `string` | `duckduckgo` | 搜索引擎提供方（`duckduckgo` / `brave` 等） |
| `webSearchApiKey` | `string` | — | 搜索引擎 API 密钥（Brave 等付费引擎需要） |

### 扩展思考（Thinking）

| 键 | 类型 | 默认值 | 作用 |
|---|---|---|---|
| `effortValue` | `string` | `medium` | 推理努力级别：`low` / `medium` / `high` / `max`。决定 thinking 预算和自适应启用阈值 |
| `thinkingEnabled` | `bool` | `false` | 是否启用扩展思考（extended thinking） |
| `showThinking` | `bool` | `false` | 是否在 TUI 中显示思考内容 |

> `effortValue`、`thinkingEnabled` 属于“下次操作生效”。设置页、`/config` 与 `/think` 均通过同一配置服务写入，并同步当前运行时状态；已经开始执行的请求仍使用启动时捕获的参数。

### Ollama

| 键 | 类型 | 默认值 | 作用 |
|---|---|---|---|
| `ollamaContextWindow` | `int` | `32768` | Ollama 请求的 `num_ctx` 上下文窗口；重启后生效 |

### Goal 模式预算

| 键 | 类型 | 默认值 | 作用 |
|---|---|---|---|
| `goal.maxSubGoalAttempts` | `int` | `20` | 单次 Goal 运行允许的子目标尝试总数 |
| `goal.maxTurnsPerSubGoal` | `int` | `50` | 每个子目标允许的最大轮数 |
| `goal.maxTotalTokens` | `long` | `200000` | Goal 运行的总 Token 预算 |
| `goal.maxWallClockHours` | `double` | `2.0` | Goal 运行的最长墙钟时间（小时） |
| `goal.maxCostUsd` | `decimal` | `5.0` | Goal 运行的成本上限（美元） |

磁盘格式使用嵌套对象，例如：

```json
{
  "goal": {
    "maxSubGoalAttempts": 20,
    "maxTurnsPerSubGoal": 50,
    "maxTotalTokens": 200000,
    "maxWallClockHours": 2.0,
    "maxCostUsd": 5.0
  }
}
```

### AutoDream（后台记忆整合）

AutoDream **默认开启**，开箱即用。如需调整门控阈值，可在 `settings.json` 中配置：

| 键 | 类型 | 默认值 | 作用 |
|---|---|---|---|
| `autodream.enabled` | `bool` | `true` | 是否启用 AutoDream。设为 `false` 关闭 |
| `autodream.minHours` | `int` | `6` | 时间门控：距上次整合的最小间隔小时数 |
| `autodream.minSessions` | `int` | `3` | 会话门控：触发整合所需的最小新会话数 |

**示例：**

```json
{
  "autodream": {
    "enabled": true,
    "minHours": 12,
    "minSessions": 3
  }
}
```

> **模型**：AutoDream 直接复用已有的 `fastModel`（未配置时回退到主 `model`），不需要单独配置模型。
>
> **远程模式**：`ONECODE_REMOTE=true` 时无论配置如何都自动关闭 AutoDream。
>
> 详见 [后台服务文档](./background-services.md#5-autodream-记忆整合)。

---

## 环境变量

以下环境变量以 `ONECODE_` 前缀加载，会覆盖 `settings.json` 中的对应值：

| 环境变量 | 覆盖的配置键 |
|---|---|
| `ONECODE_API_KEY` | `apiKey` |
| `ONECODE_BASE_URL` | `baseUrl` |
| `ONECODE_PROVIDER_OVERRIDE` | `provider` |
| `ONECODE_MODEL` | `model` |
| `ONECODE_WEB_SEARCH_PROVIDER` | `webSearchProvider` |
| `ONECODE_WEB_SEARCH_API_KEY` | `webSearchApiKey` |
| `ONECODE_AUTODREAM` | `autodream.enabled` |
| `ONECODE_AUTODREAM_MIN_HOURS` | `autodream.minHours` |
| `ONECODE_AUTODREAM_MIN_SESSIONS` | `autodream.minSessions` |
| `ONECODE_REMOTE` | `true` 时强制关闭 AutoDream（远程模式） |

> 环境变量由 `ConfigManager` 映射到配置键并标记来源为 `Environment`。环境变量作用域只读，任何保存操作都不会将其值持久化到 `settings.json`。
>
> `ONECODE_DEBUG`、`ONECODE_VERBOSE`、`ONECODE_REMOTE`、`ONECODE_IS_WORKER` 属于进程运行标志，不是持久化配置项。

---

## Hooks 配置

Hook **定义**存放在独立文件中，不在 `settings.json` 内：

| 文件 | 作用域 | 优先级 |
|---|---|---|
| `~/.onecode/hooks.json` | 用户级 | 100 |
| `<cwd>/.onecode/hooks.json` | 项目级 | 200 |

`hooks.json` 的根对象即为 hooks 内容（不再是 `settings.json` 的子属性），示例：

```json
{
  "preToolUse": [
    {
      "matcher": "Edit",
      "hooks": [
        { "type": "command", "command": "echo 'About to edit'" }
      ]
    }
  ],
  "postToolUse": [
    {
      "matcher": "Bash",
      "hooks": [
        { "type": "command", "command": "notify-send 'Command finished'" }
      ]
    }
  ]
}
```

Hook 策略由独立的 Hook 子系统管理，不属于 `settings.json` 配置。通过 `/hooks` 命令可查看已注册 Hook 和当前工作区信任状态。

> **更多详情**：Hook 子系统的完整设计、事件清单、执行器类型、扩展指南参见 [Hook 模块文档](./hooks.md)，架构决策与实现细节参见 [ADR 0005](./adr/0005-hook-module-design.md)。

---

## 完整示例

### 全局配置（`~/.onecode/settings.json`）

```json
{
  "apiKey": "sk-ant-...",
  "provider": "anthropic",
  "model": "claude-sonnet-4-6",
  "fastModel": "claude-haiku-4-6",
  "permissionMode": "default",
  "trustedDirectories": ["/home/user/projects"],
  "hasTrustAccepted": true,
  "maxTurns": 100,
  "maxBudgetUsd": 10.0,
  "webSearchProvider": "duckduckgo",
  "effortValue": "medium",
  "thinkingEnabled": false,
  "showThinking": false,
  "nextPromptSuggesterEnabled": true,
  "notificationsEnabled": false,
  "ollamaContextWindow": 32768
}
```

### 项目级配置（`<cwd>/.onecode/settings.json`）

```json
{
  "allowedDirectories": [
    "../shared-lib",
    "../common-utils"
  ]
}
```

---

## 配置项与代码的对应关系

| 配置键 | 常量定义 | 消费位置 |
|---|---|---|
| `apiKey` | `ConfigKeys.ApiKey` | `AppSettings.ApiKey`、`ReadApiConfig` |
| `baseUrl` | `ConfigKeys.BaseUrl` | `AppSettings.BaseUrl`、`ReadApiConfig` |
| `provider` | `ConfigKeys.Provider` | `AppSettings.Provider`、`ReadApiConfig` |
| `model` | `ConfigKeys.Model` | `AppSettings.Model`、`ModelManager.GetMainModel` |
| `fastModel` | `ConfigKeys.FastModel` | `ModelManager.GetFastModel`、`ReadApiConfig`、`FastModelCommand` |
| `permissionMode` | `ConfigKeys.PermissionMode` | `AppSettings.PermissionMode` |
| `trustedDirectories` | —（字面量） | `AppSettings.TrustedDirectories`（全局） |
| `allowedDirectories` | —（字面量） | `AppSettings.AllowedDirectories`（项目级覆盖全局） |
| `hasTrustAccepted` | —（字面量） | `AppSettings.HasTrustAccepted` |
| `maxTurns` | `ConfigKeys.MaxTurns` | `AppSettings.MaxTurns` |
| `maxBudgetUsd` | `ConfigKeys.MaxBudgetUsd` | `AppSettings.MaxBudgetUsd` |
| `webSearchProvider` | —（字面量） | `AppSettings.WebSearchProvider` |
| `webSearchApiKey` | —（字面量） | `AppSettings.WebSearchApiKey` |
| `effortValue` | —（字面量） | `InteractiveModeExecutor`、`EffortCommand`、`TuiHostConfigurator`、`ThinkingParamsResolver`（经 `AppState`） |
| `thinkingEnabled` | —（字面量） | `InteractiveModeExecutor`、`ThinkCommand`、`TuiHostConfigurator` |
| `showThinking` | —（字面量） | `InteractiveModeExecutor`、`ThinkCommand`、`TuiHostConfigurator` |
| `nextPromptSuggesterEnabled` | `ConfigKeys.NextPromptSuggesterEnabled` | `AppSettings.NextPromptSuggesterEnabled` |
| `autodream.*` | —（点路径） | `AutoDreamService` |
| `goal.*` | —（点路径） | `OrchestrationStreamService` |

> **配置元数据真相源**：`src/OneCode.Infrastructure/Config/ConfigModels.cs` 中的 `SettingDescriptors`。新增配置项时必须同时声明生效模式、密钥属性和项目作用域权限，并更新本文档。
