# P2a · 引擎侧多会话

> 状态：待实施。
> 依赖：[P1 daemon 验收](p1-daemon-acceptance.md)（后台会话宿主）。
> 后继：[P2b sessions/attach](p2b-sessions-attach.md)（跨进程文件锁与终端接入消费本计划的会话基础设施）。
> 关联：[docs/adr/0013-ui-surface-strategy.md](../adr/0013-ui-surface-strategy.md)（多路复用交给终端、进程内
> 不建 tab 条的原则）、[docs/adr/0017-ui-contract-single-layer.md](../adr/0017-ui-contract-single-layer.md)
> （QueryEvent 单层契约）。本计划交付 ADR-0018「会话执行权与事件缓冲」（执行权互斥 + 事件缓冲快照语义，
> 随批 D 前定稿写入 `docs/adr/0018-session-execution-and-event-buffer.md`）。

## 1. 背景与动机

当前引擎建立在「一个进程一个前台会话」模型上：`SessionManager`（单例）持有唯一的
`ForegroundConversation` 指针，`BackgroundSessions` 列表保存被切走的会话。单前端（TUI 或单个 WebUI
tab）下该模型成立；出现第二个交互消费面（WebUI 双 tab）时产生**数据损坏级**串写，三个乱源：

1. **查询归属在发起时才解析全局指针**：`QueryStreamEngine.StreamInteractiveAsync`
   （`src/OneCode.App/Query/QueryStreamEngine.cs:152-153`）以
   `sessionId ??= _sessionManager.ForegroundConversation?.Id` 决定用户消息落哪个会话、带哪份历史调 LLM。
   tab 2 切换前台后，tab 1 的下一条消息会写进 tab 2 的会话文件。
2. **旁路事件全局广播**：`RunEventBridge`（`src/OneCode.App/Services/RunEventBridge.cs`）无会话维度，
   任一会话的 Build 进度事件投给全部订阅电路。
3. **交互路由单活跃电路**：`WebInteractionDispatcher`
   （`src/OneCode.WebUI/Services/WebInteractionDispatcher.cs`）只投给最近提交的电路，其余电路的审批/提问/
   预算中断永远等不到，运行悬挂。

附带的共享状态隐患：切会话时 `_tokenUsageTracker.Reset()`（`SessionManager.CreateAsync/ResumeAsync/
SwitchToSessionAsync`）重置的是进程级全局用量；`SessionIdHolder` 亦是全局单点。

无人值守路径不受影响：`unattended` 查询用独立 `SessionId.NewId()`，`StreamWorkflowAsync` 显式携带
`request.SessionId`——这说明引擎已具备显式会话归属的能力，缺的是交互路径的贯通。

## 2. 目标

- 会话归属从「全局前台指针在查询时解析」改为「查询发起方显式携带会话上下文」；前台指针降级为
  **默认会话选择器**（单前端时决定下一个新会话/切换目标），不再是执行归属真相源。
- 每个会话拥有独立的近期事件缓冲（可快照导出）与独立的交互投递目标。
- 会话级执行权互斥（进程内）：一个会话至多一个活跃查询执行者。
- WebUI 双 tab 绑定不同会话可**同时**对话，磁盘转录互不串写。

## 3. 非目标

- 进程内 tab 条（已裁决不做，见 [ADR-0013](../adr/0013-ui-surface-strategy.md)）。
- 跨终端 attach 与跨进程执行权文件锁（归 [P2b](p2b-sessions-attach.md)；本计划只预留锁接口）。
- TUI 多会话并行 UI（TUI 仍是单前台视图，见 §2 原则；本计划对 TUI 是零行为变化的重构）。

## 4. 任务分解

### 批 A · 查询路径显式会话归属

1. `InteractiveSession`（`src/OneCode.App/Services/InteractiveSession.cs`）增加会话绑定（创建时确定
   `SessionId`；`/new`、`/session switch`、`/close` 后由前端重建或重绑）。
2. `IConversationRunner` 交互入口（`StreamQueryAsync` / `StreamCommandPromptAsync` /
   `StreamResumeWorkflowAsync` / `StreamLoopAsync`）透传 `sessionId`；引擎侧仅在 `sessionId == null`
   时回退 `ForegroundConversation`（TUI 单前端路径的兼容默认）。
3. `SessionManager` 前台指针语义收窄：保留 `EnsureActiveSessionAsync` / `SwitchToSessionAsync` /
   `BackgroundCurrentAndCreateNewAsync` 作为默认会话管理，新增按 id 直达的会话获取（已有
   `GetConversation`）。
4. Token 用量按会话隔离：切会话不再 Reset 进程级跟踪器，用量随会话上下文（`Conversation.TotalUsage`
   已按会话累计，运行中用量归 `DoneEvent`）。
- **验证**：现有单会话全量测试回归零变化；新增契约测试——两个伪造前端各持会话 A/B 交替提交，断言
  用户消息与转录落各自会话文件（真实写入端）。

### 批 B · TranscriptEventBuffer 事件缓冲

5. App 层新增 `TranscriptEventBuffer`：按会话的有界环形缓冲（容量上限可配，默认覆盖最近
   N 条 QueryEvent），按序单生产者写入，支持不可变快照导出（快照点之后的增量另接）。
6. 启用 `BackgroundSession.EventBuffer`（`src/OneCode.App/Session/BackgroundSession.cs:30`，现为
   `Channel<object>?` 预留槽位）——改强类型并接入缓冲。
- **验证**：缓冲契约测试——写入超容量后快照只含最近 N 条且顺序稳定；并发读快照不阻塞写入。

### 批 C · 事件与交互按会话路由

7. `RunEventBridge` 增会话维度：`Subscribe(sessionId, handler)`（或事件带会话 id 由订阅端过滤），
   Build 旁路事件只投给绑定该会话的订阅者。
8. `WebInteractionDispatcher` 改为**电路 ↔ 会话**绑定：注册时声明所属会话，交互请求按目标会话路由；
   「活跃电路抢占」语义仅在同会话多窗口时保留（同会话多 tab 共用一套中断卡片）。
9. TUI 侧 `TuiInteractionDispatcher` 单前端无变化，仅确认接口不破坏。
- **验证**：会话 A 的 Build 事件不出现在会话 B 订阅者；会话 B 的审批只到绑定 B 的电路。

### 批 D · 会话级执行权互斥（进程内）

10. 会话执行锁：提交查询前获取目标会话的执行权，同会话并发提交后者**明确被拒**（返回
    「该会话正在执行中」，不排队、不静默丢弃）；跨进程文件锁的接口在本批定义、实现留 P2b。
- **验证**：反证测试——同会话双提交，后者收到拒绝且前者不受干扰；不同会话互不阻塞。

### 批 E · WebUI 双会话适配

11. `ChatSessionClient` 显式绑定 conversationId：`InitializeAsync` / `SwitchSessionAsync` /
    `NewSessionAsync` / `CloseCurrentAsync` 即重绑，查询与事件订阅全程带显式 id。
12. 补当前缺口：新电路打开时回放前台会话转录（现 `InitializeAsync` 只回放 BuildRun 快照，转录为空）。
13. 双 tab 并行场景打磨：侧栏会话列表标注各会话执行状态（空闲/执行中/待审批）。
- **验证**：手动场景 + 契约测试，见 §5。

### ADR-0018（随批 D 前定稿）

- 执行权互斥模型：持有者定义（提交即持有、Done 释放）、锁粒度（会话级）、拒绝语义与提示文案。
- 事件缓冲快照语义：容量策略、顺序保证、快照一致性边界（快照点 + 增量的衔接）。

## 5. 验收

1. **双会话并行**：WebUI 两个 tab 各绑定一个会话同时对话，结束后两个会话文件各自完整、零交叉
   （按落盘断言，非仅 UI 观察）。
2. **执行互斥**：同会话两个 tab 同时提交，后者收到明确拒绝提示；不同会话互不影响。
3. **旁路隔离**：会话 A 的 Build 进度不出现在会话 B 的活动面板。
4. **单前端零回归**：TUI 单会话全流程（斜杠命令、Plan/Build、审批、Goal）与既有测试全绿；
   `dotnet build` 零新增警告。
5. **契约基线同步**：新增/变更 QueryEvent 交互路径更新 `QueryEventContractTests` 基线与双前端渲染。

## 6. 明确不做

- 会话持久化格式变更（`FileSessionEventStore`/`ISessionStore` 语义不动，多会话只是消费侧模型变化）。
- WebUI 对外网络端点扩展（[ADR-0017](../adr/0017-ui-contract-single-layer.md) 边界不变）。
- 后台会话的 headless 循环执行（属 P2b 的 daemon 宿主工作；本计划后台会话仍只保存状态）。
