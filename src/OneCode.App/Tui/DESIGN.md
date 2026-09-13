---
name: OneCode Dark
description: >
  Dark-first terminal UI design system for a multi-mode AI coding assistant.
  Constrained to the ANSI 16-color palette of Terminal.Gui v2 and monospace
  character cells (Columns × Rows).
colors:
  # ── Primary ────────────────────────────────────
  primary: "#5b8dee"
  # ── Backgrounds ──────────────────────────────────
  bg-root: "#12151a"
  bg-surface: "#181b22"
  bg-elevated: "#1e212a"
  bg-input: "#0f1218"
  # ── Brand & Semantic ─────────────────────────────
  accent: "#5b8dee"
  accent-teal: "#14B8A6"
  success: "#4caf84"
  warning: "#e5b14c"
  error: "#e0556a"
  info: "#58A6FF"
  # ── Text ─────────────────────────────────────────
  text-primary: "#d4d8e0"
  text-secondary: "#9298a4"
  text-muted: "#5c6270"
  # ── Borders ──────────────────────────────────────
  border: "#2a2e3a"
  border-active: "#3d5080"
  # ── Diff ─────────────────────────────────────────
  diff-add: "#4caf84"
  diff-del: "#e0556a"
  diff-hunk: "#5bb8c8"
  diff-context: "#9298a4"
  # ── Mode identifiers ─────────────────────────────
  mode-build: "#4caf84"
  mode-plan: "#5b8dee"
  mode-team: "#a386d8"
  mode-goal: "#5bb8c8"
  # ── Agent 8-color system ─────────────────────────
  agent-orchestrator: "#a386d8"
  agent-researcher: "#5b8dee"
  agent-planner: "#4caf84"
  agent-executor: "#e08b5c"
  agent-reviewer: "#e5b14c"
  agent-tester: "#e0556a"
  agent-debugger: "#e07ba5"
  agent-assistant: "#5bb8c8"
  # ── Message role ─────────────────────────────────
  user-message: "#5bb8c8"
  assistant-message: "#d4d8e0"
  tool-use: "#e5b14c"
  tool-result: "#c4a03c"
  system-message: "#a386d8"
grid:
  unit: cell
  charWidthAscii: 1
  charWidthWide: 2
  rowHeight: 1
breakpoints:
  compact:
    width: "< 100"
    layout: single-column
    sidebar: squeezed-28col  # Drawer Overlay 未实现；当前仍为并排挤压，上限 28 列
    chatMinWidth: 65
  standard:
    width: "100 - 140"
    layout: elastic-sidebar
    sidebarRatio: "0.30"
    sidebarWidth: "28 - 42"
    chatMinWidth: 65
  wide:
    width: "> 140"
    layout: dual-column
    sidebarWidth: "40 - 45"  # 实现为上限 45 列（Min(45, width-65)）
    chatMinWidth: 95
spacing:
  none: 0
  xs: 1
  sm: 1
  md: 2
  lg: 3
components:
  status-bar:
    backgroundColor: "{colors.bg-surface}"
    textColor: "{colors.text-secondary}"
    paddingCols: 1
    heightRows: 1
  session-context-bar:
    backgroundColor: "{colors.bg-surface}"
    textColor: "{colors.text-secondary}"
    paddingCols: 1
    heightRows: 1
  border-default:
    backgroundColor: "{colors.border}"
  border-focus:
    backgroundColor: "{colors.border-active}"
  input-bar:
    backgroundColor: "{colors.bg-input}"
    textColor: "{colors.text-primary}"
    minHeightRows: 2
    maxHeightRows: 6
  input-mode-tag-build:
    backgroundColor: "{colors.mode-build}"
    textColor: "{colors.bg-root}"
  input-mode-tag-plan:
    backgroundColor: "{colors.mode-plan}"
    textColor: "{colors.bg-root}"
  input-mode-tag-team:
    backgroundColor: "{colors.mode-team}"
    textColor: "{colors.bg-root}"
  input-mode-tag-goal:
    backgroundColor: "{colors.mode-goal}"
    textColor: "{colors.bg-root}"
  message-user:
    textColor: "{colors.user-message}"
  message-assistant:
    textColor: "{colors.assistant-message}"
  message-tool-call:
    textColor: "{colors.tool-use}"
  message-tool-result:
    textColor: "{colors.tool-result}"
  message-system:
    textColor: "{colors.system-message}"
  status-info:
    textColor: "{colors.info}"
  status-warning:
    textColor: "{colors.warning}"
  status-success:
    textColor: "{colors.success}"
  thinking-block:
    textColor: "{colors.accent}"
    backgroundColor: "{colors.bg-elevated}"
  diff-added:
    textColor: "{colors.diff-add}"
    backgroundColor: "#0a160a"
  diff-removed:
    textColor: "{colors.diff-del}"
    backgroundColor: "#1a0a0a"
  diff-hunk-header:
    textColor: "{colors.diff-hunk}"
  diff-context-line:
    textColor: "{colors.diff-context}"
  plan-card:
    backgroundColor: "{colors.bg-elevated}"
    textColor: "{colors.text-primary}"
  overlay-popup:
    backgroundColor: "{colors.bg-elevated}"
    textColor: "{colors.text-primary}"
  command-palette:
    backgroundColor: "{colors.bg-elevated}"
    textColor: "{colors.text-primary}"
  muted-label:
    textColor: "{colors.text-muted}"
  agent-avatar-orchestrator:
    textColor: "{colors.agent-orchestrator}"
  agent-avatar-researcher:
    textColor: "{colors.agent-researcher}"
  agent-avatar-planner:
    textColor: "{colors.agent-planner}"
  agent-avatar-executor:
    textColor: "{colors.agent-executor}"
  agent-avatar-reviewer:
    textColor: "{colors.agent-reviewer}"
  agent-avatar-tester:
    textColor: "{colors.agent-tester}"
  agent-avatar-debugger:
    textColor: "{colors.agent-debugger}"
  agent-avatar-assistant:
    textColor: "{colors.agent-assistant}"
  accent-mark:
    textColor: "{colors.accent-teal}"
---

# OneCode TUI 设计系统规范 (Design System Specification)

## 1. 核心架构与设计哲学 (Overview & Philosophy)

**极客深色终端美学 (Developer-Dark Terminal Aesthetic)**。
本设计规范是 OneCode CLI 全屏 TUI（基于 Terminal.Gui v2）的单一真相源（Single Source of Truth）。

### 1.1 彻底抛弃 Web 假定，回归字符单元格 (Cell-Based Model)
终端不是浏览器。终端屏幕由离散的、等宽的字符单元格网格（Grid of Monospace Cells）构成：
- **水平坐标 (Columns / Width)**：以字符列（Col）为度量。普通 ASCII 字符占 1 列，CJK 全角字符、象形符号、部分 Emoji 占用 2 列。
- **垂直坐标 (Rows / Height)**：以字符行（Row）为度量，无子像素，无连续 line-height。
- **严禁概念**：代码与规范中彻底清除 `px`、`rem`、`line-height`、`font-size`、`box-shadow`、`border-radius` 等 Web CSS 伪属性。所有内边距、间隙、高度计算一律基于整数单元格。

### 1.2 会话流主权与弹性侧栏共存
- **会话流优先**：中央会话区（`MessageListView`）是用户生产力的核心空间。
- **侧栏响应式自适应**：Plan / Team / Review 侧边栏依据终端宽度弹性伸缩。**当前实现**：小屏（<100 列）下侧栏仍为并排挤压（上限 28 列），抽屉浮层（Drawer Overlay）尚未实现；`Ctrl+G` 仅切换可见性，不改变挤压布局。

---

## 2. 三档响应式断点体系 (Three-Tier Responsive Breakpoints)

为彻底解决窄屏字符折叠、侧栏与主会话算术冲突（如 28 列侧栏下限与 65 列主区保底），确立以下三档严格断点：

```text
┌─────────────────────────┬───────────────────────────────┬─────────────────────────┐
│   Compact (< 100 列)    │      Standard (100~140 列)    │     Wide (> 140 列)     │
├─────────────────────────┼───────────────────────────────┼─────────────────────────┤
│ • 纯单栏 + 并排挤压      │ • 弹性侧边栏 (占比 30%)       │ • 并列双栏模式          │
│ • 侧栏上限 28 列         │ • 侧栏宽度: 28 ~ 42 列        │ • 侧栏上限: 45 列        │
│ • Ctrl+G 切换可见性      │ • 主区保底: ChatMinWidth ≥ 65 │ • 主区宽度: ≥ 95 列     │
└─────────────────────────┴───────────────────────────────┴─────────────────────────┘
```

### 2.1 断点数学严密约束
- **下界保底公式**：
  $$\text{ScreenWidth} \ge \text{ChatColumnMinWidth} (65) + \text{SidebarMinWidth} (28) + \text{Divider} (1) = 94 \text{ 列}$$
  在 `Standard` 模式起点（100 列）时，主会话区拥有 $100 - 28 - 1 = 71 \ge 65$ 列，留有 6 列充足裕量，绝不发生断点越界溢出。
- **收缩降级保护**：
  若用户通过拖拽手柄或快捷键强行调整，侧栏宽度恒受 `Math.Clamp(requested, 28, maxAllowed)` 保护，其中 `maxAllowed = Math.Min((int)(width * 0.30), width - 65 - 1)`。

---

## 3. 终端物理深度与层级规范 (Physical Elevation & Depth)

终端色彩映射高度依赖宿主终端调色板（从 16 色 ANSI 到 256 色/TrueColor 各异），依靠细微的灰阶背景色差传递层级不可靠。OneCode 确立以**字符边界（Borders）**与**焦点状态（Focus）**为核心的物理深度体系：

| 层级 (Elevation) | 界面角色 | 字符表现 (Boundary) | 视觉语义 |
|---|---|---|---|
| **Level 0（底座画布）** | 消息会话主区 (`MessageListView`) | 无边框，使用终端默认根背景 (`bg-root`) | 最深基底，全量呈现代码流与对话 |
| **Level 1（分割行/状态）** | `AgentStatusBar`、`SessionContextBar` | 单横线 `─` 物理硬隔离，`FgSecondary` 静音色 | 固定锚点，呈现运行期状态 |
| **Level 2（停靠面板）** | 侧边栏 (`SidebarViewBase`) | 左侧单竖线 `│` 分隔；拖拽手柄激活时为双竖线 `║` | 与主区分栏并列的内容区 |
| **Level 3（模态浮层）** | Overlays、设置、选择器、表单 | 全包围单线方角 `┌─┐ │ └─┘`；活动顶层使用重点色高亮边框 (`border-active`) | 模态挂起，Esc 逐级安全退出 |

---

## 4. 界面布局拓扑与垂直空间压缩 (Layout & Space Optimization)

```text
ReplShell (Window Root)
├── MessageListView (Chat Transcript - 弹性占据全部剩余视口)
├── AgentStatusBar (1 Row: 运行期状态 · 模型 · 沙箱 · LSP · ModeTag)
├── ChatInputView (动态 2~6 Rows: 边框线 + 自适应编辑区)
├── SessionContextBar (1 Row: 路径 · 分支 · 轮次 · 消耗指标)
└── OverlayHost (Z-Top 模态浮层宿主)
```

### 4.1 垂直空间瘦身（底部 9 行 $\rightarrow$ 4~8 行）
- **消除冗余空行**：`StatusBarTopGap = 0`，`ChatInputContextGap = 0`。
- **输入框视口动态弹性伸展**：
  - 单行输入时仅占 2 行（1 行顶部分隔线 + 1 行输入文本）；
  - 多行输入时按终端高度自适应拓展：$$\text{MaxHeight} = \text{Math.Clamp}(\text{Height} / 6, 2, 6)$$
  - 24 行终端下限制在 4 行内（1 分隔线 + 3 编辑行），40+ 行高终端可展开至 6 行。
- **视口抖动防御**：
  - 引入**行数防抖检测**，仅在物理换行增加或减少时触发重绘；
  - `MessageListView` 采用 `Pos.AnchorEnd(reservedBottom)` 底部相对锚定，流式输出与输入高度变化交错时不产生全屏重排闪烁。

---

## 5. 纯键盘交互体系 (Keyboard Interaction)

终端操作的核心生命力在于纯键盘高效闭环。按键分发按**输入态（焦点在 ChatInputView）**与**非输入态（焦点游离）**两级路由；对话区滚动由不切焦点的转发键（Shift+Up/Down、Ctrl+PgUp/PgDn、Ctrl+U）承担。对话区漫游浏览模式已评估并剔除（见 §5.3）。

### 5.1 Esc 六级绝对拦截链 (Esc Hierarchy)
`Escape` 键是全系统最敏感的撤销/中断键，必须遵循严格的优先级链，严防关闭补全菜单时误杀后台正在执行的 Agent 任务：

```text
[按下 Escape]
   │
   ├─► 1. 存在活动 Overlay 弹层？ ────────────► 仅关闭顶层 Overlay
   │
   ├─► 2. 存在 Autocomplete 补全菜单？ ───────► 仅关闭补全菜单
   │
   ├─► 3. 存在 InlineSelector 决策面板？ ──────► 取消选择器决策
   │
   ├─► 4. AgentRunner 正在执行流式任务？ ──────► 发送取消信号 (Cancel Task)
   │
   ├─► 5. ChatInputView 包含非空文本？ ───────► 清空当前输入行
   │
   └─► 6. 空闲且输入框为空 ──────────────────► 无动作 (No-op)
```

### 5.2 核心按键行为映射表

| 上下文 (Context) | 按键 (Key) | 动作 (Action) | 说明 |
|---|---|---|---|
| **Global** | `Ctrl+G` | `app:sidebarToggle` | 一键展开/收起右侧 Plan/Team 侧边栏 |
| | `Ctrl+Shift+→ / ←` | `app:sidebarWider/Narrower` | 键盘微调侧边栏宽度（步进 4 列） |
| | `Ctrl+D` | `app:exit` | **保留退出键**，任何上下文不得遮蔽 |
| **Input (打字)** | `Enter` | `chat:submit` | 提交当前消息（自动无损展开 PUA 折叠块） |
| | `Shift+Enter` | `chat:newline` | 插入换行符 |
| | `Ctrl+V` | `chat:paste` | 长文本 Unicode PUA 折叠安全粘贴 |
| | `Escape` | `chat:cancel` | 沿六级拦截链分发 |
| | `Ctrl+U` | `chat:pageUp` | 跨终端兼容的对话区翻页（Ctrl+D 已让位给保留退出键） |

### 5.3 已剔除：对话区漫游浏览模式 (Browse Mode)
历史上曾引入过 `Ctrl+T` 进入的 Transcript 导航模式（`transcript:*` 动作族），后判定 obsolete 删除（commit c731b49）。本次重构再次评估后确认：滚动需求已被不切焦点的转发键完整覆盖，对话区漫游的增量价值不足，且 `MessageListView.CanFocus=true` 会动摇全局单焦点模型的基石。**本规范不设立 Browse 上下文与 browse:* 动作族；`MessageListView.CanFocus` 恒为 false。**

---

## 6. 核心色彩体系与调色板规范 (Color System)

系统映射到 ANSI 16 色调色板。OKLCH 真实色彩定义设计愿景，终端运行时映射到语义常量：

### 6.1 语义色彩槽位 (Semantic Roles)
- **`TuiPalette.Accent` (`#14B8A6` / BrightCyan)**：交互主键。用于聚焦边框、激活状态、快捷提示、链接文字。绝不用于大面积装饰。
- **`TuiPalette.Success` (`#4caf84` / BrightGreen)**：BUILD 模式标识、成功状态、Diff 新增行。
- **`TuiPalette.Warning` (`#e5b14c` / BrightYellow)**：待审批提醒、Tool 执行中微光、Diff 警告。
- **`TuiPalette.Error` (`#e0556a` / BrightRed)**：执行失败、拒绝状态、Diff 删除行。
- **`TuiPalette.Info` (`#58A6FF` / BrightBlue)**：MCP 与沙箱运行标记、辅助提示。

### 6.2 Agent 8 色协同矩阵 (TEAM Mode)
| 角色 (Role) | 颜色 (Color) | Hex | 语义职责 |
|---|---|---|---|
| orchestrator | Purple | `#a386d8` | 架构协调、全局分发 |
| researcher | Blue | `#5b8dee` | 代码调研、模式探索 |
| planner | Green | `#4caf84` | 任务拆解、步骤规划 |
| executor | Orange | `#e08b5c` | 代码落地、文件修改 |
| reviewer | Yellow | `#e5b14c` | 代码复核、质量门禁 |
| tester | Red | `#e0556a` | 单元测试、断言验证 |
| debugger | Pink | `#e07ba5` | 故障排查、根因定位 |
| assistant | Cyan | `#5bb8c8` | 通用答疑、状态协助 |

---

## 7. 规范执行与编码准则 (Do's and Don'ts)

### 7.1 必须遵守 (Do's)
1. **度量全部使用整数字符单元格**：在 `Layout` 与 `Drawing` 代码中，所有边界与偏移一律使用整型列数与行数。
2. **状态栏左右防对撞**：先测量右侧内容宽度，左侧绘制文本严格遵守 `maxLeftCol = width - rightWidth - 2` 截断守卫。
3. **安全粘贴 PUA 标记**：附件占位符必须用 PUA 定界符包裹（文本折叠 `\uE001[Pasted text #N +K lines]\uE002`、图片 `\uE001[Image #N]\uE002`），严禁使用用户可手打的裸方括号字面量；两者共用定界符，因此统一获得原子删除与光标吸附语义。
4. **Plan 审批修改保留**：用户选择 `edit` 输入意见时，严禁清空侧栏计划，保持全景对照。
5. **全键盘无障碍覆盖**：任何鼠标可点击操作（展开、折叠、复制、切换）必须提供对应的键盘动作注册。

### 7.2 严格禁止 (Don'ts)
1. **禁止在代码或注释中硬编码 Web 概念**：禁止出现 `px`、`line-height`、`font-size` 等误导性词汇。
2. **禁止破坏 ANSI 16 色调色板常量**：组件代码禁止随意使用未在 `TuiPalette` 登记的裸字面量颜色。
3. **禁止让输入框高度超过屏幕约 1/6**：输入框弹性上限严格控制在 `Math.Clamp(Height / 6, 2, 6)`，保障会话区视口高度。
4. **禁止单按 Esc 直接杀后台任务**：严格执行六级拦截链，保障操作安全性。
