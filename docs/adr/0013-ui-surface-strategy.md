# UI 形态战略：TUI 极致化单路线

**状态**: Accepted（UI 形态部分由 [UI 契约单层化](./0017-ui-contract-single-layer.md) 取代）
**日期**: 2026-09-30
**关联**: [TUI 极致化路线图](../plan/tui-roadmap.md)（本决策的工程落地图纸，P0-P4）、
[子代理派工边界](./0011-background-agents-delegation-boundary.md)（后台会话/attach/守护进程复用引擎与权限门禁，
不构成背景代理派工触发）、[决策模型接入](./0012-decision-model-integration-jev.md)（决策模型不得产生 Allow 的授权边界）

## 语境

### 1. 讨论缘起

2026-09-30 评估 OneCode 的 UI 方向：原 `docs/plan/web-host-plan.md` 拟以「`onecode web` 本地 Web UI 完整替代 TUI」，
提问背景是 Cline 与 DeepSeek 均已推出原生客户端。经业界调研与四项诉求（并行会话监控、富渲染、远程访问、桌面集成）
的逐项可行性核实，定稿为 **TUI 极致化主线**：不做桌面客户端，也不做替代 TUI 的 Web 界面，把 UI 投入压在 TUI 与
无头引擎底座上。浏览器客户端不在「替代 TUI」之列——它作为进程内对等消费端由
[UI 契约单层化](./0017-ui-contract-single-layer.md) 立项。

### 2. 业界调研（2026-09-30，来源见 §引用）

| 项目 | 实际演进路径 | 对本决策的含义 |
|---|---|---|
| Cline | VS Code 插件（1100 万+用户）→ 基于 Cline SDK 重构 harness（2026-09-02）→ 原生桌面端（2026-09-14，开源，Mac/Win） | **从未有 web UI**；桌面端复用同一 harness；立项动机 =「并行 agent 任务的集中工作台」+ 触达非编辑器用户 |
| DeepSeek Harness | 2026-08-13 开源预览，「一切皆插件」（模型/工具/会话/UI 均为插件） | 官方主入口至今是 `npx dsh web` **本地 Web UI**；桌面端是叠加的壳——「业界抛弃 web UI」的前提不成立 |
| opencode 2.0 | shared-server 架构：一个 agent server 驱动 TUI、桌面端（beta）、Web、IDE 扩展 | TUI 仍是旗舰；桌面只是「另一个窗口」；server-first 是架构本体 |
| Claude Code | CLI 旗舰 + VS Code 扩展 + claude.ai 云端 agents + 桌面端（2026-04-14 重设计，8 月末跨端 `/resume`） | CLI 与桌面共享同一 agent，会话跨端接力是卖点 |
| Codex | CLI + IDE 扩展 + 云端 web + 原生 App | App 定位「command center」：管理本地 + 云端并行任务 |

收敛模式：**复用单元从 UI 下沉到 harness/server**（Cline 先重构 SDK harness 再出桌面端；DeepSeek 把 UI 降格为插件之一；
opencode 直接 server-first）；TUI 仍是程序员用户旗舰（Claude Code/opencode 有桌面端后均未削弱 TUI）；
所谓「原生客户端」几乎全是 web 技术打包壳 + OS 集成，「native vs web」是伪对立。

### 3. 原生客户端的真实优势与 TUI 等效方案

原生客户端相对浏览器标签页的优势：① agent 会话以小时计，浏览器标签页生命周期不可靠；② 系统通知/托盘/全局快捷键/
安全 key 存储，「审批等你决策」必须能抢回注意力；③ localhost HTTP 安立面（token/CSRF/本机其他进程）；
④ 并行会话窗口管理是桌面范式。

逐项核实后，这些优势在**程序员使用场景**下均有 TUI 等效解（详细设计见路线图）：并行监控 → 后台会话 +
终端多窗/tmux 并置 + `onecode sessions/attach`（引擎能力，与 UI 框架无关；进程内 tab 条已裁决不做，见路线图 §9）；桌面集成 → `onecode daemon` 无头守护进程 +
OS 通知器（toast/OSC 9），托盘降为可选薄助手；远程访问 → ssh/mosh + attach（比自有协议更普适，用户已确认可接受）；
富渲染 → 「TUI 是调度器，本机软件是渲染器」（产物卡一键调起系统默认应用与编辑器；图片仅在终端图形协议支持时内联）。
**唯一无等效解的是 HTML/富媒体内嵌渲染与为非程序员设计的 GUI 面貌**——前者本就该交给浏览器，
后者与本产品「只考虑程序员使用」的定位正交。

### 4. 代码现状

- `OneCode.Tui` 是独立项目（进程入口 + Terminal.Gui 视图/浮层/渲染器 + TUI 装配件），`OneCode.App` 零 Terminal.Gui 依赖；
  引擎只产 `QueryEvent` 单层契约，四类交互走 Core 端口，见
  [UI 契约单层化](./0017-ui-contract-single-layer.md)；
- 有利面：`IConversationRunner`/QueryEvent 流 public；会话事件溯源**全局**存储
  （`FileSessionEventStore` 挂 `PathsHelper.UserHome`，跨项目 attach 天然可行）；Automation 已在无 TUI 上下文运行；
- 已有地基：`SessionContextBar`（每会话项目/分支/用量身份行）、Plan 侧边栏、`/resume`、`ApprovalBroker`、
  `Process.Start + UseShellExecute` 先例（`KeybindingsCommand`）；
- 真实缺口：`CronSchedulerService` 挂在主进程，TUI 一关定时任务即停（P1 守护进程的立项理由）。

### 5. 用户决策输入（2026-09-30）

定位「自用 + 开源社区」；四项诉求全选；SSH 远程方式明确可接受；经四项详细方案确认后定稿 TUI 极致化主线。
浏览器客户端与 agent desktop 不在「替代 TUI」之列：浏览器客户端作为进程内对等消费端由
[UI 契约单层化](./0017-ui-contract-single-layer.md) 立项；agent desktop 维持不做。

## 决策

1. **TUI 是默认交互前端，深耕不设上限**。键盘与交互打磨按自身路线演进，并独占交互打磨预算；
   浏览器客户端是进程内对等消费端，样式与交互独立演进，不分流 TUI 预算（见
   [UI 契约单层化](./0017-ui-contract-single-layer.md)）。
2. **富渲染走「TUI 是调度器，本机软件是渲染器」**：对话产物注册为产物卡，一键调起系统默认应用
   （`o` 打开 / `d` 调 diff 工具 / `e` 跳编辑器）；图片仅在 Sixel/kitty 能力检测通过时内联；HTML 预览一律交系统浏览器。
3. **并行会话与多项目**：一个进程 = 一个项目 = 一个前台会话视图，跨项目与跨会话的并置切换交给终端
   多路复用器（tmux/Windows Terminal，新开窗口即新会话）+ 全局会话存储 + `onecode sessions/attach`
   跨终端接入同一会话；不重造窗口管理，进程内不建 tab 条（裁决见路线图 §9）。
4. **定时任务与后台执行进无头守护进程**：`onecode daemon` 复用引擎组合根（剥掉 TUI 注册段），
   开机自启交给 OS 调度器（任务计划程序/systemd/launchd），通知走 OS 通知器 + OSC；托盘为可选薄助手、默认不做。
5. **远程访问 = ssh/mosh + attach**，不建自有协议、不做自有客户端。
6. **以下各项「不做」（非门控缓做）**：桌面壳（Photino/Electron/Tauri/MAUI）、单进程多项目切换、
   HTML/富媒体内嵌渲染。**Web UI 与 AG-UI 协议端点两项已由 [UI 契约单层化](./0017-ui-contract-single-layer.md) 取代**：
   浏览器客户端以 Blazor Server 形式立项，与 TUI 同为进程内消费端；对外网络端点仍然不做。

## 边界与禁令

1. **业务核心不得反向依赖 TUI/前端**：引擎/编排/流式层出现 `Terminal.Gui` 或前端命名空间引用即回归；
   P0 验收含 grep 复查（路线图 §3.3）。
2. **不在 TUI 内追浏览器渲染效果**：HTML/CSS/富媒体一律委托本机软件；能力检测失败就走「打开」，不做像素级内嵌。
3. **不做单进程多项目切换**：项目级配置作用域与工具 cwd 语义不得被多项目混居破坏（路线图 §4.1）。
4. **托盘助手不得承载 UI**：只做存活指示/快捷菜单/退出；任何「顺手加个面板」都违反本决策。
5. **preview 类静态服务器（若立项）不是 web UI 的例外通道**：只服务文件预览，不得在其上长出任何界面。
6. **AG-UI 协议不引入**。排除依据是「无进程外消费端、不暴露任何端点」，与协议包或托管包的
   版本成熟度无关；重启判据见 [UI 契约单层化](./0017-ui-contract-single-layer.md)。

## 后续工作

均不在本 ADR 实施范围，各自独立立项；工程细节以 [TUI 极致化路线图](../plan/tui-roadmap.md)为准。
门禁：`dotnet build src/OneCode.slnx` 无新增警告 + `dotnet test src/OneCode.slnx` 全绿。

| ID | 事项 | 阶段 | 状态 |
|---|---|---|---|
| U1 | P0 引擎解耦（Tui 项目边界 + 契约单层化） | P0 | 已落地（见 [UI 契约单层化](./0017-ui-contract-single-layer.md)） |
| U2 | P1 `onecode daemon` 常驻调度 + 通知触发补齐 + 安装脚本（托盘可选；OS 通知器已实现并接线） | P1 | 待立项 |
| U3 | P2 `onecode sessions/attach` 跨终端接入 + `TranscriptEventBuffer`（进程内 tab 条已裁决不做，见路线图 §9） | P2 | 待立项 |
| U4 | P3 产物卡 + 三平台 open helper + 图片能力检测内联 | P3 | 待立项 |
| U5 | P4 ssh/mosh 远程工作流文档（`onecode run -p` 已裁决暂缓，见路线图 §9） | P4 | 待立项 |

## 后果

- **正面**：UI 投入以 TUI 为单点，浏览器客户端是进程内对等消费端、不分流交互打磨预算（见
  [UI 契约单层化](./0017-ui-contract-single-layer.md)）；四项诉求均有等效解且两项（并行监控、远程）的正解本就在引擎侧；
  P0-P2 是任何未来路线的公共地基——若程序员场景出现 TUI 无法覆盖的硬需求，重启 web 评估时这些投入原样复用；
  决策一次写死，「TUI 还是桌面」不再反复讨论。
- **负面**：富渲染天花板明确存在（图片内联依赖终端模拟器、HTML 永远跳出终端）；手机 ssh 体验只保可用不保精致；
  GUI 展示面限于本机 loopback 的浏览器客户端，不对远程与移动端开放；社区吸引力主要押在引擎与 TUI 质量上
  （Claude Code 证明此路可通，但天花板真实）；原多前端计划的交互协议/REST 设计随之废弃。
- **中性**：TUI 用户感知零变化；`docs/plan/web-host-plan.md` 撤稿删除，仍成立的设计（解耦清单、事件底座、
  attach 并发模型）已并入 [TUI 极致化路线图](../plan/tui-roadmap.md)。

## 引用

- Cline Desktop 发布：`cline.bot/blog/cline-desktop-an-open-source-app-for-open-weight-models`（2026-09-14）、`cline.bot`
- DeepSeek Harness：`deepseek.com/harness`、`github.com/deepseek-ai/deepseek-harness`（2026-08-13 开源预览）
- opencode：`opencode.ai`（2.0 迁移指南、shared-server 架构）
- Claude Code 桌面端：`code.claude.com/docs`、2026-08-28 跨端 `/resume` 公告
- Codex App：`developers.openai.com/codex/app`
- 代码证据：`src/OneCode.App/Query/QueryEventTypes.cs`（唯一 UI 契约）、
  `src/OneCode.Core/Permissions/IApprovalBroker.cs`（审批端口）、
  `src/OneCode.App/Query/IConversationRunner.cs`、`src/OneCode.App/Session/FileSessionEventStore.cs`
  （全局存储挂 `PathsHelper.UserHome`）、`src/OneCode.Automation/Cron/CronSchedulerService.cs`（主进程宿主缺口）、
  `src/OneCode.App/Services/Agent/ApprovalBroker.cs`、`src/OneCode.App/Commands/KeybindingsCommand.cs`
  （`Process.Start` 先例）
- 用户决策输入：定位「自用 + 开源社区」、四诉求全选、SSH 可接受、TUI 极致化定稿（2026-09-30）
