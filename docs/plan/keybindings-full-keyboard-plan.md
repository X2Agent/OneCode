# OneCode 全键盘操作与快捷键体系演化计划

> 状态：计划稿。
> 目标：实现 100% 不依赖鼠标的全键盘操作体验（Full Keyboard Accessibility），重点解决左右侧边栏独立滚动、长内容键盘漫游、交互块展开折叠，并明确快捷键自定义的安全与架构边界。
> 关联文档：`docs/keybindings.md`（现行快捷键参考手册）。

---

## 1. 当前快捷键功能设计与实现评估

### 1.1 架构优势与设计亮点
OneCode 的快捷键体系位于 `src/OneCode.Core/Keybindings/` 与 `src/OneCode.App/Tui/`，整体具备清晰的模块化分层：

- **单真相源契约（Core 层）**：
  - `KeybindingDefaults.cs`：集中定义标准动作常量（`Action*`）、上下文常量（`Context*`）、默认键位映射与平台保留键列表。
  - `KeybindingResolver.cs`：纯逻辑按键匹配引擎，支持上下文优先级、1000ms 和弦按键序列（Chords）以及 Eager-fire（单键既是绑定又是前缀时立即触发自身）。
- **用户增量覆盖与热重载（Infrastructure 层）**：
  - `KeybindingLoader.cs`：从 `~/.onecode/keybindings.json` 异步加载，支持 `FileSystemWatcher` 500ms 去抖动热重载，无需重启应用。
  - `KeybindingValidator.cs`：主动校验按键冲突、保留键占用和语法合法性。
  - 支持 `command:<slash-command>` 前缀，可将任意斜杠命令直接绑定为热键。
- **UI 抽象与协议解耦（App 层）**：
  - `TuiKeyAdapter.cs`：桥接 Terminal.Gui 的 `Key` 与 Core 层的 `IKeyInput`，剥离修饰键以获取纯净键名，屏蔽跨平台终端编码差异。

### 1.2 现存架构瓶颈与设计不足
- **单焦点模型限制导致“键盘盲区”**：
  - 核心会话区 `MessageListView.cs` 和侧边栏 `SidebarViewBase.cs` 的 `CanFocus` 均为 `false`，界面焦点常驻在 `ChatInputView` 的编辑框内。
  - 主会话区的滚动依赖 `ChatInputView.Keys.cs` 拦截特定按键（如 `Shift+Up/Down`、`PageUp/PageDown`）后通过 C# 事件转发给 `MessageListView`。
  - 右侧侧边栏（Plan / Team 面板）没有暴露任何滚动事件钩子，导致**右侧内容超出时只能靠鼠标滚轮滚动**。
- **部分组件绕开 Resolver 存在硬编码**：
  - `Tab` 键行为（空输入接受建议、斜杠补全、循环模式切换）全部硬编码在 `ChatInputView.Keys.cs`。
  - `QuestionWizard.cs` 多问题向导直接使用原始 `switch (kb)`，未接入 `ContextSelector` 动态绑定。
  - 模态弹窗的 `Ctrl+S` 保存与 `Esc` 退出大多内联在各视图的 `OnKeyDown` 中。

---

## 2. 目前按键覆盖场景与盲区全景

### 2.1 当前已覆盖的场景
| 场景分类 | 按键 | 对应动作 | 行为说明 |
|---|---|---|---|
| **对话输入** | `Enter` | `chat:submit` | 提交消息 |
| | `Shift+Enter` / `Alt+Enter` | `chat:newline` | 多行换行 |
| | `Ctrl+V` | `chat:paste` | 智能粘贴（支持图片识别、文件路径与长文本折叠） |
| | `Escape` | `chat:cancel` | 运行时代中中断 Agent；空闲时关闭补全 |
| | `Ctrl+D` | `app:exit` | 退出应用 |
| **历史漫游** | `Up` / `Down` | `history:previous/next` | 翻阅上一条 / 下一条历史输入 |
| | `Ctrl+Up` | `history:recallLast` | 召回上一条消息供重新编辑 |
| **补全候选** | `Up` / `Down` | `autocomplete:previous/next` | 切换补全建议候选 |
| | `Tab` | `autocomplete:accept` | 采纳补全项 |
| | `Escape` | `autocomplete:dismiss` | 关闭补全浮层 |
| **工作流调度** | `Alt+1` .. `Alt+4` | `app:mode*` | 一键直达 BUILD / PLAN / TEAM / GOAL 模式 |
| | `Tab` (空输入) | *(硬编码)* | 循环切换工作模式 |
| | `Shift+Tab` | `chat:cycleTeam` | TEAM 模式下循环切换团队配置 |
| | `Ctrl+Left` / `Ctrl+Right` | *(硬编码)* | 切换占位建议短语 |
| **对话流滚动** | `Shift+Up/Down` / `Ctrl+PgUp/PgDn` | `chat:scrollUp/Down` | 会话区 3 行微调滚动 |
| | `PageUp` / `PageDown` | `chat:pageUp/pageDown` | 会话区整页滚动 |
| **面板几何** | `Ctrl+Shift+Left/Right` | `app:sidebarNarrower/Wider` | 步进 4 列调节侧边栏宽度 |
| **Diff 审查** | `Up/Down` / `J/K` / `PgUp/PgDn` / `Home/End` | `diff:*` | 代码差异详情查看与上下滚动 |
| **内联审批** | `Up/Down` / `Enter` / `Escape` | `selector:*` | 权限提示与计划审批单选确认 |

### 2.2 核心盲区（鼠标强依赖痛点）
1. **侧边栏内容滚动（严重阻断）**：
   - 当 Plan 计划步骤很多，或 Team 任务卡与质量门条目超出屏幕高度时，`SidebarViewBase` 仅在 `OnMouseEvent` 里响应滚轮，**键盘完全无法滚动右侧内容**。
2. **工具调用与思考过程展开/折叠**：
   - 会话流中的 `[▶ 思考过程]`、`[▶ 工具调用详情]` 折叠行仅监听鼠标 `LeftButtonClicked`，键盘用户无法聚焦并查看入参详情或报错栈。
3. **文本选取与流式复制**：
   - 无法通过键盘进入选择模式以选中并复制会话历史中的代码片段。
4. **即时行内搜索交互**：
   - 依赖 `/find <keyword>` 命令行输入，缺少类似 `Ctrl+F` 呼出即时高亮条、按 `Enter/N` 无缝跳到下一处的高效键盘体验。

---

## 3. 专项场景决策：侧边栏显隐与左右双区滚动

### 3.1 侧边栏一键显隐：结论为“保持纯数据驱动，无需快捷键”
针对“是否有必要支持快捷键一键折叠/展开侧边栏以释放屏幕宽度”的分析：
- **数据状态的只读投影**：
  - Plan 侧边栏和 Team 侧边栏属于当前运行事实的响应式投影。进入 PLAN 模式或生成 Plan 时自动展开，清空 Plan 或新会话时自动收起；触发 Team 时自动展示进度看板。
- **避免状态冲突与决策阻断**：
  - 若允许手动关闭，当 Agent 推进到第 3 步计划或 Team 触发审批卡点时，系统若强行弹出则违背用户折叠意图，若不弹出则导致用户漏看关键阻塞。保持数据驱动能消除该二义性。
- **已有宽度弹性调节兜底**：
  - 系统已支持 `Ctrl+Shift+Left`（收窄面板至极限 32 列），极窄终端下用户随手微调即可释放对话流视野，无需额外的显隐状态机。

### 3.2 左右两边滚动条的独立键盘滚动方案
左右两边同时存在滚动条时，采用**“快捷直发微调 + 窗格焦点轮转”混合方案**：

```
                ┌── 路径 A（免切焦点，高频微调）──> Alt+Up/Down / Alt+PgUp/PgDn ──> 侧边栏滚动
                │
键盘输入流 ─────┤
                │
                └── 路径 B（窗格轮转，长文沉浸）──> F6 / Ctrl+W (Cycle Pane) ─────> 聚焦 Sidebar
                                                                                       │
                                                                                       └──> 原生方向键 / PgUp / PgDn
```

#### 方案 A：直发修饰键方案（Direct Hotkeys - 推荐首期落地）
- **适用场景**：用户在编辑框打字或查看 Agent 回答时，眼角瞥到右侧步骤更新，需要顺手往下翻两行，不希望焦点离开输入框。
- **键位规划**：
  - `Alt+Up` / `Alt+Down`：右侧侧边栏微调滚动（3 行）
  - `Alt+PageUp` / `Alt+PageDown`：右侧侧边栏翻页滚动
- **分发管道**：
  - `KeybindingDefaults` 增加：
    - `ActionSidebarScrollUp = "sidebar:scrollUp"`
    - `ActionSidebarScrollDown = "sidebar:scrollDown"`
    - `ActionSidebarPageUp = "sidebar:pageUp"`
    - `ActionSidebarPageDown = "sidebar:pageDown"`
  - `ChatInputView.Keys.cs` 捕获后发出 `SidebarScrollRequested` 事件，由 `ReplShell` 路由到当前可见的 `SidebarViewBase.Content.ScrollUp/Down()`。

#### 方案 B：窗格焦点轮转方案（Pane Focus Switching - 进阶长文阅读）
- **适用场景**：生成的 Plan 篇幅极长，需要连续翻看数十步。
- **交互设计**：
  - 全局动作 `ActionAppCyclePane = "app:cyclePane"`，绑定 `F6`（或 `Ctrl+W`）。
  - 按 `F6` 在 `[ChatInput (主输入框)]` ↔ `[Sidebar (侧边栏)]` 之间轮转。
  - **聚焦提示**：侧边栏标题栏渲染反色高亮提示：`📋 计划 [活动窗格 · ↑↓/PgUp/PgDn 滚动 · Esc/F6 切回]`。
  - **切回机制**：在侧边栏处于活动窗格时，按 `Esc`、再次按 `F6`，或直接键入任何可打印字符，自动将焦点还给 `ChatInputView` 并将字符追加进输入框。

---

## 4. 快捷键自定义的合理边界与权衡思考

### 4.1 为什么“支持所有按键自定义”是不可取的？
1. **系统安全与防死锁（Safe Exit Guarantee）**：
   - `Escape` 作为弹窗退出、选择器取消、补全撤销的最后一道保险，必须始终可靠。如果允许用户随意修改或解绑 `Escape`，一旦发生异常状态，用户将被困在某个 View 中无法退出。
2. **终端协议与宿主环境硬性限制**：
   - 终端传输层保留键：`Ctrl+C` (SIGINT)、`Ctrl+D` (EOF)、`Ctrl+Z` (SIGTSTP)、`Ctrl+M` (回车CR)。不同操作系统（Windows vs macOS vs Linux）对这些键的解释深度不同。强行暴露会导致不可预期的终端崩溃或信号丢失。
3. **文本编辑基石破坏**：
   - 纯字符键、`Backspace`、`Delete`、光标移动键属于多行编辑器的基础语义。若全部放入配置文件，用户极易因语法模糊或正则误配导致无法正常打字。

### 4.2 80/20 自定义边界划分标准
- **开放 80% 的业务与漫游按键**：
  - 工作流与团队切换（`Alt+1..4`, `Shift+Tab`）；
  - 会话区与侧边栏的滚动、翻页、跳顶底；
  - 智能粘贴、换行、重新召回历史；
  - 窗格切换（`F6`）；
  - 宏指令与斜杠命令绑定（`command:<name>`）。
- **坚守 20% 的底层安全机制**：
  - 兜底取消键（`Escape`）；
  - 终端关键控制（`Ctrl+C`, `Ctrl+D`）；
  - 基础文本输入原子键。

> **核心原则**：**全键盘可达性（Accessibility） > 全键可配置性（Customizability）**。  
> 用户的根本诉求是“扔掉鼠标可以流畅完成所有事情”，而非为了“可配置”而将简单的逻辑复杂化。提供一套符合直觉的优质默认按键体验，比提供 100 个复杂的配置项更重要。

---

## 5. 落地改造规划与任务拆解

### 阶段一：侧边栏键盘滚动管道（彻底消灭鼠标强依赖）
1. **Core 契约扩展**：
   - 在 `KeybindingDefaults.cs` 中增加 `ActionSidebarScrollUp`、`ActionSidebarScrollDown`、`ActionSidebarPageUp`、`ActionSidebarPageDown` 动作常量与描述。
   - 默认绑定至 `ContextChat` 与 `ContextGlobal`（`alt+up`, `alt+down`, `alt+pageup`, `alt+pagedown`）。
2. **TUI 事件穿透**：
   - 在 `ChatInputView.Keys.cs` 中拦截上述动作，触发 `SidebarScrollUpRequested` 等事件。
   - 在 `ReplShell.cs` 中绑定事件，若 `PlanSidebarView` 或 `TeamSidebarView` 可见，则调用其内部 `MessageListView` 的滚动 API。
   - 在 `SidebarViewBase.cs` 增加对外公开的滚动委托接口。

### 阶段二：折叠块键盘交互（思考与工具详情）
1. **MessageListView 键盘导航模式（可选）**：
   - 支持快捷键在最近的工具/思考折叠行之间跳转；
   - 支持快捷键（如 `Ctrl+O`）一键展开/折叠当前会话中最后一条工具详情或思考过程。

### 阶段三：文档与 Schema 同步
1. 更新 `docs/keybindings.md`，追加侧边栏滚动的默认按键说明与使用提示。
2. 运行 `/keybindings validate` 验证 JSON Schema 能够正确补全新增动作。
