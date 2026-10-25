# OneCode TUI 极致化路线图

> 状态：P0 项目边界与契约下沉已落地（TUI 独立为 `OneCode.Tui`；App 零 Terminal.Gui 依赖，且不再产出 UI 形状事件，
> 见 [TUI 项目边界](../adr/0016-tui-project-boundary.md) 与 [UI 契约单层化](../adr/0017-ui-contract-single-layer.md)）；
> P1 daemon 已落地（§4）；P2-P4 依次推进。
> UI 形态：TUI 与 Blazor Server 浏览器客户端同为进程内消费端，对外网络端点不做
> （决策依据与业界调研见 [UI 形态战略决策](../adr/0013-ui-surface-strategy.md)，本计划是其工程落地图纸）。
> 关联文档：[后台代理派工边界](../adr/0011-background-agents-delegation-boundary.md)（后台会话/attach/守护进程复用
> `QueryStreamEngine`/权限/Build 门禁，不构成背景代理派工触发）、[docs/keybindings.md](../keybindings.md)。
> 范围：本路线只覆盖宿主/交互形态。引擎能力演进（如控制型浏览器自动化工具、GitHub/PR 集成）不在本路线范围，
> 出现真实需求另行立项。

## 1. 路线总览

| 阶段 | 内容 | 一句话目标 | 依赖 |
|---|---|---|---|
| **P0** | 引擎与 TUI 解耦 | 引擎可脱离 TUI 运行，attach/守护进程的地基 | 无（已落地） |
| **P1** | `onecode daemon` 无头守护进程 + 通知触发补齐 | TUI 关了定时任务照跑、审批消息弹 toast | P0 |
| **P2** | `onecode sessions` / `attach` 跨终端接入 | 任意终端介入任意会话，会话不随终端死 | P0、P1 |
| **P3** | 产物卡 + 本机软件调起 + 图片能力检测内联 | 对话产物一键交给系统里最专业的软件 | P0 |
| **P4** | ssh/mosh 远程工作流文档化 | 远程机器与手机上可用 | P1、P2 |

四项用户诉求与本路线的映射：并行会话监控台 → 终端多窗/tmux 并置（新开控制台窗口即新会话，见 §2 原则 2），
P1 daemon 让会话与定时任务常驻、P2 `sessions`/`attach` 提供跨终端可见与接入；桌面系统集成（通知/定时）→ P1
（托盘为可选薄助手，§4）；富渲染与可视化 → P3；远程访问 → P4（ssh 即协议，用户已确认可接受）。

## 2. 设计原则（与 UI 形态战略决策一一对应）

1. **TUI 是调度器，本机软件是渲染器**：TUI 只做摘要与入口，文件/网页/diff 的渲染委托系统默认应用与编辑器；不在 TUI 内追浏览器效果。
2. **多路复用交给终端**：一个 OneCode 进程 = 一个项目 = 一个前台会话视图；跨项目与跨会话的并置切换用终端
   窗口/tab/tmux——新开一个控制台窗口即得一个新会话（无单实例锁、会话存储全局，`/new`/`/session`/`/resume` 既有）；
   跨终端接入同一会话用 attach。进程内不建 tab 条（裁决见 §9）。
3. **后台能力进守护进程，不进 UI**：定时任务、后台会话的宿主是无头进程；通知走 OS 现成设施（toast/OSC），托盘只是化妆品。
4. **引擎零前端依赖**：业务核心不得反向引用 Tui/前端命名空间（P0 验收含 grep 守卫）。

## 3. P0 · 引擎与 TUI 解耦

### 3.1 当前结构（项目边界已落地）

TUI 已独立为 `OneCode.Tui` 项目（Terminal.Gui 视图/浮层/渲染器 + TUI 装配件 + 进程入口），
`OneCode.App` 零 Terminal.Gui 依赖（包引用 / global using / 类型使用三层均无命中）。

引擎与前端只经 `QueryEvent` 单层契约交互，四类交互走 Core 端口：

| 契约 | 文件 | 消费方 |
|---|---|---|
| `QueryEvent` 事件层级（25 个子类型） | `src/OneCode.App/Query/QueryEventTypes.cs` | TUI 三处分发、WebUI 会话客户端 |
| 契约基线（新增子类型必须同步两个前端渲染） | `src/OneCode.Tests/QueryEventContractTests.cs` | 两个前端的未知事件告警、基线守卫 |
| 交互端口与 App 侧统一实现 | `src/OneCode.Core/Permissions/IApprovalBroker.cs`、`src/OneCode.Core/Permissions/IApprovalPromptSink.cs`、`src/OneCode.App/Services/Interaction/` | 审批 / 提问 / 向导 / 预算 |
| `PlanCardDecision` / `PlanCardPhase` / `PlanStep` / `PlanStepStatus` | `src/OneCode.App/Services/PlanMode/PlanCardTypes.cs` | Plan 工作流、审批门 |
| `SlashCommandEntry` | `src/OneCode.App/Commands/SlashCommandEntry.cs` | 交互会话、补全目录 |
| `McpConfigServerEntry` / `McpConfigResult` / `McpConfigServerChange` | `src/OneCode.App/Services/Mcp/McpConfigTypes.cs` | `McpConfigService` |

DI 注册按领域归属拆分：`AddStreamingServices`（App：流式管道、Build 回放、QueryEvent 旁路）+
`AddAppRuntimeServices`（App：斜杠命令管线、模式桥、会话启动）+
`AddInteractionCoreServices`（App：交互端口默认实现）+
`AddTuiServices`（OneCode.Tui：Terminal.Gui 栈与交互装配件）。组合根 `OneCodeApp.Create(args, configureTuiStack)`
经 App 侧 `InteractiveShellRunner` 委托驱动前端——无头宿主（daemon / attach）注册核心服务后自行驱动引擎，
这是 P1 的前置条件。

`OneCode.WebUI` 首版已落地：单会话对话、工具卡片、审批/提问/预算三类中断卡片、会话列表/切换/新建/关闭
与斜杠命令分发（多问题向导暂按取消降级）；范围与边界见
[UI 契约单层化](../adr/0017-ui-contract-single-layer.md)。

### 3.2 阶段 B · 契约下沉（已完成）

流式管道、Goal/Build/Loop 全部产出 `QueryEvent`；四类交互收敛为 Core 端口
（`IApprovalBroker` / `IApprovalPromptSink` / `IUserQuestionService` / `IBudgetDecisionService`），
端口实现统一在 App（经 `IInteractionDispatcher` 投递到前端），前端只替换投递通道；
`TuiEvent` 层级、`TuiEventMapper` 与旧旁路桥删除，`src/OneCode.App/Tui/` 目录不再存在。
非运行通知（MCP 连接变化 / LSP 诊断变化）由前端直接订阅源事件刷新。

| # | 原障碍 | 处置 |
|---|---|---|
| B1 | 流式管道产出 UI 形状事件 | 引擎改产 `QueryEvent`，UI 契约层整体删除 |
| B2 | 审批/提问经 `TaskCompletionSource` 回传 UI 形状请求 | 收敛为 Core 端口，App 统一实现经投递通道达前端；事件不再携带等待句柄 |
| B3 | Terminal.Gui 视图寄生在组合层 | 视图迁入 `OneCode.Tui`，App 只保留 UI 无关契约（`QueryEvent` / 端口 / DTO），`src/OneCode.App/Tui/` 目录删除 |

### 3.3 验收（阶段 B）

`grep -r "using OneCode.App.Tui" src/OneCode.App` 零命中；`grep -r "TuiEvent" src/` 零命中；
`dotnet build src/OneCode.slnx` 零新增警告 + `dotnet test src/OneCode.slnx` 全绿。

## 4. P1 · `onecode daemon` 无头守护进程 + 通知（已落地）

### 4.1 交付内容

- **`onecode daemon` 无头宿主**（`src/OneCode.App/Daemon/DaemonHost.cs`；启动分发 `LaunchMode.Daemon`，
  分支在 `Program.Main`——全量 DI 路径不进 `FastPathDispatcher`）：核心组合根（不挂 TUI 注册段）
  承载全部既有后台服务；单实例互斥体（兼作 `/daemon` 存活探针）+ 状态文件 + 停止哨兵
  （`DaemonLifecycle` / `DaemonStopWatcher`）。daemon 是 `/daemon start` 与 OS 调度器共用的内部启动协议，
  不作用户功能入口宣传。
- **`/daemon` 命令**（TUI 管理面，`Commands/DaemonCommand.cs`）：状态视图（PID/启动时间 + 本项目
  durable cron 概况 + 未运行时的安装指引）、`start`（分离启动，回报 PID 与日志目录）、
  `stop`（停止哨兵，15 秒优雅上限，超时只报告不强杀）。
- **cron 多进程正确性**：`CronJobEntry.ProjectDir` 项目归属盖章与加载过滤（别项目的 durable 作业不再被
  误触发）；durable 触发前的跨进程逐作业锁（同项目 TUI + daemon 并存不双发）。语义详见
  [docs/background-services.md](../background-services.md) §4.4。
- **通知触发补齐**：审批挂起超 30 秒宽限 toast（`InteractionApprovalPromptSink`）、交互运行超 2 分钟
  完成 toast（`QueryStreamEngine`，无人值守路径由 `CronJobExecutor` 通知，不双发）。
- **开机自启**：`scripts/install-daemon.ps1`（Windows 任务计划程序，含卸载）、
  `scripts/onecode-daemon.service`（systemd 模板）、`scripts/com.onecode.daemon.plist`（launchd 模板）。

### 4.2 本阶段不做

托盘（可选薄助手，daemon + toast 覆盖 90% 价值后再评估）；OSC 9 并发终端标记（全屏终端兼容性风险，后置）。

### 4.3 验收

TUI 完全关闭，daemon 常驻，cron 到点照跑、toast 到达；重开 TUI 经 `/session` 可见 cron 执行历史会话；
TUI 内 `/daemon` 可见运行状态并显式启停；同项目多进程并存不双触发；别项目作业不在本项目触发。

## 5. P2 · `onecode sessions` / `attach` 跨终端接入

会话间切换不建进程内 tab（§2 原则 2、§9）：一个 OneCode 进程一个前台会话视图，「切到另一个会话」= attach 到它。
本阶段交付引擎侧多会话基础设施与跨终端接入。

### 5.1 引擎侧多会话

- **多会话执行**：后台会话循环 headless 运行（`BackgroundSession` 已存在于 `Session/`，Automation 已验证
  `IConversationRunner` 无 TUI 可跑），宿主是 P1 的 daemon；前台任一终端 attach 其一，其余会话状态经
  `onecode sessions` 查看（空闲/执行中/待审批）。
- **`onecode sessions`**：全局会话列表（会话存储本就是全局的），按项目分组，显示运行状态与未决审批。
  会话内已有的 `/session` 列表改造成按项目分组的同一视图；`/resume` 专用于恢复持久化的 Goal 任务。
- **`onecode attach <id>`**：从另一终端接入运行中会话——`FileSessionEventStore` 回放到当前点
  （事件溯源已支持）+ 订阅增量；审批经 `ApprovalBroker` 同一待决面应答。
- **事件缓冲**：`BackgroundSession.EventBuffer` 目前仅预留了属性槽位尚未启用；本阶段在 App 层落地
  `TranscriptEventBuffer`（有界环形缓冲，按序保存近期 `QueryEvent` 并支持不可变快照导出），
  会话级单生产者写入；attach 时从快照全量重建视图再接增量。
- **执行权互斥**：任一时刻一个会话至多一个活跃查询执行者（前台 TUI、attach 端或 daemon 三者之一）；
  其余连接只读观察 + 可应答审批。会话级执行权锁（进程内锁 + 跨进程文件锁），冲突提交明确提示「该会话正在
  其他终端执行中」。

### 5.2 新增命令与验收

- 启动分发扩展：`LaunchModeDetector` 增 `sessions`/`attach` 模式（`src/OneCode.Tui/LaunchModeDetector.cs` + `FastPathDispatcher.cs`）。
- **验收**：终端 A 跑项目甲 BUILD 长任务，另开终端窗口（同项目）继续第二会话提问；终端 B `onecode sessions`
  看到两个会话的运行状态，`onecode attach` 接入项目乙会话看到实时进度并应答审批；同会话双端同时发起查询时
  后者被互斥提示。

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

## 7. P4 · ssh/mosh 远程工作流

- **远程工作流文档化**（`docs/remote-workflow.md` 新增）：`ssh -t devbox onecode`（或登录后 `onecode attach`）、
  mosh 管漫游、tmux 管断线；Windows 开发机开 OpenSSH Server；手机 Termius/Blink 走同一套。
- **验收**：手机 ssh 到家里机器 attach 会话、应答审批、看 cron 执行历史，全程 TUI。

## 8. 风险与对策

| 风险 | 对策 |
|---|---|
| 富渲染天花板（图片内联受终端模拟器割裂、HTML 永远要跳出终端） | 设计原则 1 写死：TUI 只做摘要与入口；能力检测 + 降级链（内联 → `o` 打开） |
| 手机/远程 ssh TUI 体验一般 | 定位为可用而非精致；mosh/tmux 缓解断线；不为此建自有协议 |
| 无 GUI 展示面，社区吸引力全押引擎与 TUI | asciinema/GIF 演示 + 引擎质量；与 UI 形态战略决策一致，硬需求出现再立项 |
| daemon 与 TUI 并发写同一会话 | 执行权锁（§5.1）+ 事件存储追加写语义；契约测试走真实写入端 |
| toast 点击无法聚焦终端 | 承诺降级：通知只保证「知道有事」，介入靠 `attach`/重开 TUI |

## 9. 明确不做清单

- **对外网络端点（含 AG-UI 协议端点）**：不做。依据是「无进程外消费端、不暴露端点」，
  与协议包成熟度无关；AG-UI 协议的评估结论、自研单层契约的边界与重启判据见
  [UI 契约单层化](../adr/0017-ui-contract-single-layer.md)。
- **应用内 tab 条（进程内多会话切换 UI）**：不做。多会话并置交给终端（设计原则 2）——新开控制台窗口即新会话
  （无单实例锁、会话存储全局，今天已零成本可用），「会话间切换」由 P2 的 attach 承担；tab 条的净增量
  （省一次 alt-tab）撑不起每 tab 视图模型、输入路由、审批汇聚的实现与维护成本。
- **`onecode run -p` 非交互单发**：暂缓。当前无真实消费场景（无 CI 集成、一次性任务开 TUI 即可、
  定时走内置 cron、远程走 ssh + TUI）；headless 执行链路已由 `CronJobExecutor` 验证，出现脚本/CI 硬需求
  随时可补（启动分发 + stdout 格式化 + 退出码映射，工作量以天计）。
- **桌面壳**：Photino / Electron / Tauri / MAUI，不做。
- **单进程多项目切换**：不做。项目级配置作用域、工具 cwd 语义、内存隔离会互相污染，且终端多路复用器已把
  这件事做得很好（设计原则 2）。
- **HTML/富媒体内嵌渲染**：不做（§6.2）。
- 以上任何一项若未来出现 TUI 无法覆盖的程序员场景硬需求，按 [UI 形态战略决策](../adr/0013-ui-surface-strategy.md)
  重新立项评估——P0-P2 投入（引擎解耦/守护进程/attach）届时原样复用。
