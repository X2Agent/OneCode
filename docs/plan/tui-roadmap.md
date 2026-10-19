# OneCode TUI 极致化路线图

> 状态：计划稿（P0 待立项，P1-P4 依次推进）。
> UI 形态战略（TUI 是唯一交互前端；Web UI / 桌面壳 / AG-UI 端点不做）的决策依据与业界调研见
> [UI 形态战略决策](../adr/0013-ui-surface-strategy.md)，本计划是其工程落地图纸。
> 关联文档：[后台代理派工边界](../adr/0011-background-agents-delegation-boundary.md)（后台会话/attach/守护进程复用
> `QueryStreamEngine`/权限/Build 门禁，不构成背景代理派工触发）、[docs/keybindings.md](../keybindings.md)。

## 1. 路线总览

| 阶段 | 内容 | 一句话目标 | 依赖 |
|---|---|---|---|
| **P0** | 引擎与 TUI 解耦 | 引擎可脱离 TUI 运行，attach/守护进程的地基 | 无，立即开工 |
| **P1** | 后台多会话并行 + tab + `onecode sessions/attach` | 一个终端盯多个会话，任意终端介入任意会话 | P0 |
| **P2** | `onecode daemon` 无头守护进程 + OS 通知 | TUI 关了定时任务照跑、审批消息弹 toast | P0 |
| **P3** | 产物卡 + 本机软件调起 + 图片能力检测内联 | 对话产物一键交给系统里最专业的软件 | P1 |
| **P4** | `onecode run -p` 单发模式 + ssh/mosh 工作流 | 远程机器与手机上可用 | P1、P2 |

四项用户诉求与本路线的映射：并行会话监控台 → P1；桌面系统集成（通知/定时）→ P2（托盘为可选薄助手，§5）；
富渲染与可视化 → P3；远程访问 → P4（ssh 即协议，用户已确认可接受）。

## 2. 设计原则（与 UI 形态战略决策一一对应）

1. **TUI 是调度器，本机软件是渲染器**：TUI 只做摘要与入口，文件/网页/diff 的渲染委托系统默认应用与编辑器；不在 TUI 内追浏览器效果。
2. **多路复用交给终端**：一个 OneCode 进程 = 一个项目；跨项目切换用终端 tab/tmux；跨进程接入用 attach。
3. **后台能力进守护进程，不进 UI**：定时任务、后台会话的宿主是无头进程；通知走 OS 现成设施（toast/OSC），托盘只是化妆品。
4. **引擎零前端依赖**：业务核心不得反向引用 Tui/前端命名空间（P0 验收含 grep 守卫）。

## 3. P0 · 引擎与 TUI 解耦（App 层内，无新项目）

### 3.1 现状

`using OneCode.App.Tui` 泄漏到 App 层 `Tui/` 目录之外共 **24 个文件**。有利事实：`Terminal.Gui` 包依赖本身封闭在
`Tui/` 目录内（唯一例外 `Services/TuiHostConfigurator.cs`）；`IConversationRunner` 流式契约 public（`Query/IConversationRunner.cs`，
`QueryStreamEngine.StreamQueryAsync` 实现）；QueryEvent 事件体系（16 种，`Query/QueryEventTypes.cs`）UI 无关；
会话事件溯源全局存储（`Session/FileSessionEventStore.cs` 挂 `PathsHelper.UserHome`）；Automation 的 `CronJobExecutor`
经 `IConversationRunner` headless 运行（引擎可脱离 TUI 的既有证明）。

泄漏分两类（初判，逐文件核实是 P0 第一步）：

- **A 类 · TUI 装配层（9 个，性质合理、位置混淆）**：`ClaudeCodeApp`（组合根）、`InteractiveBootstrapService`、
  `InteractiveSession`、`InteractiveTuiDependencies`、`TuiInteractionBridge`、`TuiCatalogDependencies`、
  `TuiHostConfigurator`、`TuiContextFactory`、`Setup/SetupServiceCollectionExtensions`。处置：收拢进 `Tui/`
  或统一命名，归入 TUI 专属注册段。
- **B 类 · 引擎层泄漏（14 个，真障碍）**：`Streaming/OrchestrationStreamService`、`Streaming/QueryStreamService`、
  `PlanMode/PlanAgentRunDispatcher`、`PlanMode/PlanApprovalGate`、`PlanMode/PlanWorkflowApplicationService`
  （含 `.Transitions.cs`）、`GoalMode/GoalWorkflowRuntime`、`GoalMode/GoalRuntimeProgress`、
  `Agent/GoalSubGoalExecutor`、`Agent/GoalSubGoalLoop`、`Loop/IterativeLoopService`、
  `BuildMode/BuildRunTuiReplayService`、`Mcp/McpConfigService`、`Services/UserQuestionService`
  （AskUserQuestion 工具路径，经 `TuiInteractionBridge` 转发 TUI 形状请求）。已抽样：
  `PlanApprovalGate` 真实引用 `PlanCardDecision`/`PlanCardPublisher`（交互类型需抽到 UI 无关层）；
  `QueryStreamService`、`GoalRuntimeProgress` 疑似仅残留 using（可直删）。

### 3.2 改动

| # | 障碍 | 改造方向 |
|---|---|---|
| O1 | Tui 命名空间泄漏约 25 文件 | A 类收拢进 TUI 装配层；B 类把被引用的交互类型（`PlanCardPublisher`/`PlanCardDecision` 等）迁到 UI 无关层，残留 using 直删 |
| O2 | 审批应答经 `TaskCompletionSource<ApprovalDecision>` 回传（`QueryEventTypes.cs`） | TCS 本身 UI 无关，**不改**；attach 端经 `ApprovalBroker`（`Services/Agent/ApprovalBroker.cs`）同一待决面应答 |
| O5 | `RegisterInteractiveServices` 注册 TUI 栈（`ServiceCollectionExtensions.*.cs`） | 拆分为 `RegisterInteractionCore`（UI 无关）+ `RegisterTuiServices`（Terminal.Gui 栈）；`OneCodeApp.Create` 串联，对外行为不变——这是 P2 守护进程复用同一组合根的前提 |

### 3.3 验收

`dotnet build src/OneCode.slnx` 零新增警告（TreatWarningsAsErrors）+ `dotnet test src/OneCode.slnx` 全绿；
grep 复查 App 层引擎/编排/流式目录无 `using OneCode.App.Tui`（TUI 装配层除外）。

## 4. P1 · 后台多会话并行 + tab + attach/sessions

### 4.1 两层结构（职责不越界）

- **应用内 tab（一个进程 = 一个项目，tab = 该项目的会话）**：顶部 tab 条；每 tab 一套会话视图模型，
  底栏复用 `SessionContextBar`（已有项目/分支/轮次/用量身份行）；待审批的 tab 高亮，审批状态汇聚到 `AgentStatusBar`。
  切换键经 `KeybindingResolver` 注册（预案：`alt+1..9`、`alt+←/→`，走 `KeybindingContextManager` 上下文路由）。
  新建/恢复会话从全局存储按当前项目过滤（`/session` 已存在，改造成按项目分组的列表；`/resume` 专用于恢复持久化的 Goal 任务）。
- **跨项目交给终端**：每个项目一个终端 tab/窗口跑各自 cwd 的 `onecode`。**明确不做单进程多项目切换**
  （项目级配置作用域、工具 cwd 语义、内存隔离会互相污染，且终端多路复用器已把这件事做得很好）。

### 4.2 会话并行与 attach（引擎侧）

- **多会话执行**：后台会话循环 headless 运行（`BackgroundSession` 已存在于 `Session/`，Automation 已验证
  `IConversationRunner` 无 TUI 可跑），前台 tab attach 其一，其余在 tab 条/侧栏显示状态（空闲/执行中/待审批）。
- **`onecode sessions`**：全局会话列表（会话存储本就是全局的），按项目分组，显示运行状态与未决审批。
- **`onecode attach <id>`**：从另一终端接入运行中会话——`FileSessionEventStore` 回放到当前点
  （事件溯源已支持）+ 订阅增量；审批经 `ApprovalBroker` 同一待决面应答。
- **事件缓冲**：`BackgroundSession.EventBuffer` 目前仅预留了属性槽位尚未启用；P1 在 App 层落地 `TranscriptEventBuffer`（有界环形缓冲，按序保存近期 `QueryEvent` 并支持不可变快照导出），
  会话级单生产者写入；attach 时从快照全量重建视图再接增量。
- **执行权互斥**：任一时刻一个会话至多一个活跃查询执行者（前台 TUI、attach 端或 daemon 三者之一）；
  其余连接只读观察 + 可应答审批。会话级执行权锁（进程内锁 + 跨进程文件锁），冲突提交明确提示「该会话正在
  其他终端执行中」。

### 4.3 新增命令与验收

- CLI 分发扩展：`CliModeDetector` 增 `sessions`/`attach` 模式（`src/OneCode.Cli/CliModeDetector.cs` + `FastPathDispatcher.cs`）。
- **验收**：终端 A 跑项目甲 BUILD 长任务，`alt+2` 切到项目甲第二会话继续提问；另开终端 `onecode attach`
  项目乙会话看到实时进度并应答审批；同会话双端同时发起查询时后者被互斥提示。

## 5. P2 · `onecode daemon` 无头守护进程 + OS 通知

### 5.1 现状缺口

`CronSchedulerService`（`src/OneCode.Automation/Cron/`）当前是寄宿在 TUI 主进程 `IHost` 内的 `BackgroundService`——**TUI 一关定时任务就停**（即使开启 `--durable` 将任务写入 `~/.onecode/cron/*.json`，也只能在用户下次重新启动 TUI 时由 `ReloadJobs` 补跑到期任务，无法在无 TUI 期间后台驻留触发）。本阶段引入独立 `onecode daemon` 进程正是为了彻底解决跨会话常驻调度问题。

### 5.2 设计

- **`onecode daemon`**：同一 IHost 组合根，剥掉 TUI 注册段（P0 的 O5 拆分正是为此），只挂
  Automation（`CronSchedulerService`/`ModelCatalogRefreshService`）+ 后台会话执行（`IConversationRunner`
  + `BackgroundSession`）+ 通知器。cron 到点 → headless 执行 → 结果写事件存储 → toast 通知；
  TUI/attach 重开时从事件存储看到全部执行历史。
- **开机自启交给 OS**：`scripts/install-daemon.ps1`（Windows 任务计划程序）/ systemd unit / launchd 各一份。
- **通知器**（设置「通知开关」已有此位，落地为实现）：审批挂起、长任务完成、cron 结束触发。
  Windows：PowerShell toast；macOS：`osascript`/terminal-notifier；Linux：`notify-send`；
  并发终端标记（OSC 9）让任务栏/标题栏显示未决状态。诚实预期：点击 toast 聚焦回终端窗口尽力而为，不承诺各终端一致。
- **托盘（可选、后置）**：极薄 `OneCode.Tray` 辅助进程，纯 C#（P/Invoke `Shell_NotifyIcon` 或 H.NotifyIcon），
  **不承载任何 UI**——只做守护进程存活指示、最近任务结果菜单、退出守护进程，约 200 行。默认不做；
  守护进程 + toast 覆盖 90% 价值后再评估。

### 5.3 验收

TUI 完全关闭，cron 到点照跑，toast 到达；重开 TUI/`attach` 能看到执行历史并可接管会话。

## 6. P3 · 产物卡 + 本机软件调起 + 图片内联

### 6.1 产物卡机制

- 工具产出或对话提到的文件（图片/html/pdf/xlsx/notebook…）注册为**产物事件**（路径 + 类型 + 来源工具）；
  TUI 渲染一行卡片（类型徽标 + 文件名 + 大小 + 状态），不渲染内容本身。
- **`o` 打开**：`Process.Start(UseShellExecute=true)` 交系统默认应用（先例：`Commands/KeybindingsCommand.cs`）。
  三平台 helper 一个静态类（`ShellExecute` / `open` / `xdg-open`）。
- **`d` diff**：`code --diff a b`（或用户配置的 diff 工具）；**`e` 跳转**：`code path:line`（或 `$EDITOR`）。
  文件对比与编辑永远交给编辑器。
- **自动预览策略**（设置三档：关 / 询问 / 任务结束自动打开）：agent 生成 `index.html` → 任务完成 toast +
  浏览器自动弹出。这是桌面端「内置浏览器面板」的 TUI 等价物，用的是用户自己配好的浏览器。

### 6.2 图片内联（能力检测，不做则降级）

检测 `TERM_PROGRAM`/`WT_SESSION`/kitty 图形协议 → 支持则 Sixel/kitty 内联缩略图；否则产物卡 `o` 打开。
**明确不做** HTML/CSS 内嵌渲染。可选加项（按需另立项，不进本路线默认范围）：Kestrel 纯静态文件 +
livereload 预览小服务（~200 行，无任何 UI）——注意它不是 web UI 的例外通道，不得在其上长出界面。

### 6.3 验收

agent 产出 `report.html` 与图片 → 会话尾部产物卡 → `o` 打开浏览器/照片查看器，diff 卡 `d` 在 VS Code
看对比，全程零鼠标；Windows Terminal 内联显示缩略图，VS Code 内嵌终端自动降级为 `o`。

## 7. P4 · `onecode run -p` 单发 + ssh/mosh 工作流

- **`onecode run -p "<task>"`**：非交互单发——headless 跑一轮查询，结果按 markdown/纯文本写 stdout，
  退出码表达成败（Automation `CronJobExecutor` 已有同构先例，补 CLI 分发与输出格式化）。服务 cron/CI/远程。
- **远程工作流文档化**（`docs/remote-workflow.md` 新增）：`ssh -t devbox onecode`（或登录后 `onecode attach`）、
  mosh 管漫游、tmux 管断线；Windows 开发机开 OpenSSH Server；手机 Termius/Blink 走同一套。
- **验收**：手机 ssh 到家里机器 attach 会话、应答审批、看 cron 执行历史，全程 TUI。

## 8. 风险与对策

| 风险 | 对策 |
|---|---|
| 富渲染天花板（图片内联受终端模拟器割裂、HTML 永远要跳出终端） | 设计原则 1 写死：TUI 只做摘要与入口；能力检测 + 降级链（内联 → `o` 打开） |
| 手机/远程 ssh TUI 体验一般 | 定位为可用而非精致；mosh/tmux 缓解断线；不为此建自有协议 |
| 无 GUI 展示面，社区吸引力全押引擎与 TUI | asciinema/GIF 演示 + 引擎质量；与 UI 形态战略决策一致，硬需求出现再立项 |
| daemon 与 TUI 并发写同一会话 | 执行权锁（§4.2）+ 事件存储追加写语义；契约测试走真实写入端 |
| toast 点击无法聚焦终端 | 承诺降级：通知只保证「知道有事」，介入靠 `attach`/重开 TUI |
| O1 分类初判有误（个别文件归错类） | 逐文件核实是 P0 第一步；以「引用了什么类型」而非「文件在哪」定类 |

## 9. 明确不做清单

- **Web UI**：`OneCode.Web`/Blazor/浏览器访问，不做。
- **桌面壳**：Photino / Electron / Tauri / MAUI，不做。
- **AG-UI 协议端点**：不做（`Microsoft.Agents.AI.*.AGUI` preview 包不进依赖面）。
- **单进程多项目切换**：不做（§4.1）。
- **HTML/富媒体内嵌渲染**：不做（§6.2）。
- 以上任何一项若未来出现 TUI 无法覆盖的程序员场景硬需求，按 [UI 形态战略决策](../adr/0013-ui-surface-strategy.md)
  重新立项评估——P0-P2 投入（引擎解耦/多会话/守护进程）届时原样复用。
