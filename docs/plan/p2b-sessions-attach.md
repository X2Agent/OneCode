# P2b · `onecode sessions` / `attach` 跨终端接入

> 状态：待实施。
> 依赖：[P2a 引擎侧多会话](p2a-engine-multi-session.md)（会话级执行权、TranscriptEventBuffer 快照、
> 按会话事件/交互路由）、[P1 daemon 验收](p1-daemon-acceptance.md)（后台会话宿主）。
> 后继：[P4 远程工作流](p4-remote-workflow.md)。
> 关联：ADR-0018「会话执行权与事件缓冲」（P2a 交付）、
> [docs/adr/0011-background-agents-delegation-boundary.md](../adr/0011-background-agents-delegation-boundary.md)
> （后台会话/attach 复用 `QueryStreamEngine`/权限/Build 门禁，不构成后台代理派工触发）。

## 1. 背景与动机

会话存储全局（`~/.onecode` 下跨进程可见），但接入能力是单终端的：一个 OneCode 进程一个前台会话视图，
终端关闭即视图消失。「切到另一个会话」目前只能在进程内 `/session switch`。P2a 已把引擎改造为会话级
执行上下文；本计划补上跨终端的可见与接入：任意终端查看全部会话状态、接入任一运行中会话，会话不随
终端死。原则（[ADR-0013](../adr/0013-ui-surface-strategy.md)）：**多路复用交给终端**——新开控制台窗口
即新会话；跨终端接入同一会话用 attach；进程内不建 tab 条。

## 2. 目标

- `onecode sessions`：全局会话列表（按项目分组），显示运行状态（空闲/执行中/待审批）。
- `onecode attach <id>`：从另一终端接入运行中会话——事件回放到当前点 + 增量订阅 + 审批应答。
- 后台会话可在 daemon 宿主中 headless 运行（`IConversationRunner` 无 TUI 可跑，Automation 已验证）。
- 跨进程执行权互斥：任一时刻一个会话至多一个活跃查询执行者（前台 TUI、attach 端或 daemon 三者之一）。

## 3. 非目标

- 进程内 tab 条与会话切换 UI（已裁决不做）。
- 对外网络端点（attach 是本机多进程能力，不是服务化；[ADR-0017](../adr/0017-ui-contract-single-layer.md)）。
- 远程访问协议自研（ssh 即协议，归 [P4](p4-remote-workflow.md)）。

## 4. 任务分解

### 批 A · `onecode sessions` 全局会话列表

1. 启动分发扩展：`LaunchModeDetector`（`src/OneCode.Tui/LaunchModeDetector.cs`）增 `sessions` 模式，
   `FastPathDispatcher` / `Program.Main` 分发到无交互列表视图（打印即退出）。
2. 列表数据：`ISessionStore.ListAsync` 全局会话 + 各会话运行状态（执行中/待审批来自 daemon 状态文件
   与执行锁探测）；按项目（工作目录）分组，最近活跃在前。
3. `/session` 会话内列表改造成同一视图（同一数据源与分组语义，TUI 内嵌渲染）；`/resume` 职责收窄为
   恢复持久化的 Goal 任务，不再承担普通会话切换。
- **验证**：双项目双会话（一执行中一待审批）下列表分组与状态正确；`/session` 与 `onecode sessions`
  同源。

### 批 B · 后台会话 headless 执行（daemon 宿主）

4. `BackgroundSession` 在 daemon 中作为 headless 运行宿主：复用 `IConversationRunner` 与
   `CronJobExecutor` 已验证的无人值守链路（审批走通知 + attach 应答，不弹无头 UI）。
5. 运行状态与未决审批写入 daemon 状态文件（`/daemon` 状态视图与 `onecode sessions` 消费同一数据）。
- **验证**：TUI 关闭、daemon 常驻，后台会话经触发执行；审批挂起产生 toast，`onecode sessions` 可见
  「待审批」。

### 批 C · `onecode attach <id>`

6. 启动分发增 `attach` 模式（`onecode attach <sessionId>`）。
7. 视图重建：`FileSessionEventStore` 回放到当前点（事件溯源已支持）+ P2a `TranscriptEventBuffer`
   快照全量重建，再接增量订阅；回放不重新执行工具、无副作用。
8. 接入后能力：只读观察 + 可应答审批；**提交查询需先取得执行权**（见批 D），未持权时输入区给出
   「只读模式」提示。
9. 断开语义：attach 端退出不影响会话本体（宿主仍是原进程或 daemon）；再次 attach 从当前点重建。
- **验证**：终端 B attach 终端 A 的长任务会话，看到实时进度并应答一次审批；attach 端 Ctrl-C 后会话
  继续运行。

### 批 D · 跨进程执行权文件锁

10. 实现执行权互斥的跨进程半边（P2a 已定义接口）：会话级文件锁（先例：cron 的
    `~/.onecode/cron/locks/{id}.lock` 独占锁语义），前台 TUI / attach 端 / daemon 三方同一套获取
    与释放协议；进程异常退出的锁回收（陈旧锁探测）。
11. 冲突提交明确提示「该会话正在其他终端执行中」，不排队、不静默丢弃。
- **验证**：同会话双终端同时提交，后者被拒；持权端崩溃后锁可被回收再获取。

## 5. 验收

1. 终端 A 跑项目甲 BUILD 长任务；另开终端窗口（同项目）第二会话提问并行进行。
2. 终端 B `onecode sessions` 看到两个会话的运行状态；`onecode attach` 接入项目乙会话看到实时进度
   并应答审批。
3. 同会话双端同时发起查询，后者收到互斥提示。
4. attach 端断开，会话本体（原终端或 daemon）不受影响。
5. `dotnet build` 零新增警告 + `dotnet test` 全绿。

## 6. 明确不做

- 多 attach 端之间的视图同步协商（多端各自快照重建 + 增量，不保证逐帧一致）。
- 会话迁移（把会话从一个宿主搬到另一个宿主）——只做接入，不做搬迁。
