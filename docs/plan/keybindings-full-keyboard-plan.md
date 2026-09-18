# OneCode 全键盘操作与快捷键体系优化重构计划

> 状态：计划稿（2026-09-18 基于当前代码实现重写，取代旧版计划稿）。
> 目标：实现 100% 不依赖鼠标的全键盘操作体验（Full Keyboard Accessibility）。
> 关联文档：`docs/keybindings.md`（现行快捷键参考手册）。
> 代码证据基线：`src/OneCode.Core/Keybindings/`、`src/OneCode.App/Tui/`（行号以 2026-09-18 工作区为准）。

---

## 1. 现状评估：已达成 vs 未达成

### 1.1 已达成（无需重复建设）

| 能力 | 实现位置 | 状态 |
|---|---|---|
| 单真相源契约（上下文/动作/默认绑定/保留键） | `KeybindingDefaults.cs` | ✅ 5 上下文、35 动作 |
| 上下文优先级 + 1000ms 和弦 + Eager-fire | `KeybindingResolver.cs` | ✅ |
| 用户覆盖 + 热重载 + Schema 校验 | `KeybindingLoader.cs` / `KeybindingSchema.cs` / `KeybindingValidator.cs` | ✅ |
| 侧边栏显隐快捷键 | `app:sidebarToggle`（Ctrl+G，Global） | ✅ 旧计划称"无需快捷键"已过时 |
| 侧边栏宽度键盘调整 | `app:sidebarWider/Narrower`（Ctrl+Shift+←/→） | ✅ 双路径分发（输入框聚焦经 `ChatInputView.Keys.cs`，非聚焦经 `ReplShell.Keyboard.cs` 兜底） |
| Autocomplete/Diff/Selector 上下文接入 Resolver | `KeybindingDefaults.DefaultBindings` | ✅ 旧计划"硬编码"描述已过时 |
| 对话区键盘滚动 | `chat:scrollUp/Down`（Shift+↑↓ / Ctrl+PgUp/PgDn）、`chat:pageUp/pageDown`（PgUp/PgDn / Ctrl+U） | ✅ 事件转发范式：`ReplShell.cs:106-111` |
| Esc 兜底链（Overlay → 补全 → 交互会话） | `ReplShell.Keyboard.cs` Level 1-3 | ✅ 关闭永远可用 |
| 交互会话统一接管（选择器/提问向导） | `IInteractionSession` / `ChatInputView.Keys.cs` 挂起态转发 | ✅ |
| 搜索内核 | `ChatTranscriptView.Search.cs`：`SearchAndScroll` / `FindNext` / `ClearSearchHighlight` | ✅ 缺键盘入口（见 §2） |

### 1.2 未达成（本计划要解决的盲区）

| # | 盲区 | 代码证据 | 严重度 |
|---|---|---|---|
| 1 | **侧边栏内容键盘滚动**：Plan/Team 面板内容超出屏幕时只能鼠标滚轮滚动 | `SidebarViewBase.cs`：`CanFocus = false`、无键盘处理；内部 `_content`（`MessageListView`）滚动 API 齐全但无外部触发路径 | 🔴 阻断 |
| 2 | **折叠块键盘展开/折叠**：思考过程/工具详情/错误详情仅鼠标点击切换 | `MessageListView.cs:476` 起：`ToggleToolExpansion` / `ToggleThinkingExpansion` / `ToggleErrorExpansion` 均为私有方法，仅 `OnMouseEvent` 的 `LeftButtonClicked` 分支调用 | 🔴 阻断 |
| 3 | **行内搜索无键盘入口**：仅 `/find <keyword>` 命令行入口，无 Ctrl+F 呼出、无跳上一处 | `ChatTranscriptView.Search.cs`：有 `FindNext` 无 `FindPrevious`；`FindCommand` 为唯一入口 | 🟡 体验 |
| 4 | **窗格焦点轮转缺失**：无法把焦点交给侧边栏做长文沉浸漫游 | `SidebarViewBase` 构造函数：`CanFocus = false; TabStop = TabBehavior.NoStop` | 🟡 体验 |
| 5 | **QuestionWizard 未接 Resolver**：原始 `Key.` 比较链 | `QuestionWizard.cs:59-171`：`kb == Key.Enter` 等硬编码 | 🟢 低（已有 `IInteractionSession` 接管 + Esc 兜底，功能不受损） |

### 1.3 维持现状的硬编码（有意保留，不迁移）

| 按键 | 行为 | 保留理由 |
|---|---|---|
| 裸 `Tab`（空输入/斜杠前缀/默认） | 占位建议采纳 / 打开命令补全 / 循环工作模式 | 单键复用三种运行时状态相关行为，无法映射为单一动作；`Alt+1..4` 已提供模式直达 |
| `Ctrl+Left/Right`（占位建议可见时） | 循环切换占位建议 | 同上，依赖 `_placeholderLabel.Visible` 运行时状态 |
| `Esc` 关闭 Overlay | `OverlayHost.HandleEsc()` | 关闭必须永远可用，不可重映射（安全兜底） |

---

## 2. 盲区根因分析

### 2.1 侧边栏滚动（盲区 #1）

`SidebarViewBase` 的设计意图是"纯数据投影、显示专用"（类头注释明确 `Display-only by design`），因此 `CanFocus = false` 且不处理键盘。但 Plan 步骤与 Team 任务卡内容长度不可控，"显示专用"不等于"只读到看不见"。

**根因**：缺一条"焦点不离开输入框也能滚动侧边栏"的事件管道，以及一条"把焦点交给侧边栏"的轮转管道。

### 2.2 折叠块交互（盲区 #2）

`MessageListView` 的三个 `Toggle*Expansion` 方法是纯逻辑（行索引 + Tag → 重渲），与鼠标无耦合，仅因触发入口只有 `OnMouseEvent` 而键盘不可达。

**根因**：缺"最近折叠块"定位逻辑与公开触发入口。

### 2.3 行内搜索（盲区 #3）

搜索内核完整（含正则），但入口是斜杠命令——用户必须打断输入流输入 `/find`，且 `FindNext` 只能单向前进。

**根因**：缺键盘呼出入口与 `FindPrevious`。

---

## 3. 键位规划

### 3.1 新增动作总表

| 动作 | 默认键 | 上下文 | 说明 |
|---|---|---|---|
| `sidebar:scrollUp` | `Alt+Up` | Chat | 侧边栏上滚 3 行（焦点不动） |
| `sidebar:scrollDown` | `Alt+Down` | Chat | 侧边栏下滚 3 行 |
| `sidebar:pageUp` | `Alt+PgUp` | Chat | 侧边栏上翻页 |
| `sidebar:pageDown` | `Alt+PgDn` | Chat | 侧边栏下翻页 |
| `app:cyclePane` | `F6` | Global | 输入框 ↔ 侧边栏焦点轮转 |
| `chat:toggleLastDetails` | `Ctrl+O` | Chat | 展开/折叠最近一条工具/思考/错误详情 |
| `chat:find` | `Ctrl+F` | Chat | 呼出行内搜索条 |
| `chat:findNext` | `Enter`（搜索条激活时） | Search | 跳下一处 |
| `chat:findPrevious` | `Shift+Enter`（搜索条激活时） | Search | 跳上一处 |
| `chat:findDismiss` | `Esc`（搜索条激活时） | Search | 关闭搜索条并清除高亮 |

> **键位冲突核查**：`Alt+Up/Down/PgUp/PgDn`、`F6`、`Ctrl+O`、`Ctrl+F` 均不在 `KeybindingDefaults.NonRebindable`（ctrl+d/ctrl+m）、`TerminalReserved`（ctrl+z/ctrl+\，仅 Unix）、`MacOSReserved`（cmd+*）列表中，与现有 35 个动作的默认绑定无冲突。`Ctrl+O`/`Ctrl+F` 在终端中为标准控制序列，无协议歧义。

### 3.2 侧边栏滚动：双路径方案

```
                ┌── 路径 A（免切焦点，高频微调）──> Alt+Up/Down / Alt+PgUp/PgDn ──> 侧边栏滚动
                │
键盘输入流 ─────┤
                │
                └── 路径 B（窗格轮转，长文沉浸）──> F6 (app:cyclePane) ──> 聚焦 Sidebar
                                                                              │
                                                                              └──> 原生 ↑↓/PgUp/PgDn/Home/End
```

**路径 A（直发热键，首期落地）**：
- 适用：输入框打字时瞥到右侧步骤更新，顺手翻两行，焦点不离开输入框。
- 分发：`ChatInputView.Keys.cs` 拦截 `sidebar:*` 动作 → 触发 `SidebarScrollRequested(direction, amount)` 事件 → `ReplShell` 路由到当前可见侧边栏（`PlanSidebarView` / `TeamSidebarView`）的 `MessageListView` 滚动 API。
- 兜底：焦点不在输入框时，`ReplShell.Keyboard.cs` 的 Resolver 兜底分支直接处理（与 `app:sidebarWider/Narrower` 双路径同构）。

**路径 B（窗格轮转，进阶）**：
- `F6` 在 `[ChatInput]` ↔ `[Sidebar]` 间轮转；侧边栏聚焦时标题栏反色高亮：`📋 计划 [活动窗格 · ↑↓/PgUp/PgDn 滚动 · Esc/F6 切回]`。
- 切回：侧边栏活动时按 `Esc`、再按 `F6`，或键入任何可打印字符 → 焦点还给输入框，可打印字符追加进输入框。
- 实现要点：`SidebarViewBase` 需支持临时 `CanFocus = true`（或引入独立的焦点代理视图包裹内容区），聚焦态由 `ReplShell` 维护，避免破坏"显示专用"的默认语义。

### 3.3 折叠块键盘交互

- `Ctrl+O`（`chat:toggleLastDetails`）：定位会话流中**最后一条**折叠行（工具/思考/错误三类 Tag 任一），切换其展开态；重复按压在"展开 → 折叠"间往复。
- 定位逻辑放 `MessageListView`：从尾部向前扫描 `_lines`，找到第一个带 `ToolLineTag` / `ThinkingLineTag` / `ErrorLineTag` 的行；若该行已展开则折叠，否则展开。展开后 `ScrollToLine` 保证可见。
- 三个 `Toggle*Expansion` 方法从 `private` 提升为 `internal`（或新增统一入口 `ToggleLastDetails()`），供事件路由调用。

### 3.4 行内搜索

- `Ctrl+F`（`chat:find`）：在输入框上方呼出单行搜索条（复用补全浮层的定位与渲染模式），输入即高亮全部匹配（复用 `SearchAndScroll`），`Enter` 跳下一处。
- `Search` 上下文激活时：`Enter` → `chat:findNext`、`Shift+Enter` → `chat:findPrevious`（需 `ChatTranscriptView.Search.cs` 新增 `FindPrevious`，实现为 `FindNext` 的反向扫描）、`Esc` → `chat:findDismiss`（清除高亮、焦点回输入框）。
- `/find` 命令保留：作为无键盘入口时的兼容路径，两者共用同一搜索内核。

---

## 4. 自定义边界（80/20 原则，维持旧计划结论）

**开放自定义**：本计划全部新增动作（`sidebar:*`、`app:cyclePane`、`chat:toggleLastDetails`、`chat:find*`）均注册进 `KeybindingDefaults.AllActions` 与 `AllActionDescriptions`，可经 `~/.onecode/keybindings.json` 重映射或 `null` 解绑。

**坚守不开放**：
- `Escape` 兜底取消（弹窗/选择器/补全/搜索条的关闭链）；
- 终端协议保留键（`Ctrl+C`/`Ctrl+D`/`Ctrl+M`，Unix 信号键）；
- 基础文本编辑原子键（字符、Backspace、Delete、光标移动）。

> 核心原则不变：**全键盘可达性 > 全键可配置性**。

---

## 5. 分阶段任务拆解

### 阶段一：侧边栏键盘滚动管道（消灭最大阻断）

| # | 任务 | 文件 | 验收 |
|---|---|---|---|
| 1.1 | 新增 4 个动作常量 + 描述 + 默认绑定（Chat 上下文） | `KeybindingDefaults.cs` | `AllActionDescriptions_CoversEveryAction` 测试通过 |
| 1.2 | `ChatInputView` 新增 `SidebarScrollRequested(SidebarScrollDirection, SidebarScrollAmount)` 事件与分发分支 | `ChatInputView.Keys.cs`、`ChatInputView.cs` | 单测：DispatchInputKey 模拟 Alt+Up 触发事件 |
| 1.3 | `SidebarViewBase` 新增公开滚动方法 `ScrollBy(int lines)` / `ScrollPage(int pages)`（内部转发 `_content`） | `SidebarViewBase.cs` | 单测：调用后 `ScrollOffset` 变化 |
| 1.4 | `ReplShell` 订阅事件，路由到可见侧边栏；无可见侧边栏时静默吞键 | `ReplShell.cs` | 单测：Plan 可见时 Alt+Down 生效，不可见时不抛异常 |
| 1.5 | `ReplShell.Keyboard.cs` 兜底分支处理 `sidebar:*`（焦点不在输入框时） | `ReplShell.Keyboard.cs` | 单测：非聚焦态 Alt+PgDn 生效 |
| 1.6 | Schema 自动包含新动作（`AllActions` 驱动，无额外工作，验证即可） | `KeybindingSchema.cs` | `/keybindings validate` 通过 |

### 阶段二：折叠块键盘交互

| # | 任务 | 文件 | 验收 |
|---|---|---|---|
| 2.1 | `MessageListView` 新增 `ToggleLastDetails()`：尾部向前扫描定位最近折叠行并切换 | `MessageListView.cs` | 单测：含工具/思考/错误折叠行的流，调用后 `IsExpanded` 翻转 |
| 2.2 | `ChatInputView` 新增 `ToggleDetailsRequested` 事件与 `chat:toggleLastDetails` 分发分支 | `ChatInputView.Keys.cs` | 单测：Ctrl+O 触发事件 |
| 2.3 | `ReplShell` 订阅并路由到 `_transcript.MessageView.ToggleLastDetails()` | `ReplShell.cs` | 集成：展开后行数变化、`ScrollToLine` 可见 |
| 2.4 | （可选）`QuestionWizard` 接入 Resolver：将 `Key.` 比较链迁至 `Selector` 上下文扩展绑定 | `QuestionWizard.cs`、`KeybindingDefaults.cs` | 现有交互行为回归测试全绿；**若迁移风险高则放弃，维持现状**（功能已可用，见 §1.2 #5） |

### 阶段三：行内搜索键盘入口

| # | 任务 | 文件 | 验收 |
|---|---|---|---|
| 3.1 | `ChatTranscriptView.Search.cs` 新增 `FindPrevious()`（反向扫描） | `ChatTranscriptView.Search.cs` | 单测：多匹配流，Next/Previous 往复一致 |
| 3.2 | 新增 `Search` 上下文与 3 个动作（`chat:findNext/Previous/Dismiss`），搜索条激活时 push/pop | `KeybindingDefaults.cs`、`KeybindingContextManager` 调用方 | 单测：上下文激活时 Enter 归 findNext，未激活归 chat:submit |
| 3.3 | 搜索条 UI：单行输入 + 匹配计数（`3/17`）+ 即时高亮 | 新文件 `ChatInputView.FindBar.cs`（partial） | 手动：Ctrl+F 呼出、输入即高亮、Esc 关闭 |
| 3.4 | `chat:find` 分发分支 + `ReplShell` 路由 | `ChatInputView.Keys.cs`、`ReplShell.cs` | 手动：全键盘完成一次搜索跳转 |

### 阶段四：窗格焦点轮转（进阶，可独立裁剪）

| # | 任务 | 文件 | 验收 |
|---|---|---|---|
| 4.1 | `app:cyclePane` 动作注册（Global，F6） | `KeybindingDefaults.cs` | validate 通过 |
| 4.2 | `SidebarViewBase` 支持聚焦态：临时 `CanFocus` 切换或焦点代理视图；标题栏反色提示 | `SidebarViewBase.cs` | 手动：F6 后标题栏出现 `[活动窗格]` 提示 |
| 4.3 | `ReplShell` 维护窗格状态机：F6 轮转、Esc/可打印字符切回（字符追加输入框） | `ReplShell.Keyboard.cs` | 手动：F6 → ↑↓ 漫游 → 打字直接回输入框 |
| 4.4 | 侧边栏聚焦时 `MessageListView.OnKeyDown` 原生滚动生效（已实现，验证即可） | `MessageListView.cs:446` | 手动：聚焦态 PgUp/PgDn/Home/End 全部可用 |

### 阶段五：文档与测试收尾

| # | 任务 | 文件 |
|---|---|---|
| 5.1 | `docs/keybindings.md` 同步：新动作补入上下文表格、`Search` 上下文说明、保留行为表更新 | `docs/keybindings.md` |
| 5.2 | 全量测试 + build 无新增警告 | `dotnet build src/OneCode.slnx` / `dotnet test src/OneCode.slnx` |
| 5.3 | `/keybindings list` 输出核对（新动作描述齐全） | 运行时验证 |

**依赖关系**：阶段一、二、三相互独立可并行；阶段四依赖阶段一（侧边栏滚动 API 已就位）；阶段五收尾。

---

## 6. 全键盘验证清单（验收标准）

逐条仅用键盘执行，任何一条失败即视为未达成"无鼠标完成所有动作"：

- [ ] **基础闭环**：输入消息 → `Enter` 提交 → `Esc` 中断运行 → `Ctrl+D` 退出
- [ ] **侧边栏微调**：PLAN 模式生成计划后，`Alt+Up/Down` 滚动 Plan 面板，焦点始终在输入框（继续打字不丢字符）
- [ ] **侧边栏翻页**：`Alt+PgUp/PgDn` 翻页滚动 Team 任务卡
- [ ] **窗格轮转**：`F6` 聚焦侧边栏 → `↑↓/PgUp/PgDn/Home/End` 漫游 → `Esc` 切回输入框
- [ ] **折叠块**：Agent 执行工具调用后，`Ctrl+O` 展开最近工具详情 → 再按折叠；思考过程、错误详情同理
- [ ] **行内搜索**：`Ctrl+F` 呼出搜索条 → 输入关键词即时高亮 → `Enter`/`Shift+Enter` 前后跳转 → `Esc` 关闭
- [ ] **Diff 审查**：`/diff` → `↑↓/J/K/PgUp/PgDn/Home/End` 浏览 → `Esc` 逐层关闭 overlay
- [ ] **审批选择器**：权限提示弹出后 `↑↓` 选择、`Enter` 确认、`Esc` 拒绝
- [ ] **配置链路**：`/keybindings validate` 通过；`/keybindings list` 列出全部新动作及中文描述
- [ ] **回归**：`dotnet build` 无新增警告；`dotnet test` 全部通过

---

## 7. 明确不做（及理由）

| 项 | 理由 |
|---|---|
| 文本选取与键盘复制模式 | Windows 终端 / tmux 原生鼠标选择 + `Ctrl+Shift+C` 已覆盖复制需求；TUI 内自建选取模式 ROI 低、与终端选择语义冲突。列为后续可选。 |
| 侧边栏显隐快捷键 | 已有 `Ctrl+G`（`app:sidebarToggle`），数据驱动展开语义不变。 |
| Tab / Ctrl+Left/Right 硬编码迁移 | 行为依赖运行时状态（多动作复用单键），见 §1.3。 |
| QuestionWizard 强行接入 Resolver | 已有 `IInteractionSession` 接管 + Esc 兜底链，功能不受损；迁移收益低于回归风险（阶段 2.4 标记可选）。 |
