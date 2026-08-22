# OneCode 斜杠命令参考

本文档列出 OneCode 当前支持的全部斜杠命令、功能说明及参数用法。

命令通过 TUI 输入框以 `/` 开头触发，按 `Tab` 自动补全，输入 `?` 或 `/help` 可查看内置帮助。

---

## 命令分类

OneCode 命令按 `CommandCategory` 分为 5 类：

| 类别 | 说明 | 数量 |
|---|---|---|
| `Builtin` | 内置通用命令（配置、模式、工具等） | 22 |
| `Session` | 会话管理（记忆、检查点、导出、队列等） | 8 |
| `Diagnostic` | 诊断命令（环境检查、状态统计） | 3 |
| `Skill` | 技能安装与 MCP 服务器管理 | 2 |
| `Git` | Git 工作流命令 | 6 |

---

## 命令开发约定

- **基类**：所有命令继承 `Command`（位于 `OneCode.Core/Commands/Command.cs`），必须重写 `Name`、`Description`、`ExecuteAsync(string[] args, CancellationToken ct)`。
- **参数解析**：项目不使用特性式绑定，而是约定式手动解析：
  - `args.Contains("--flag")` 检测布尔标志
  - `ParseFlag(args, "--key")` 提取 key-value 参数
  - 非 `--` 前缀的参数视为位置参数
  - 子命令通过 `args[0].ToLowerInvariant() switch {...}` 分发
- **返回值约定**：

  | 方法 | 用途 |
  |---|---|
  | `CommandResult.Text(msg)` | 直接输出文本到 TUI |
  | `CommandResult.Prompt(prompt, tools?)` | 交给 LLM 处理 |
  | `CommandResult.Error(msg)` | 错误提示（红色） |
  | `CommandResult.Exit()` | 退出应用 |

- **隐藏命令**：`IsHidden = true` 的命令不会出现在 `/help` 列表中。
- **即时命令**：`Immediate = true` 的命令绕过 query 队列立即执行（如 `/session`）。
- **别名**：部分命令支持别名，如 `/help` → `?`，`/exit` → `quit`。

---

## Builtin 类别命令

### /add-dir

将一个目录添加到当前项目上下文中。

**用法**：

```
/add-dir <path> [--persist]
```

**参数**：

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `path` | 位置参数 | 是 | 要添加的目录路径 |
| `--persist` | 标志 | 否 | 持久化到配置文件 |

不带参数时列出已添加的目录。

---

### /compact

清空历史但保留压缩摘要。

**用法**：

```
/compact [--from <index>] [--up-to <index>] [instructions]
```

**参数**：

| 参数 | 简写 | 说明 |
|---|---|---|
| `--from <index>` | `-f` | 从指定消息索引开始压缩 |
| `--up-to <index>` | `-u` | 压缩到指定消息索引 |
| 其他非 flag 参数 | — | 自定义压缩指令 |

> ProgressMessage：`Compacting conversation...`

---

### /config

查看或编辑配置项（按显式作用域 Patch 修改）。

**用法**：

```
/config [list|get <key>|set <scope> <key> <value>|remove <scope> <key>]
```

`<scope>` 取值：`user` / `project` / `session`。

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` / `ls` | 列出所有配置及来源、生效模式 |
| `get <key>` | 获取指定配置值 |
| `set <scope> <key> <value>` | 在指定作用域写入配置值 |
| `remove <scope> <key>` | 移除指定作用域的配置覆盖 |

> 配置项白名单请参考 [settings.md](settings.md)。
>
> **⚠️ 生效时机**：`/config set` 会将值写入配置文件并更新内存配置。`model`、`thinkingEnabled`、`showThinking`、`effortValue` 四个键会额外经 `ApplyRuntimeState` 同步更新运行时 `AppState`：`showThinking` 立即生效；`model` / `thinkingEnabled` / `effortValue` 属“下次操作生效”（下一次 LLM 调用按新设置执行）。其余键按各自生效模式（下次操作 / 重启后）生效。

---

### /copy

将响应复制到剪贴板。

**用法**：

```
/copy [N]
```

**参数**：

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `N` | 位置参数 | `1` | 从末尾开始 1-based 的响应序号，`1` 表示最后一条 |

---

### /design-init

为当前项目初始化 `DESIGN.md` 设计系统文件。支持两种模式：项目内省（无 URL）和网站克隆（带 URL）。

`DESIGN.md` 遵循 [designmd.ai](https://designmd.ai/what-is-design-md) 的 markdown 设计系统格式，捕获颜色、排版、间距、组件、阴影等 design token，供 AI 编码工具构建一致 UI。

**用法**：

```
/design-init [url] [--force] [--no-llm] [--output <path>]
```

**参数**：

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `url` | 位置参数 | — | 目标网站 URL（克隆模式）。必须是 `http://` / `https://` 公网地址，排除 localhost / 内网 |
| `--force` | 标志 | — | 覆盖已存在的 `DESIGN.md` |
| `--no-llm` | 标志 | — | 跳过 LLM，生成静态模板（含默认 design token） |
| `--output <path>` | 标志 | `DESIGN.md` | 自定义输出路径 |

**两种模式**：

| 模式 | 触发条件 | 行为 |
|---|---|---|
| 项目内省 | 无 URL | 扫描前端文件（`.html`/`.css`/`.vue`/`.tsx`/`.jsx`/`.svelte` 等）、检测 CSS 框架（Tailwind/UnoCSS/MUI/Ant Design 等）、收集项目上下文，交给 LLM 生成项目特定的 `DESIGN.md` |
| 网站克隆 | 带 URL | 用 Playwright MCP（如已连接 `mcp__playwright__*`）截图 + 提取计算样式；否则用 `WebFetch` 抓取页面结构，交给 LLM 克隆视觉设计 |
| 静态模板 | `--no-llm` 或无 API Key | 跳过 LLM，直接生成包含默认 design token 的模板（颜色、排版、间距、组件、阴影、设计准则） |

> ProgressMessage：`initializing DESIGN.md`。
> 允许工具：`Read(*)`、`Glob(*)`、`Grep(*)`、`WebFetch(*)`、`mcp__playwright__*`（可选）、`Write(DESIGN.md)`。
> Prompt 来源：`prompts/system/design-init.prompt`（可通过 `.onecode/prompts/system/design-init.prompt` 覆盖）。
> 如 `DESIGN.md` 已存在且未指定 `--force`，命令将拒绝覆盖并提示使用 `--force`。

---

### /exit

退出 Code Assistant。

**用法**：

```
/exit
```

> 别名：`quit`。

---

### /fastmodel

查看或设置快速模型（fastModel）。

**用法**：

```
/fastmodel [<id>|off]
```

**参数**：

| 值 | 说明 |
|---|---|
| 无参数 | 显示当前 fastModel 配置 |
| `<id>` | 设置 fastModel 并持久化 |
| `off` / `none` | 清除 fastModel 配置（回退到主模型） |

> **✅ 运行时立即生效**：`/fastmodel` 会更新内存中的配置并落盘，当前会话的下一次轻量任务调用（记忆提取、Hook 执行、下一步提示建议等）即使用新模型。
>
> fastModel 未配置时自动回退到主模型（`/model`）。与 `/config set fastModel <value>` 等价但更简洁，是调整 fastModel 的推荐方式。

---

### /files

显示会话中涉及的文件列表。

**用法**：

```
/files
```

无参数。扫描会话中工具调用涉及的 `file_path` / `path` / `filepath` 字段。

---

### /help

显示所有可用命令和键盘快捷键。

**用法**：

```
/help
```

> 别名：`?`。按分类显示所有命令。

---

### /hooks

查看已注册的 hooks、策略状态和生命周期事件。

**用法**：

```
/hooks [list|events|status]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 | 概览（持久 hook 数 / 会话 hook 数 / 异步待处理数 / 策略状态） |
| `list` / `ls` | 完整 hook 列表（按 source 分组：Managed / User / Project / Plugin） |
| `events` | 可用 hook 事件列表 |
| `status` | Hook 策略状态 |

> **更多详情**：Hook 子系统的完整设计与使用方式参见 [Hook 模块文档](./hooks.md)。

---

### /init

为当前项目初始化 `AGENTS.md`。

**行为模式**：

- **LLM 模式（默认）**：当 API Key 已配置时，采集项目上下文（项目类型、README、目录结构、标记文件），交给 LLM 生成针对当前项目的 AGENTS.md。LLM 被授予 `Read` / `Glob` / `Grep` / `Write(AGENTS.md)` 工具。
- **静态模板回退**：未配置 API Key 时，根据标记文件探测项目类型，生成含 build/test 命令的静态模板。

**用法**：

```
/init [--force] [--no-llm]
```

**参数**：

| 参数 | 类型 | 说明 |
|---|---|---|
| `--force` | 标志 | 覆盖已存在的 AGENTS.md |
| `--no-llm` | 标志 | 强制使用静态模板，绕过 LLM（即使已配置 API Key） |

> Prompt 来源：`prompts/system/init.prompt`（可通过 `.onecode/prompts/system/init.prompt` 覆盖）。
> ProgressMessage：`initializing AGENTS.md`。

---

### /keybindings

查看或自定义键盘快捷键。

**用法**：

```
/keybindings [list|validate|open|reset]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 | 创建 / 打开 `keybindings.json` 编辑（首次会生成模板并写入 JSON Schema） |
| `list` | 列出默认键位绑定 |
| `validate` | 校验 `keybindings.json` 格式与必填字段 |
| `open` / `edit` | 在编辑器中打开 |
| `reset` | 重置为默认键位绑定（丢弃所有自定义） |

---

### /lsp

管理语言包和 LSP 服务器。

**用法**：

```
/lsp [list|install <lang>|uninstall <lang>|status|enable <lang>|disable <lang>]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` / `ls` | 列出所有语言包及状态 |
| `install <lang>` | 安装语言包服务器二进制 |
| `uninstall <lang>` | 卸载语言包 |
| `status` | 显示运行中的服务器状态和诊断 |
| `enable <lang>` | 启动 LSP 服务器 |
| `disable <lang>` | 停止 LSP 服务器 |

---

### /model

查看或切换当前模型。

**用法**：

```
/model [model-id]
```

**参数**：

| 值 | 说明 |
|---|---|
| 无参数 | 显示当前模型和可用模型列表 |
| `model-id` | 切换并持久化 |

> **✅ 运行时立即生效**：`/model` 会同时更新内存中的 `AppState.MainLoopModel` 和配置文件，当前会话的下一次 LLM 调用即使用新模型。
>
> 与 `/config set model <value>` 的区别：`/model` 会对输入做 Resolve 规范化（剥 provider 前缀、匹配别名），并展示可用模型列表；`/config set model` 直接原样写入，适合脚本化场景。两者都会更新运行时状态并持久化。

---

### /permissions

管理工具执行权限模式。

**用法**：

```
/permissions [mode]（仅 BUILD 模式下可见可用）
```

**参数**：

| 值 | 别名 | 说明 |
|---|---|---|
| `default` | — | 默认模式，敏感操作需用户确认 |
| `plan` | — | 计划模式，只读分析 |
| `auto` | — | YOLO 自动分类模式 |
| `acceptedits` | `accept-edits` / `accept_edits` | 文件写入自动放行 |
| `bypasspermissions` | `bypass-permissions` / `bypass_permissions` / `bypass` | 跳过所有权限检查 |
| `dontask` | `dont-ask` / `dont_ask` | 直接拒绝危险操作 |
| `bubble` | — | 气泡式权限提示 |

无参数时显示当前模式和可用模式。

> **✅ 运行时立即生效**：`/permissions` 会更新 `PermissionModeProvider`（Agent 管道实际读取的鉴权源），同步 `AppState`，并将 `permissionMode` 写入配置文件。

---

### /skills

列出或查看 skills。执行技能请用动态斜杠命令 `/<skillname>`（由 `SkillCommandSource` 注册）。

**用法**：

```
/skills [list|show <name>]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` / `ls` | 列出 bundled + 自定义 skills |
| `show <name>` 或 `<name>` | 显示 skill 内容（预览） |
| ~~`run <name>`~~ | 已移除；请改用 `/<skillname> [args]` |

---

### /team

管理 Agent 团队：列表、切换活跃团队、查看详情。

**用法**：

```
/team [list|switch <name>|info [<name>]|<name>]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` / `ls` | 列出所有团队 |
| `<name>` | 直接切换活跃团队 |
| `switch` / `use <name>` | 切换团队 |
| `info` / `show [<name>]` | 显示团队详情 |
| `help` | 显示帮助 |

> 别名：`teams`。内置团队：`feature-impl`、`code-review`、`research`。

---

### /think

配置扩展思考：模型思考开关 + reasoning_effort 努力程度 + TUI 思考块显示，共两个独立维度。

**用法**：

```
/think [on|off|low|medium|high|max|show|hide]
```

**参数**：

| 值 | 说明 |
|---|---|
| 无参数 | 显示当前状态（思考开关、effort、TUI 显示） |
| `on` / `off` | 启用 / 禁用模型扩展思考（`thinkingEnabled`） |
| `low` / `medium` / `high` / `max` | 设置 reasoning_effort（同时自动开启思考） |
| `show` / `hide` | 展开 / 折叠对话历史中的思考块（`showThinking`，立即生效） |

> **✅ 运行时立即生效**：`/think` 会同时更新内存中的 `AppState`（`ThinkingEnabled`/`EffortValue`/`ShowThinking`）和配置文件，当前会话的下一次 LLM 调用即按新设置计算 thinking 预算。
>
> 与 `/config set thinkingEnabled <value>` 的区别：`/config set` 也会同步更新运行时 `AppState`（当前会话生效），但 `/think` 提供 effort / show / hide 等更完整的校验与交互入口，仍是调整 thinking 设置的推荐方式。

---

### /upgrade

检查并安装 OneCode 更新。

**用法**：

```
/upgrade [--apply|-y] [--check|-c]
```

**参数**：

| 参数 | 说明 |
|---|---|
| 无参数 / `--check` / `-c` | 只检查 GitHub 最新 release 版本，不执行升级 |
| `--apply` / `-y` / `--yes` | 执行自动升级 |

---

### /version

显示版本信息。

**用法**：

```
/version
```

> 别名：`v`。

---

### /prompts

列出并运行 `.prompt` 模板（与 PromptManager / FilePromptStore 同一扩展名与目录布局）。

**用法**：

```
/prompts [list|run <name>]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` / `ls` | 列出项目级与用户级 `.onecode/prompts/**/*.prompt` |
| `run <name>` | 执行指定 prompt（如 `system/review`）；优先经 PromptManager 解析（含内置） |

---

### /cron

管理定时 cron 任务。

**用法**：

```
/cron [list|create|delete|pause|resume|run]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` | 列出所有 cron 任务 |
| `create` / `add <cron-expr> <prompt>` | 创建任务，支持 `--once` / `--durable` 标志 |
| `delete` / `remove` / `rm <id>` | 删除任务 |
| `pause <id>` | 暂停任务 |
| `resume <id>` | 恢复任务 |
| `run` / `trigger <id>` | 立即触发任务 |

> `--durable` 需要环境变量 `ONECODE_DURABLE_CRON=true`。

---

## Session 类别命令

### /checkpoint

管理命名会话检查点（保存 / 列表 / 恢复 / 删除）以及恢复中断的 Goal/Team 任务。

**用法**：

```
/checkpoint save [name] | list | restore [name] | delete [name] | resume [sessionId]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| `save [name]` | 保存当前消息索引为检查点（默认名：时间戳） |
| `list` | 列出所有检查点 |
| `restore [name]` | 恢复到检查点（删除之后的消息，默认最后一个） |
| `delete <name>` | 删除检查点 |
| `resume [sessionId]` | 从中断点继续 Goal/Team 任务执行；不带参数时列出所有可恢复的会话 |

> 会话级 checkpoint（`save`/`list`/`restore`/`delete`）持久化到 session JSONL。
> 工作流级 checkpoint（`resume`）为 InMemory，仅进程内有效，适用于 Goal/Team 执行被 Ctrl+C 中断后恢复。

---

### /export

将会话内容导出为 JSON。

**用法**：

```
/export [--output <path>]
```

**参数**：

| 参数 | 简写 | 默认 | 说明 |
|---|---|---|---|
| `--output <path>` | `-o` | 带时间戳的默认名 | 输出路径（必须在当前工作目录内） |

---

### /find

在会话记录中搜索关键词并滚动到匹配位置。

**用法**：

```
/find <keyword>
```

**参数**：

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `keyword` | 位置参数 | 是 | 搜索关键词 |

> 别名：`search`。TUI 路径由 `OneCodeToplevel` 拦截并滚动到匹配行；非 TUI 宿主仅提示在交互式 TUI 中使用。
> 元数据：`Immediate = true`。

---

### /insights

分析已保存会话的使用模式。

**用法**：

```
/insights
```

无参数。分析最近 100 个会话的消息数、token 用量、模型分布。

> ProgressMessage：`analyzing your sessions`。

---

### /memory

管理**可检索记忆子系统**（会话事实 + `MEMORY.md` 结构化条目）。  
项目编码规范请用 `/remember` 写入 `AGENTS.md`，不要用本命令。

**用法**：

```
/memory [list|add|remove|clear|autodream]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` | 列出会话记忆 + `MEMORY.md` 持久化条目 |
| `add [--user] <text>` | 写入 `MEMORY.md` 事实/偏好（默认可检索项目级；`--user` 为用户级） |
| `remove` / `delete <n>` | 删除第 N 条持久化条目 |
| `clear [--all]` | 清空项目级条目；`--all` 同时清空用户级 |
| `autodream [trigger\|status]` | 触发或查看 AutoDream 后台整合 |

> **与 `/remember` 的边界**：`/memory` → `MEMORY.md`（摘要注入 + `search_memories`）；`/remember` → `AGENTS.md`（项目规范 / Project Context）。详见 [docs/memory-overview.md](memory-overview.md)。


---

### /queue

管理输入队列（对话完成后自动取下一条继续执行）。

**用法**：

```
/queue [add <text> | list | drop <index> | clear]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` / `ls` | 列出队列中的所有输入（带索引） |
| `add` / `a <text>` | 添加输入到队列末尾 |
| `drop` / `remove` / `rm <index>` | 移除指定索引的输入 |
| `clear` | 清空队列 |

> 队列是内存中的单队列——query 运行时用户输入会自动入队，query 完成后自动出队执行。也可通过此命令主动预排任务序列。

---

### /rename

重命名当前会话。

**用法**：

```
/rename <new-name>
```

**参数**：

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `new-name` | 位置参数 | 是 | 新会话名 |

---

### /session

管理会话生命周期（list / new / switch / close）。运行时诊断见 `/status`。

**用法**：

```
/session [list|new|switch|close]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 | TUI：打开会话选择器；非 TUI：显示用法 |
| `list` / `ls` | 列出所有会话 |
| `new [name]` | 创建新会话（当前会话转入后台） |
| `switch <session-id>` | 切换会话 |
| `close <session-id>` | 关闭会话 |
| ~~`info`~~ | 已迁至 `/status`（仍接受并提示迁移） |

> 元数据：`Immediate = true`。

---

## Diagnostic 类别命令

### /doctor

诊断环境与配置健康：API Key 解析（settings.json → 环境变量，按 provider 判断是否必需）、settings.json 解析健康、MCP 服务器连通性（已配置 vs 已连接、每服务器工具数）、LSP 服务器状态（运行 vs 初始化）、Git 可用性（/commit、/review 等依赖）。

**用法**：

```
/doctor [info|env|setup]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `info` | 完整诊断报告 |
| `env` | 显示相关环境变量 |
| `setup` | 首次使用引导检查清单（API Key / Git / AGENTS.md） |

---

### /gc-stats

显示 .NET GC 和内存统计。

**用法**：

```
/gc-stats
```

无参数。显示堆大小、GC 回收次数、暂停时间等。

> 隐藏命令（`IsHidden = true`）。定义在 `DebugCommands.cs` 中。

---

### /status

显示当前会话的运行时诊断（身份信息、模型、权限、thinking、用量、上下文窗口）。会话生命周期见 `/session`。

**用法**：

```
/status [info|stats|window]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `info` | 会话身份 + 运行时状态（模型、权限、thinking、工具数、git 分支等） |
| `stats` | token 用量统计、缓存命中率、成本、场景分解 |
| `window` | 上下文窗口使用进度条 |

---

## Skill 类别命令

> 9 个内置技能（`/batch`、`/debug`、`/loop` 等）通过 `SkillCommandSource` 动态加载，不在此处逐一列举。详见 [docs/skills.md](skills.md)。

### /install

从本地目录或 git 仓库安装一个 skill。

**用法**：

```
/install <path>                      # 本地目录 → 项目级安装
/install <path> -g                   # 本地目录 → 全局安装
/install <git-url>                   # git 仓库 → 项目级安装
/install <git-url> -g                # git 仓库 → 全局安装
```

> git URL 会被自动识别，无需显式声明 `git` 子命令。

**参数**：

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `path` / `url` | 位置参数 | 是 | 本地 skill 源目录路径，或 git 仓库 URL |
| `-g` / `--global` | 标志 | 否 | 安装到用户级目录（`~/.onecode/skills/`），跨项目共享；省略时安装到项目级目录（`<cwd>/.onecode/skills/`） |

**支持的 git URL 格式**：

- `https://github.com/user/skill-repo.git`
- `https://github.com/user/skill-repo`
- `git://github.com/user/skill-repo.git`
- `git@github.com:user/skill-repo.git`（SSH）

**skill 内容识别**：克隆后会从仓库根目录（或 `skills/` 子目录）查找 `SKILL.md` 或任意 `.md` 文件作为 skill 内容。仓库名（去除 `.git` 后缀）作为 skill 名称。

> `--list` / `list` 参数会提示使用 `/skills list`。

---

### /mcp

管理 MCP 服务器连接。

**用法**：

```
/mcp [list|get|search|install|add|remove|connect|disconnect|enable|disable]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` / `ls` | 列出已配置和已连接的 MCP 服务器 |
| `get <name>` | 查看服务器详情（已连接时列出 tools） |
| `search <query>` | 从 Smithery 注册表搜索 MCP 服务器 |
| `install <qualifiedName>` | 从 Smithery 注册表安装 MCP 服务器（支持 `--name`、`--scope`、`--connect`） |
| `add <name> [options]` | 手动添加本地服务器 |
| `remove` / `rm <name>` | 移除服务器 |
| `connect <name>` | 连接服务器 |
| `disconnect <name>` | 断开服务器 |
| `enable <name>` | 启用服务器 |
| `disable <name>` | 禁用服务器 |

**`add` 子命令支持的选项**：

| 选项 | 说明 |
|---|---|
| `--transport <stdio\|sse\|http\|ws>` | 传输协议（必填） |
| `--command <cmd>` | 启动命令（stdio） |
| `--url <url>` | 服务地址（sse / http / ws） |
| `--scope <project\|user>` | 配置作用域 |
| `--args ...` | 透传参数（stdio 命令参数） |
| `--connect` | 添加后立即连接 |

配置写入 `.mcp.json`（用户级为 `~/.onecode/.mcp.json`，项目级为工作区 `.mcp.json`）。

**Playwright 浏览器扩展（可选）**：

```
/mcp add playwright --transport stdio --command npx --args @playwright/mcp@latest --scope user --connect
```

前置条件：Node.js 20+。写入配置不等于安装浏览器；Chromium 在首次调用 MCP 工具时下载。

---

## Git 类别命令

### /branch

创建或切换 Git 分支。

**用法**：

```
/branch [branch-name]
```

**参数**：

| 值 | 说明 |
|---|---|
| 无参数 | 列出所有分支 |
| `branch-name` | 尝试 switch，失败则 `switch -c` 创建 |

---

### /commit

创建一个 Git 提交（由 AI 生成 commit 信息）。

**用法**：

```
/commit
```

无参数。Prompt 类型命令，交给 LLM 生成 commit。

> ProgressMessage：`creating commit`。
> 允许工具：`Bash(git add:*)`、`Bash(git status:*)`、`Bash(git commit:*)`、`Bash(git diff:*)`。
> Prompt 来源：`prompts/system/commit.prompt`（可通过 `.onecode/prompts/system/commit.prompt` 覆盖）。

---

### /diff

审查 Git 变更。无参数时在 TUI 弹出图形化 Review 覆盖层（文件列表，Enter 下钻 Diff）；带参数时输出原始 git diff 文本。

**用法**：

```
/diff [--staged] [file-path]
```

**参数**：

| 参数 | 类型 | 说明 |
|---|---|---|
| （无参数） | — | TUI 中打开变更审查覆盖层 |
| `--staged` | 标志 | 仅显示 staged 变更（文本输出） |
| `file-path` | 位置参数 | 限制到指定文件（文本输出） |

---

### /rebase

将当前分支 rebase 到目标分支。

**用法**：

```
/rebase [target-branch]
```

**参数**：

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `target-branch` | 位置参数 | `main` | 目标分支 |

> 检测冲突时给出恢复提示。

---

### /review

AI 代码审查，支持严重级别、聚焦领域与结构化输出。

**用法**：

```
/review [--staged|--all|--base <ref>] [--severity critical|warning|all] [--focus security|crashes|performance|style] [--output json|text] [--no-edit] [--blame] [file-path]
```

**参数**：

| 参数 | 说明 |
|---|---|
| `--staged` | 仅 review staged 变更 |
| `--all` | review staged + unstaged（`git diff HEAD`） |
| `--base <ref>` | 对比指定分支 / commit |
| `--severity <critical\|warning\|all>` | 过滤输出严重级别（默认 all） |
| `--focus <security\|crashes\|performance\|style>` | 聚焦审查领域（默认全面审查） |
| `--output <json\|text>` | 输出格式（text 默认，json 适合 CI） |
| `--no-edit` | 只报告不修复 |
| `--blame` | 附加 git blame 上下文 |
| `file-path` | 位置参数，限制到指定文件 / 目录 |

**`--focus` 领域说明**：

| 值 | 审查范围 | Prompt 文件 |
|---|---|---|
| 无（默认） | 全面审查（Critical + Warning + Suggestion） | `prompts/system/review.prompt` |
| `security` | OWASP Top 10、密钥泄露、注入、不安全密码学、反序列化、访问控制 | `prompts/system/review-security.prompt` |
| `crashes` | 空指针、越界、除零、未初始化、资源泄漏、并发竞争、整数溢出 | `prompts/system/review-crashes.prompt` |
| `performance` | N+1 查询、不必要分配、阻塞 I/O、算法复杂度、锁竞争 | `prompts/system/review-performance.prompt` |
| `style` | 命名、一致性、可读性、重复代码、封装、可测试性 | `prompts/system/review-style.prompt` |

> ProgressMessage：`reviewing code`。
> 所有 prompt 均可通过 `.onecode/prompts/system/` 覆盖。
> 原 `/security` 命令已合并为 `/review --focus security`。

---

### /stash

管理 Git stash。

**用法**：

```
/stash [list|push [msg]|pop [n]|drop [n]]
```

**参数**：

| 子命令 | 说明 |
|---|---|
| 无参数 / `list` / `ls` | 列出 stash |
| `push` / `save [msg]` | stash 当前变更（可选消息） |
| `pop [n]` | 弹出 `stash@{n}`（默认 0） |
| `drop [n]` | 删除 `stash@{n}`（默认 0） |

---

## 汇总统计

- **总命令数**：41 个
- **隐藏命令**：`/gc-stats`
- **即时命令**（绕过 query 队列）：`/session`、`/find`、`/diff`
- **带别名的命令**：

  | 命令 | 别名 |
  |---|---|
  | `/help` | `?` |
  | `/exit` | `quit` |
  | `/version` | `v` |
  | `/team` | `teams` |
  | `/find` | `search` |

> **命令注册真相源**：`src/OneCode.App/Commands/CommandServiceExtensions.cs` 中的 `AddCommands()` 方法。本文档与此注册表保持同步，新增或删除命令时请同时更新。
