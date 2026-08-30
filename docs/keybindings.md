# OneCode 快捷键参考

本文档列出 OneCode 全部默认快捷键、可配置上下文、保留快捷键，以及自定义快捷键的方法。

快捷键通过 `~/.onecode/keybindings.json` 配置文件自定义，修改后自动热重载，无需重启。

---

## 设计概览

快捷键模块位于 [OneCode.Core/Keybindings/](../src/OneCode.Core/Keybindings/)，采用「默认绑定 + 用户覆盖」的合并策略：

```
默认绑定（代码定义）  ──┐
                       ├── 合并 → KeybindingResolver → 运行时按键分发
用户配置（JSON 文件） ──┘
```

- **默认绑定**：定义在 [KeybindingDefaults.cs](../src/OneCode.Core/Keybindings/KeybindingDefaults.cs) 的 `DefaultBindings` 数组中，是所有快捷键的源头
- **用户配置**：`~/.onecode/keybindings.json`，用户绑定追加在默认绑定之后，**后匹配的生效**（用户覆盖默认）
- **JSON Schema**：编辑器自动补全与校验，Schema 文件写入 `~/.onecode/schemas/keybindings.schema.json`
- **Resolver 强制路径**：TUI 层快捷键均通过 `KeybindingResolver` 解析；未匹配的按键不处理，直接放行到基础输入逻辑

### 核心组件

| 组件 | 文件 | 职责 |
|---|---|---|
| `KeybindingDefaults` | KeybindingDefaults.cs | 上下文常量、动作常量、默认绑定、保留快捷键 |
| `KeybindingResolver` | KeybindingResolver.cs | 运行时按键解析，支持上下文优先级与和弦序列 |
| `KeybindingLoader` | KeybindingLoader.cs | 加载用户配置，合并默认绑定，支持热重载 |
| `KeybindingSchema` | KeybindingSchema.cs | 生成 JSON Schema 与配置模板 |
| `KeybindingContextManager` | KeybindingContextManager.cs | 管理活跃上下文集合 |
| `KeybindingParser` | KeybindingParser.cs | 解析按键字符串为结构化对象 |
| `KeybindingValidator` | KeybindingValidator.cs | 验证绑定合法性（重复、保留、无效上下文/动作） |

---

## 自定义快捷键

### 配置文件位置

```
~/.onecode/keybindings.json
```

首次运行 `/keybindings` 命令会自动生成模板文件，包含全部默认绑定。

### 文件格式

```json
{
  "$schema": "file:///home/user/.onecode/schemas/keybindings.schema.json",
  "bindings": [
    {
      "context": "Global",
      "bindings": {
        "ctrl+d": "app:exit"
      }
    },
    {
      "context": "Chat",
      "bindings": {
        "shift+enter": "chat:newline",
        "ctrl+v": "chat:paste"
      }
    }
  ]
}
```

### 修改方式

| 方式 | 说明 |
|---|---|
| `/keybindings` | 打开配置文件编辑器，自动生成模板（若不存在） |
| `/keybindings list` | 列出全部生效快捷键（按键 → 动作 + 功能说明） |
| `/keybindings validate` | 校验配置文件格式与合法性 |
| `/keybindings open` / `/keybindings edit` | 在默认编辑器中打开配置文件 |
| `/keybindings reset` | 重置为默认绑定，丢弃所有自定义配置 |

### 覆盖规则

1. **用户绑定追加在默认绑定之后**，相同按键的用户绑定覆盖默认绑定
2. **设为 `null` 表示显式解绑**：`"ctrl+s": null` 会禁用该默认快捷键
3. **和弦序列**用空格分隔：`"ctrl+x ctrl+k": "chat:killAgents"`（用户自定义示例）
4. **支持 `command:` 前缀**绑定斜杠命令：`"ctrl+y": "command:compact"`（`command:` 绑定必须在 `Chat` 上下文中，否则产生验证警告；注意 ctrl+t 已被默认绑定为 chat:enterTranscript）

### 按键语法

| 修饰键 | 写法 | 示例 |
|---|---|---|
| Ctrl | `ctrl` | `ctrl+k` |
| Alt | `alt` / `opt` / `option` | `alt+enter` |
| Shift | `shift` | `shift+tab` |
| Meta | `meta` | `meta+p` |
| Cmd/Super | `cmd` / `command` / `super` / `win` | `cmd+shift+f` |

| 特殊键 | 写法 |
|---|---|
| 回车 | `enter` / `return` |
| Esc | `escape` / `esc` |
| Tab | `tab` |
| 空格 | `space` |
| 退格 | `backspace` |
| 删除 | `delete` / `del` |
| 方向键 | `up` / `down` / `left` / `right` |
| 翻页 | `pageup` / `pagedown` / `pgup` / `pgdn` |
| Home/End | `home` / `end` |

---

## 上下文

快捷键按上下文分组，仅在对应上下文活跃时生效。`Global` 上下文始终活跃。

| 上下文 | 说明 |
|---|---|
| `Global` | 全局生效，不受焦点影响 |
| `Chat` | 聊天输入框获得焦点时 |
| `Autocomplete` | 自动补全菜单可见时（由输入视图 push/pop） |
| `Transcript` | 对话区导航模式激活时（`Ctrl+T` 进入，`Esc`/`i` 退出） |
| `Diff` | Diff 审查视图聚焦时（解析时并入活跃集合） |
| `Selector` | 内联选择器（权限提示 / Plan 审批）接管键盘时 |

---

## 默认快捷键列表

### Global（全局）

| 快捷键 | 动作 | 说明 |
|---|---|---|
| `Ctrl+D` | `app:exit` | 退出应用 * |
| `Ctrl+Shift+→` | `app:sidebarWider` | 加宽右侧侧边栏（Plan/TEAM 面板，步进 4 列） |
| `Ctrl+Shift+←` | `app:sidebarNarrower` | 收窄右侧侧边栏（与分隔线拖拽共用 clamp 规则） |

> \* 标记的快捷键为保留快捷键，不可重新绑定。
>
> 其他常见全局行为不经 `keybindings.json` 配置：`Ctrl+C` 复制选中文本（编辑器层行为）；中断运行中的查询用 `Escape`（`chat:cancel`）或自定义和弦（如 `ctrl+x ctrl+k` → `chat:killAgents`）。

### Chat（聊天输入）

| 快捷键 | 动作 | 说明 |
|---|---|---|
| `Escape` | `chat:cancel` | 模型响应时中断 agent；空闲时无操作 |
| `Enter` | `chat:submit` | 提交消息 |
| `Up` | `history:previous` | 上一条历史输入 |
| `Down` | `history:next` | 下一条历史输入 |
| `Ctrl+Up` | `history:recallLast` | 召回上一条用户消息以便编辑重发 |
| `Shift+Enter` | `chat:newline` | 输入换行（需 kitty 协议） |
| `Alt+Enter` | `chat:newline` | 输入换行（通用兼容） |
| `Ctrl+V` | `chat:paste` | 智能粘贴（图片/路径/大文本折叠） |
| `Shift+Up` / `Ctrl+PgUp` | `chat:scrollUp` | 对话区向上滚动（行级，3行） |
| `Shift+Down` / `Ctrl+PgDn` | `chat:scrollDown` | 对话区向下滚动（行级，3行） |
| `PageUp` | `chat:pageUp` | 对话区向上翻页 |
| `PageDown` | `chat:pageDown` | 对话区向下翻页 |
| `Shift+Tab` | `chat:cycleTeam` | TEAM 模式下循环切换已注册团队（编排模式由团队 team.yaml 固定声明） |
| `Alt+1` .. `Alt+4` | `app:modeBuild/Plan/Team/Goal` | 工作模式直达（裸 Tab 为循环切模式：硬编码不经 Resolver，不占用绑定；macOS 需终端开启 Option 作为 Meta 键） |
| `Ctrl+T` | `chat:enterTranscript` | 进入对话区导航模式 |

### Autocomplete（自动补全）

| 快捷键 | 动作 | 说明 |
|---|---|---|
| `Up` | `autocomplete:previous` | 上一条建议 |
| `Down` | `autocomplete:next` | 下一条建议 |
| `Tab` | `autocomplete:accept` | 接受当前补全（补全激活时 Autocomplete 上下文活跃，Tab 归此绑定接管） |
| `Escape` | `autocomplete:dismiss` | 关闭补全菜单 |

### Transcript（对话区导航）

`Ctrl+T` 进入导航模式：焦点移到对话流，状态栏显示「导航模式」，绑定上下文切换为导航。游标在**可交互行**（工具 / 思考 / 错误摘要行）之间跳转并高亮。流式响应期间禁止进入，开始响应时自动退出。

| 快捷键 | 动作 | 说明 |
|---|---|---|
| `Esc` / `i` | `transcript:exit` | 退出导航模式，焦点回到输入框 |
| `J` / `K` | `transcript:next` / `transcript:previous` | 下一个 / 上一个可交互行 |
| `Enter` / `Space` | `transcript:toggle` | 展开 / 折叠当前块 |
| `C` | `transcript:copyCode` | 复制游标行文本到剪贴板 |
| `G` | `transcript:top` | 跳到第一个可交互行 |
| `Shift+G` | `transcript:bottom` | 跳到最后一个可交互行 |
| `↑↓ PgUp PgDn Home End` | — | 原生滚动（不经 Resolver，不可重映射） |

### Diff（差异审查）

`/diff` 打开审查覆盖层、`Enter` 进入文件详情后生效：

| 快捷键 | 动作 | 说明 |
|---|---|---|
| `↑` / `K` | `diff:scrollUp` | 向上滚动一行 |
| `↓` / `J` | `diff:scrollDown` | 向下滚动一行 |
| `PageUp` / `PageDown` | `diff:pageUp/pageDown` | 翻页滚动 |
| `Home` / `End` | `diff:top/bottom` | 跳顶 / 跳底 |

### Selector（内联选择器）

权限提示、Plan 审批等内联选择器接管键盘时生效：

| 快捷键 | 动作 | 说明 |
|---|---|---|
| `↑` | `selector:previous` | 上一选项 |
| `↓` | `selector:next` | 下一选项 |
| `Enter` | `selector:confirm` | 确认当前选项 |
| `Escape` | `selector:dismiss` | 取消选择器 |

---

## 保留行为

以下按键行为不经 `KeybindingResolver`，由组件直接处理，无法通过配置文件重映射：

| 按键 | 行为 | 处理位置 | 保留原因 |
|---|---|---|---|
| `Esc` 关闭 Overlay | 关闭居中浮层（设置/恢复会话/Diff 详情等） | `OverlayHost.cs` | 关闭必须永远可用，不受重映射影响 |
| `Tab`（空输入 + 占位建议） | 接受占位建议 | `ChatInputView.Keys.cs` | 行为依赖运行时状态（是否有建议），非单一动作 |
| `Tab`（斜杠前缀，补全未激活） | 打开命令补全列表 | `ChatInputView.Keys.cs` | 同上（补全未激活时 `Autocomplete` 上下文不在场） |
| `Ctrl+Right` / `Ctrl+Left` | 占位建议可见时循环切换建议 | `ChatInputView.Keys.cs` | 同上 |
| `↑↓ PgUp PgDn Home End`（导航/Diff 视图聚焦时） | 原生滚动 | `MessageListView` / `DiffView` OnKeyDown 之前 | 滚动是视图基础能力；可交互跳转已由 transcript:* / diff:* 覆盖 |
| `/find <keyword>` | 搜索会话 transcript 并跳转匹配 | `FindCommand` / TUI Dispatch | 斜杠命令入口 |
| `/diff` | 打开 Git 变更审查覆盖层（无参数时） | `DiffCommand` / TUI Dispatch | 斜杠命令入口 |

---

## 硬编码按键

以下按键行为不经过 `KeybindingResolver`，由 TUI 组件直接处理，无法通过配置文件重映射：

| 按键 | 行为 | 处理位置 |
|---|---|---|
| `Tab` | 补全激活时接受/循环建议（已迁移为 `autocomplete:accept`）；空输入时接受占位建议；其他情况切换工作模式 | `ChatInputView.Keys.cs` |
| `Ctrl+Right` / `Ctrl+Left` | 占位建议可见时循环切换建议 | `ChatInputView.Keys.cs` |
| `/find <keyword>` | 搜索会话 transcript 并跳转匹配 | `FindCommand` / TUI Dispatch |
| `/diff` | 打开 Git 变更审查覆盖层（无参数时） | `DiffCommand` / TUI Dispatch |

---

## 保留快捷键

以下快捷键不可重新绑定，配置文件中绑定这些键会产生验证警告。

### 硬编码保留

| 快捷键 | 原因 | 严重级别 |
|---|---|---|
| `Ctrl+D` | 退出，终端协议硬编码 | Error |
| `Ctrl+M` | 与 Enter 等价（终端均发送 CR） | Error |

### 终端保留

| 快捷键 | 原因 | 严重级别 |
|---|---|---|
| `Ctrl+Z` | Unix 进程挂起（SIGTSTP） | Warning |
| `Ctrl+\` | 终端退出信号（SIGQUIT） | Error |

### macOS 系统保留

| 快捷键 | 原因 | 严重级别 |
|---|---|---|
| `Cmd+C` | 系统复制 | Error |
| `Cmd+V` | 系统粘贴 | Error |
| `Cmd+X` | 系统剪切 | Error |
| `Cmd+Q` | 退出应用 | Error |
| `Cmd+W` | 关闭窗口/标签 | Error |
| `Cmd+Tab` | 应用切换器 | Error |
| `Cmd+Space` | Spotlight | Error |

---

## 和弦序列

支持 Emacs 风格的和弦序列（如 `Ctrl+X Ctrl+K`）：

- 和弦超时时间为 **1000ms**，超时后自动取消
- 按 `Escape` 可取消当前挂起的和弦
- 和弦序列在配置文件中用空格分隔：`"ctrl+x ctrl+k": "chat:killAgents"`（用户自定义示例）
- **Eager-fire（立即触发）**：若某个按键自身已精确绑定动作、同时又是更长和弦的前缀，该键会**立即触发自身绑定**，并在 1s 窗口内继续监听和弦尾键。纯前缀键（自身无绑定，如 `ctrl+x ctrl+k` 的 `ctrl+x`）不受影响，仍等待尾键。
- 模式直达为独立单键 `Alt+1..4`，不与任何和弦共享监听窗口；裸 Tab 循环为硬编码行为，不经 Resolver。

---

## 热重载

`KeybindingLoader` 通过 `FileSystemWatcher` 监视配置文件变化：

- 文件保存后自动重新加载，**无需重启应用**
- 文件删除后自动回退到默认绑定
- 文件写入稳定性阈值为 500ms，避免编辑器原子写入触发多次重载
- 重载后触发 `BindingsChanged` 事件，通知 UI 更新

---

## 配置示例

### 示例 1：禁用某个默认快捷键

禁用 `Ctrl+D`（退出应用）：

```json
{
  "bindings": [
    {
      "context": "Global",
      "bindings": {
        "ctrl+d": null
      }
    }
  ]
}
```

### 示例 2：绑定斜杠命令

将 `Ctrl+Y` 绑定到 `/compact` 命令：

```json
{
  "bindings": [
    {
      "context": "Chat",
      "bindings": {
        "ctrl+y": "command:compact"
      }
    }
  ]
}
```

### 示例 3：重新映射快捷键

将 `Ctrl+E` 绑定到 `chat:killAgents`（自定义中断 agent 的快捷键）：

```json
{
  "bindings": [
    {
      "context": "Chat",
      "bindings": {
        "ctrl+e": "chat:killAgents"
      }
    }
  ]
}
```

---

## 验证与排错

### 常见问题

| 问题 | 原因 | 解决方案 |
|---|---|---|
| 修改后快捷键未生效 | JSON 格式错误 | 运行 `/keybindings validate` 检查 |
| 配置文件被忽略 | 缺少 `bindings` 数组 | 确保顶层有 `"bindings": [...]` |
| 绑定到保留键无效 | 保留键被硬编码 | 查看上方「保留快捷键」章节 |
| 和弦序列无响应 | 超过 1000ms 超时 | 连续按键时缩短间隔 |
| Shift+Enter 无效 | 终端不支持 kitty 协议 | 使用 `Alt+Enter` 作为换行备用 |

### 验证命令

```
/keybindings validate
```

输出示例：

```
✓ Valid keybindings file
  Binding blocks: 2
  Total bindings: 5
```

---

## 相关文件

| 文件 | 说明 |
|---|---|
| [KeybindingDefaults.cs](../src/OneCode.Core/Keybindings/KeybindingDefaults.cs) | 默认绑定定义（源头） |
| [KeybindingResolver.cs](../src/OneCode.Core/Keybindings/KeybindingResolver.cs) | 按键解析器 |
| [KeybindingLoader.cs](../src/OneCode.Infrastructure/Keybindings/KeybindingLoader.cs) | 配置加载与热重载 |
| [KeybindingSchema.cs](../src/OneCode.Core/Keybindings/KeybindingSchema.cs) | JSON Schema 生成 |
| [KeybindingParser.cs](../src/OneCode.Core/Keybindings/KeybindingParser.cs) | 按键字符串解析 |
| [KeybindingValidator.cs](../src/OneCode.Core/Keybindings/KeybindingValidator.cs) | 绑定验证 |
| [KeybindingsCommand.cs](../src/OneCode.App/Commands/KeybindingsCommand.cs) | `/keybindings` 命令实现 |
| [ReplShell.Keyboard.cs](../src/OneCode.App/Tui/ReplShell.Keyboard.cs) | TUI 键盘分发 |
| [ChatInputView.Keys.cs](../src/OneCode.App/Tui/ChatInputView.Keys.cs) | 输入框键盘处理 |
| [TuiKeyAdapter.cs](../src/OneCode.App/Tui/TuiKeyAdapter.cs) | Terminal.Gui 按键适配器 |
