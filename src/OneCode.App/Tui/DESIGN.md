---
name: OneCode TUI
description: Terminal.Gui v2 character-cell design system for OneCode.
colors:
  accent: "#14B8A6"
  accent-dim: "#0E8A80"
  success: "#22C55E"
  error: "#EF4444"
  warning: "#F59E0B"
  info: "#58A6FF"
  in-progress: "#14B8A6"
  streaming: "#8B949E"
  thinking: "#5BB8C8"
  fg-primary: "#E6EDF3"
  fg-secondary: "#8B949E"
  fg-muted: "#6B7280"
  bg-primary: "#0A0A0A"
  bg-surface: "#161B22"
  bg-input: "#0F1218"
  bg-terminal: "#0D1117"
  bg-terminal-header: "#161B22"
  bg-card: "#111111"
  bg-error: "#1A0A0A"
  bg-success: "#0A160A"
  border: "#21262D"
  border-accent: "#14B8A6"
  separator: "#21262D"
  user-message: "#58A6FF"
  assistant-message: "#E6EDF3"
  tool-use: "#F59E0B"
  tool-result: "#D29922"
  system-message: "#BC8CFF"
  diff-added: "#22C55E"
  diff-removed: "#EF4444"
  diff-hunk: "#14B8A6"
  diff-context: "#8B949E"
  mode-build: "#4CAF84"
  mode-plan: "#5B8DEE"
  mode-team: "#A386D8"
  mode-goal: "#5BB8C8"
  agent-orchestrator: "#A386D8"
  agent-researcher: "#5B8DEE"
  agent-planner: "#4CAF84"
  agent-executor: "#E08B5C"
  agent-reviewer: "#E5B14C"
  agent-tester: "#E0556A"
  agent-debugger: "#E07BA5"
  agent-assistant: "#5BB8C8"
  tool-detail: "#8B949E"
  thought-timing: "#D29922"
---

# OneCode TUI Design System

OneCode 是基于 Terminal.Gui v2 的字符单元格界面。本文是 TUI 的可移植设计规范：颜色、网格、间距、组件、层级和交互必须可直接映射到实现。运行时颜色的唯一来源是 `TuiPalette.DesignTokens`；本文颜色表由自动测试校验。

## Colors

颜色使用 TrueColor 语义值；终端能力不足时由 Terminal.Gui/宿主调色板降级。重要状态必须同时使用符号或文字表达，不能只依赖颜色。

### Core

| Token | Hex | 用途 |
|---|---|---|
| `accent` | `#14B8A6` | 焦点、快捷键、链接、活动边界 |
| `accent-dim` | `#0E8A80` | 弱强调、非活动焦点 |
| `success` | `#22C55E` | 成功、完成、允许 |
| `warning` | `#F59E0B` | 等待确认、执行中、警告 |
| `error` | `#EF4444` | 失败、拒绝、错误 |
| `info` | `#58A6FF` | 信息、连接状态、辅助提示 |
| `in-progress` | `#14B8A6` | 进行中状态 |
| `streaming` | `#8B949E` | 流式文本和次级状态 |
| `thinking` | `#5BB8C8` | 思考摘要和过程信息 |

### Text and Surfaces

| Token | Hex | 用途 |
|---|---|---|
| `fg-primary` | `#E6EDF3` | 正文和主要控件文字 |
| `fg-secondary` | `#8B949E` | 描述、次要信息 |
| `fg-muted` | `#6B7280` | 分隔线、弱提示 |
| `bg-primary` | `#0A0A0A` | 消息区和根画布 |
| `bg-surface` | `#161B22` | 固定状态栏和上下文栏 |
| `bg-input` | `#0F1218` | 输入区 |
| `bg-terminal` | `#0D1117` | 终端型内容区域 |
| `bg-terminal-header` | `#161B22` | 表单字段和终端头部 |
| `bg-card` | `#111111` | 卡片和模态浮层 |
| `bg-error` | `#1A0A0A` | 错误详情 |
| `bg-success` | `#0A160A` | 成功详情 |
| `border` | `#21262D` | 普通边界和分隔线 |
| `border-accent` | `#14B8A6` | 活动边界和顶层浮层 |
| `separator` | `#21262D` | 横向或纵向分隔线 |

### Roles and Modes

`user-message`、`assistant-message`、`tool-use`、`tool-result`、`system-message` 用于消息角色。`diff-added`、`diff-removed`、`diff-hunk`、`diff-context` 用于差异视图。`mode-build`、`mode-plan`、`mode-team`、`mode-goal` 用于工作模式。

TEAM 角色使用 `agent-orchestrator`、`agent-researcher`、`agent-planner`、`agent-executor`、`agent-reviewer`、`agent-tester`、`agent-debugger`、`agent-assistant`。角色色只用于识别，不替代状态符号或文字。

## Grid and Spacing

- 所有尺寸使用整数字符列和行。
- ASCII 字符按 1 列计算；CJK、emoji 和其他宽字符按实际显示宽度计算。
- 行高固定为 1 个字符行。
- 间距 token：`none=0`、`xs=1`、`sm=2`、`md=4`。
- 禁止在布局规范中使用像素、字体大小、阴影或圆角等连续屏幕概念。

## Layout

根布局从上到下由消息区、Agent 状态栏、聊天输入区和会话上下文栏组成；OverlayHost 覆盖在最上层。

- 消息区使用 `bg-primary`，弹性占据剩余空间。
- Agent 状态栏和会话上下文栏各占 1 行，使用 `bg-surface`。
- 输入区使用 `bg-input`，单行输入总高度为 2 行，多行输入最多 6 行。
- 输入区父级负责设置动态高度和底部位置；输入控件只报告内容所需行数并绘制自身。
- 侧栏默认宽度 35 列，最小宽度 28 列；主会话区目标最小宽度为 65 列。
- 当终端宽度不足以同时容纳主会话区、侧栏和 1 列分隔时，侧栏自动隐藏；宽度恢复后恢复原可见状态。

## Components

### Chat Input

- 顶部分隔线使用 `separator`；编辑器获得焦点时使用 `accent`。
- 编辑器背景始终使用 `bg-input`，空闲、聚焦、忙碌、只读和补全状态不得暴露默认背景。
- `Enter` 提交；`Shift+Enter` 换行；`Ctrl+V` 智能粘贴。
- 普通输入状态下 `Tab` 循环工作模式；补全激活时 `Tab` 接受补全；空输入有建议时 `Tab` 接受建议。
- `Escape` 按层级处理：关闭顶层浮层、关闭补全、取消交互、取消任务、清空输入，空闲空输入时无动作。

### Conversation

- `MessageListView` 不持有键盘焦点，避免与输入区争夺焦点。
- `PageUp/PageDown` 按当前视口高度翻页。
- `Shift+Up/Down` 和 `Ctrl+PageUp/PageDown` 小步滚动。
- 用户离开底部跟随模式时显示新内容提示，回到底部后隐藏提示。
- 可展开内容使用 `▸`/`▾`，错误、成功、进行中状态使用符号和文字辅助颜色。

### Status Bars

- 状态栏左侧优先显示活动状态和模型，右侧显示当前工作模式。
- 上下文栏左侧显示工作区和分支，右侧显示轮次和上下文使用情况。
- 空间不足时按优先级隐藏次要信息；错误、警告和取消状态必须保留可识别摘要。

### Sidebars and Overlays

- Plan 与 Team 侧栏互斥，右侧停靠；分隔线支持鼠标拖动和键盘宽度调整。
- 侧栏使用左侧竖向分隔线；拖动或悬停时使用 `accent`。
- Overlay 使用 `bg-card` 和方角边界；顶层 Overlay 使用 `border-accent`，非顶层使用 `border`。
- Overlay 打开后焦点必须落在可交互叶子控件上，不能停在容器或标题上。
- 所有鼠标操作必须有等效键盘操作。

## Interaction

交互必须保持单焦点模型。全局快捷键在输入区获得焦点时由输入区转发，在其他焦点状态由根 Shell 兜底；同一个动作只能有一个权威执行路径。

快捷键的默认绑定、上下文和显示名称以 `KeybindingDefaults` 为准；文档只描述稳定语义，不复制完整绑定表。

## Guidelines

- 会话流优先于装饰和次要状态。
- 语义颜色必须来自 `TuiPalette`；控件 `Scheme` 通过 `TuiStyles` 组合。
- 状态不能只通过颜色表达，必须提供符号、文字或结构提示。
- 所有文本绘制必须按显示宽度裁剪和换行，不能直接使用字符串长度推断 CJK/emoji 宽度。
- 渲染器必须主动填充其负责的背景区域，不能依赖父控件默认背景。
- 设计文档只保留稳定设计规则；实现状态、历史决策和未来路线放在其他文档中。
