# UI 契约单层化与浏览器客户端立项

**状态**: Accepted
**日期**: 2026-10-23
**关联**: [UI 形态策略](./0013-ui-surface-strategy.md)（本决策取代其中「不做 Web UI / 不做 AG-UI 协议端点」两项）、
[TUI 项目边界](./0016-tui-project-boundary.md)（阶段 B 契约下沉在本决策中收口）、
[后台代理派工边界](./0011-background-agents-delegation-boundary.md)（无头宿主复用同一组合根与端口默认实现）

## 语境

UI 与引擎之间曾有两层事件：`QueryEvent`（引擎产出）→ `TuiEvent`（33 个 UI 形状 record）→ 渲染。
第二层由 `TuiEventMapper` 逐 case 投影，且 14 处引擎文件绕过映射器直接构造 `TuiEvent`，形成「引擎反向依赖 UI 形状」。
四类交互（工具审批、用户提问、多问题向导、预算决策）以 `TaskCompletionSource` 内嵌在事件里，使事件流无法被非 TUI 前端消费。

同时需要新增一个浏览器客户端，作为 TUI 之外的第二前端。

评估过直接接入 AG-UI 协议。协议层可以部分进程内使用：`AGUI.Abstractions` 是强类型事件体系
（26 个线缆事件类 + 3 个 `SUBAGENT_*`，仅 state / raw / custom / activity 等开放载荷是 `JsonElement`，带源生成序列化上下文），
`AGUI.Client` 的 `IAGUITransport` 是单方法接口（`IAsyncEnumerable<BaseEvent> SendAsync(RunAgentInput, CancellationToken)`），
官方支持自定义实现，「零 HTTP 直连」成立；`AGUI.Server` 的宿主无关部分只有
`RunAgentInput.ToChatRequestContext(...)` 与 `ChatResponseUpdate` 流上的 `AsAGUIEventStreamAsync(...)` 两个转换 helper——
其端点层（`AddAGUI()` + `MapAGUI(pattern)`）绑死 ASP.NET Core（`IEndpointRouteBuilder` / `TypedResults.ServerSentEvents` /
`SseItem<BaseEvent>`），且托管对象是 `IChatClient` 而非 MAF `AIAgent`。结论仍是不接入，真实理由有三条：

1. **中断语义不一致**：AG-UI 的中断是 `RUN_FINISHED` 携带 interrupts 结束本次 run，客户端再以 `resume[]` 重跑；
   本项目的审批是内联 `await`（`IApprovalBroker` → `IApprovalPromptSink`），改造等于把执行循环做成可挂起可重放。
2. **契约强类型降级**：Goal 计划与预算、Build run 状态、模式进度在协议中没有等价事件，
   只能进 `ACTIVITY_SNAPSHOT`/`ACTIVITY_DELTA` 或 `CustomEvent` 的 `JsonElement` 开放载荷，
   丢掉的正是本项目需要的强类型信息（run 生命周期、`STEP_*`、`STATE_*` 与 `SUBAGENT_*` 另有等价事件，不构成缺口）。
3. **跨进程能力零收益**：threadId、SSE、客户端工具、客户端状态与跨进程安全边界，在「两个前端都与引擎同进程」时全部用不上；
   客户端工具反而引入「声明与结果均不可信」的新攻击面。

.NET 侧有两条 HTTP + SSE 接入形态：MAF 托管包 `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`
（`AddAGUIServer()` + `MapAGUIServer(pattern, AIAgent)`，`1.25.0-preview`）与 `AGUI.Server` 自建
（`AddAGUI()` + `MapAGUI(pattern)`，入参 `IChatClient`）。协议类型已从 MAF 拆出到官方 `AGUI.*` SDK
（`AGUI.Abstractions` 稳定 `1.0.1`）。官方没有「`AIAgent` → `IAGUITransport`」的进程内桥，
`AGUIChatClient` 是 `IChatClient` 实现，方向与之相反。唯一独有收益是「未来可直连第三方 AG-UI 客户端」，
与「不暴露网络端点」的定位矛盾。

## 决策

1. **`QueryEvent` 是唯一的 UI 契约**。引擎只产出 UI 无关的强类型事件；`TuiEvent` 层级与 `TuiEventMapper` 删除。
2. **交互走 Core 端口，事件不携带等待句柄**。提问沿用 `IUserQuestionService`，预算决策用 `IBudgetDecisionService`；
   审批拆成两层：`IApprovalBroker` 留在 App，持有 run 级放行状态与 fail-closed 分支，
   其「怎么问用户」外置为 `IApprovalPromptSink`。
   交互式实现在 App 统一一份（`Services/Interaction/`）：请求 record、等待 / 取消 / 无接收者降级只有一处，
   前端只实现 `IInteractionDispatcher`（把请求投递到界面），新增前端因此不再复制端口实现。
   无界面宿主使用 App 的默认实现（拒绝 / 暂停 / 空结果）。`TaskCompletionSource` 只存在于 App 的交互请求 record 内部。
3. **非运行通知不经事件流**。MCP 连接变化与 LSP 诊断变化发生在运行之外，前端直接订阅源事件刷新状态。
4. **立项 `OneCode.WebUI`**：Blazor Server（`Microsoft.NET.Sdk.Web`），与 TUI 同为 `OneCode.App` 的对等消费端。
   首版范围：单会话对话、工具卡片、审批 / 提问 / 预算三类中断卡片。会话切换、多问题向导、命令面板与 MCP 配置页
   属后续阶段；首版的多问题向导直接返回取消，由工具按「用户未回答」降级。
5. **组合根复用**：抽 `AddOneCodeCore(IServiceCollection services, string workingDir, Action<IServiceCollection>? configureFrontend)`
   纯注册入口；WebUI 用 `WebApplicationBuilder.Services` 调用它，无头宿主与两个前端共用同一套服务图。
6. **不引入 AG-UI 协议**。重启判据：出现进程外消费端（第三方 AG-UI 客户端、桌面壳、远程 attach）时才重新评估。
   届时在宿主层加投影即可：`QueryEvent` → `BaseEvent` 投影与 `IAGUITransport` 进程内实现**都必须自写**，
   官方只给协议类型与客户端/服务端适配，没有「引擎 → transport」的现成桥；两者都不改引擎与两个前端。
   需要真正的 HTTP 端点时用 `AGUI.Server` 自建（`AddAGUI()` + `MapAGUI(pattern)`，`RunAgentInput` 进、AG-UI 事件出），
   但它托管 `IChatClient`，须先为引擎写 `IChatClient` 适配器；或改用 MAF 的
   `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`（`1.25.0-preview`，托管 `AIAgent`，需把 MAF 从 `1.24.0`
   升到 `1.25.0` 以上）。

## 边界与禁令

1. `OneCode.App` 不得引用 `Terminal.Gui`，也不得引用 `OneCode.Tui`。
2. `OneCode.WebUI` 不得引用 `OneCode.Tui`；两个前端之间的唯一共享物是 `OneCode.App` 的契约类型。
3. **不暴露 AG-UI 端点，也不暴露任何对外的命令端点**。WebUI 的浏览器通道是 Blazor SignalR，仅绑定 loopback。
4. 对话内容一律按不可信文本处理：Markdown 渲染禁用原始 HTML；不得把消息正文写入日志。
5. 事件契约新增子类型时，必须同时补充前端渲染与穷尽性测试；不得以「未知事件静默丢弃」兜底。
6. 交互端口实现必须在取消时移除等待项，重复应答为 no-op，不得悬挂调用方。

## 后果

- **正面**：UI 契约从两层降为一层，删除 33 个事件类型、1 个映射器、1 个旁路桥与四层审批类型转换；
  引擎不再依赖任何 UI 形状；两个前端消费同一条事件流，新增前端不再需要复制映射逻辑。
- **负面**：`QueryEvent` 家族（现 25 个子类型）成为公共契约，改动需同时评估两个前端；
  TUI 侧两处事件分发（转写过滤器与会话分发）需按新契约重写，一次性成本集中在迁移期。
- **中性**：TUI 用户感知无变化；WebUI 是新增可见面，其样式与交互独立演进，不反向影响 TUI。
