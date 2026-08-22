# OneCode Web Host 重构计划

> 状态：计划稿（未实施）。目标形态参照 deepseek harness 本地控制台：`onecode web` 启动本地服务，浏览器访问 `http://localhost:xxxx` 用网页完整替代 TUI 执行全部任务。
> 技术栈约束：纯 C# 生态，UI 框架选用 **Blazor Server**（前后端均为 C#，无 Node.js 工具链；SignalR 电路由框架托管）。选型对比见 §8。
> 关联文档：`docs/keyboard-first-refactor-plan.md`（TUI 键盘化计划，与本计划并行不冲突）。

## 1. 目标与非目标

### 1.1 目标
1. 新命令 `onecode web [--port N] [--no-browser]`：启动 ASP.NET Core Kestrel 本地服务，不进入 TUI。
2. 网页 UI 覆盖 TUI 核心能力：多轮对话流式输出、工具调用可视化、权限审批、Build 门禁（澄清/计划卡审批）、四工作模式切换、会话管理、设置页（服务商/模型/权限等，复用 ConfigManager）、Token 用量展示。
3. 多浏览器标签页可同时连接同一会话，断线重连后补发事件（复用 `BackgroundSession.EventBuffer` 思路）。
4. 业务核心（`QueryStreamEngine`/`ChatService`/工具/权限/Build 门禁）零改动或最小改动即可被双宿主（TUI + Web）复用。
5. 自包含单文件发布约束保持：静态资源嵌入程序集（EmbeddedFileProvider）。

### 1.2 非目标（首期）
- 斜杠命令全量覆盖（42 个）：首期只协议化高频命令（/mode、/model、/session、/compact、/clear、/files），其余分批迁移。
- 远程访问/多用户鉴权：仅绑定 localhost，单用户单实例。
- 前端 JS 框架化（React/Vue）：不采用——Blazor Server 下 UI 全部为 Razor 组件，不引入任何 JS 构建链。

## 2. 现状评估（调研结论）

### 2.1 可复用资产（Web 化的有利条件）
| 资产 | 位置 | 复用方式 |
|---|---|---|
| `IAsyncEnumerable<QueryEvent>` 流式契约 | `src/OneCode.App/Query/ChatService.cs` | 已 public；Web 层直接消费并映射 JSON DTO |
| QueryEvent 事件体系（16 种） | `src/OneCode.App/Query/QueryEventTypes.cs` | 已 public、UI 无关；仅 `ApprovalRequestEvent` 含 TCS 需特殊处理 |
| DI 注册链 `Register*` 扩展 | `src/OneCode.App/ClaudeCodeApp.cs` L45-56 | 全部 public 扩展方法，Web Host 直接复用，仅替换 `RegisterInteractiveServices`（TUI 专属） |
| 组合根模式 | `OneCodeApp.Create` | Web Host 仿写 `WebAppHost.Create`，复用同一注册链 |
| Headless 先例 | CronJobExecutor 依赖 `IConversationRunner` 接口 | 证明业务核心可脱离 TUI 运行 |
| CLI 快速路径分发 | `src/OneCode.Cli/CliModeDetector.cs` + `FastPathDispatcher.cs` | 已有模式：新增 `CliMode.WebHost` 即可接入 |
| 事件缓冲重放 | `BackgroundSession.EventBuffer`（`Channel<object>`） | 仅参考其缓冲思路；落地为有界环形缓冲 + 快照导出（`TranscriptEventBuffer`，见 §3.3/§3.7） |
| 快捷键体系 | `src/OneCode.Core/Keybindings/`（Resolver/Parser/ContextManager 等，UI 无关） | Web 端 Blazor `onkeydown` 走同一 `KeybindingResolver`；`keybindings.json` 双宿主共用（见 §3.6） |

### 2.2 耦合障碍（必须改造项）
| # | 障碍 | 位置 | 改造方向 |
|---|---|---|---|
| O1 | `using OneCode.App.Tui` 命名空间引用 | `ChatService.cs` L3、`QueryStreamEngine.cs` L9 | 核实具体引用类型，迁移到 `OneCode.App.Query` 或 UI 无关层 |
| O2 | 审批应答经 `TaskCompletionSource<ApprovalDecision>` 回传 | `QueryEventTypes.cs` L100 | Blazor 电路内拦截该事件：TCS 存入待决表 → 审批组件渲染 → 用户点击 → `TrySetResult`。TCS 本身 UI 无关，无需改动 |
| O3 | 交互体系绑定 Terminal.Gui `Key` | `src/OneCode.App/Tui/IInteractionSession.cs` | 不改动 TUI；Web 侧新建并行的 UI 无关交互协议（见 §3.2） |
| O4 | 斜杠命令直接操作 TUI 视图 | `src/OneCode.App/Commands/` | 命令分层：核心逻辑已抽 `CommandService` 的可协议化，仅渲染耦合 TUI |
| O5 | `RegisterInteractiveServices` 注册 TUI 栈 | `ServiceCollectionExtensions.*.cs` | 拆分为「交互无关」+「TUI 专属」两段，Web Host 只注册前者 + Web 实现 |
| O6 | 「全屏 TUI 是唯一交互式 UI」表述 | `src/AGENTS.md` / `README_CN.md` | Web Host 落地后同步修订文档 |

## 3. 架构设计

### 3.1 分层与依赖方向（严格遵守现有规范）

```
OneCode.Core          ← 纯抽象（零改动）
OneCode.Infrastructure
OneCode.Automation
OneCode.App           ← 业务核心 + TUI（拆出 UI 无关交互协议）
OneCode.Web  (新增)   → 依赖 App 及以下；实现 Web 交互协议 + ASP.NET Core 宿主
OneCode.Cli           → 依赖 App + Web；`onecode web` 分发入口
```

- 新项目 `src/OneCode.Web/`：net10.0，类库 + 内嵌 wwwroot 资源；CLI 引用它并启动 Kestrel。
- 依赖方向不变（Web 在 App 之上、Cli 之下），不引入反向依赖。
- 包引用仅新增：`Microsoft.AspNetCore.App`（框架引用 FrameworkReference，非 NuGet 包）、`System.Text.Json`（已在传递依赖）。

### 3.2 UI 无关交互协议（App 层新增 `OneCode.App/Interaction/`）

TUI 的 `IInteractionSession` 保持不动；为 Web 定义并行的抽象（接口 ≤5 成员规范）：

```csharp
// OneCode.App/Interaction/IInteractionProtocol.cs —— UI 无关交互会话
public interface IInteractionProtocol
{
    ValueTask<InteractionRequest?> NextRequestAsync(CancellationToken ct);   // 引擎→UI：待决交互（提问/选择/计划卡）
    ValueTask SubmitResponseAsync(string requestId, InteractionResponse response, CancellationToken ct); // UI→引擎：应答
}
```

- `InteractionRequest`（record）：问题列表 / 单选多选 / 计划卡审批，纯数据模型，可 JSON 序列化。
- `ApprovalRequestEvent` 链路不动：Web 层订阅事件流，收到后推送浏览器并异步 `SetResult`。
- Build 门禁的 `ClarificationInteraction`/`PlanCardPublisher` 现为回调注入，天然可换 Web 后端（构造注入 Web 实现）。
- **职责边界**（避免双通道重叠）：`IInteractionProtocol` 只承载**不走 QueryEvent 流**的交互——Build 门禁的澄清提问与计划卡审批；权限审批一律走 `ApprovalRequestEvent` + TCS 待决表。同一交互绝不允许同时出现在两条通道中，实现时以「事件流中已有对应 Event 类型则不进 Protocol」为准绳。

### 3.3 Web 架构设计（OneCode.Web，Blazor Server）

REST（控制面，Minimal API）+ Blazor Server 电路（UI 数据面，框架托管 SignalR）：

| 端点 | 方法 | 说明 |
|---|---|---|
| `/` | GET | Blazor 页面入口（App.razor / MainLayout），静态资源由框架 + EmbeddedFileProvider 提供 |
| `/api/sessions` | GET/POST | 会话列表 / 新建会话（复用 SessionManager） |
| `/api/sessions/{id}/messages` | GET | 历史消息（重连补发，事件缓冲快照） |
| `/api/models` / `/api/modes` | GET | 模型目录 / 工作模式 |
| `/api/settings` | GET/PUT | 读取/保存配置（复用 ConfigManager，用户/项目双作用域，只提交变更字段） |

**核心组件设计**（Razor 组件直接消费事件流，无需手写 JSON 协议层）：

- `WebSessionService`（singleton，会话级）+ 电路级订阅：会话由 singleton 管理并持有**唯一 pump 任务**消费 `ChatService.StreamQueryAsync`（所有权与互斥见 §3.7），事件写入会话级 `TranscriptEventBuffer` 并广播；各电路把事件套用到电路内 `SessionViewModel` 后触发 `StateHasChanged`。
- `SessionViewModel`：消息列表、流式文本缓冲、工具调用卡片、审批卡片、用量条——与 TUI MessageListView 同构的视图模型，纯 C# 可单测。
- 审批链路：`ApprovalRequestEvent` 到达时把 TCS 存入**会话级**待决表并渲染审批组件；用户点击「允许/拒绝」后组件调用 `tcs.TrySetResult(decision)`（待决表挂会话而非电路，跨标签可应答，见 §3.7）。
- 多标签同步：`WebSessionService` 订阅会话级广播（每电路一份订阅，复用会话级事件广播服务）；断线重连后从环形缓冲快照重建 `SessionViewModel`。
- **事件重放存储**：`BackgroundSession.EventBuffer` 是 `Channel<object>`，消费即移除、不能直接支撑补发。App 层新增 `TranscriptEventBuffer`（有界环形缓冲，按序保存近期 `QueryEvent` 并支持不可变快照导出），会话级单生产者写入；重连时从快照全量重建视图模型。
- Markdown/代码高亮：首期用轻量 JS 互操作调用 marked.js/highlight.js（内嵌静态副本）；不引入构建链。

**与 JSON 协议方案的关键差异**：不需要 `WebEventMapper`/`{type, seq, payload}` 信封/SignalR Hub 方法——QueryEvent 直接流入 C# 视图模型，序列化边界消失。

### 3.6 双宿主共享设计（与 keyboard-first 计划协同）

**共享 TranscriptViewModel（App 层新增 `OneCode.App/Transcript/`）**：
- Web 的 `SessionViewModel` 与 TUI 的可交互行体系（`ToolLineTag`/`ThinkingLineTag`/`ErrorLineTag`/`CodeBlockCopyTag`）本质同构。共享视图模型 `TranscriptViewModel` 已由 keyboard-first Phase 1 在 App 层创建（消息列表、可交互块标记、展开状态、游标位置），TUI 的 `MessageListView` 与 Web 的 Razor 组件均消费它。
- 收益：交互逻辑（展开/折叠/复制/审批状态迁移）单测一次覆盖两端；为第三宿主（移动端/IDE 插件）预留统一状态层。
- 落点：`TranscriptViewModel` 由 keyboard-first Phase 1 **直接创建**（游标导航、展开状态即落于此），Web Phase 2 仅做消费接入，不做二次抽取迁移。

**keybindings.json 双宿主共用**：
- `KeybindingResolver`/`KeybindingParser`/`KeybindingContextManager` 均在 Core 层、UI 无关。Web 端 Blazor `onkeydown`（JS 互操作转按键字符串）同样经 Resolver 解析，网页拥有与 TUI 一致的 Ctrl+T 导航模式与可配置快捷键。
- 绑定配置经 `GET /api/keybindings` 下发给页面（Phase 4 接入）；`keybindings.json` 修改热重载后两端同步生效。
- 约束：浏览器保留键（Ctrl+T 新标签页、Ctrl+W 关标签等）不可用。`KeybindingDefaults` 提供**宿主感知默认集**（TUI：ctrl+t；Web：alt+t），用户 `keybindings.json` 覆盖优先于宿主默认；`KeybindingValidator` 增加「Web 保留键」校验集并结合当前宿主校验（见 keyboard-first-refactor-plan §2.3）。

### 3.7 会话所有权与并发模型（多标签语义）

多标签连接下的并发是本计划最大不确定点，模型定义如下：

- **会话所有权**：会话由 `WebSessionService`（singleton）持有的**会话级单生产者**管理，电路（标签页）只是订阅者。任一时刻一个会话至多有一个活跃查询在执行；查询的发起权归属「当前持有输入焦点的标签页」——后端以会话级互斥保证：已有活跃查询时，其他标签页的提交返回 409 并提示「该会话正在执行中」（首期不做排队）。
- **广播**：查询事件由单生产者写入 `TranscriptEventBuffer`，各电路独立消费快照/增量，标签页之间只读同步（历史与流式输出），不出现双电路各自 pump 同一会话导致重复执行的情况。
- **审批归属**：审批 TCS 挂在会话级待决表而非电路级——发起审批的电路断连后，其他标签页仍可应答；全部断连才走超时默认拒绝兜底。
- **输入冲突**：多标签同时输入不合并；以提交到达顺序为准，输者在 UI 收到「已被其他标签页抢先」提示。

### 3.4 `onecode web` 命令接入

```
CliModeDetector.Detect:  args[0] == "web"  → CliMode.WebHost
FastPathDispatcher:      CliMode.WebHost   → WebHostRunner.RunAsync(args)   // OneCode.Web 提供
```

- 默认端口：随机空闲端口（`TcpListener` 探测）+ `--port N` 覆盖；启动后打印 URL 并尝试打开浏览器（`--no-browser` 跳过）。
- Ctrl+C 优雅关停（ExistingHost.StopAsync）。

### 3.5 静态资源与发布

- `src/OneCode.Web/wwwroot/`：Blazor 静态资源（app.css、marked.js/highlight.js 内嵌副本、favicon）；Razor 组件编译进程序集。
- csproj：`<EmbeddedResource Include="wwwroot\**" />`，运行时 `EmbeddedFileProvider` 提供，保证单文件发布可用。

## 4. 分阶段实施计划

### Phase 0：解耦准备（App 层内，无新项目）
**改动**：
- O1：核实并移除 `ChatService.cs`/`QueryStreamEngine.cs` 的 `using OneCode.App.Tui`（迁移被引用类型或改用完全限定）。
- O5：`RegisterInteractiveServices` 拆分为 `RegisterInteractionCore`（UI 无关）+ `RegisterTuiServices`（Terminal.Gui 栈）；`OneCodeApp.Create` 串联二者（对外行为不变）。
**验收**：现有全部测试通过；`dotnet build` 零警告（TreatWarningsAsErrors）。

### Phase 1：Web 项目骨架 + 命令接入
**改动**：
- 新建 `src/OneCode.Web/`（类库，FrameworkReference Microsoft.AspNetCore.App）；加入 `OneCode.slnx` 与 `Directory.Build.props` 管辖。
- `WebAppHost.Create/RunAsync`：仿 `OneCodeApp.Create` 复用注册链（替换 `RegisterTuiServices`）；Kestrel 仅绑定 127.0.0.1；静态首页。
- **本地访问令牌**：启动时生成随机 token，拼接进打印的 URL（`http://127.0.0.1:xxxx/?token=...`）；中间件校验 token（或首请求种入 HttpOnly Cookie），未携带 token 的请求一律 403——防止本机其他进程/用户无授权读写 `/api/settings`（含 API Key）与审批端点。`--no-token` 显式关闭（仅调试用）。
- CLI：`CliMode.WebHost` + `WebHostRunner` 分发；端口探测与浏览器打开。
- **发布体积 spike**：实测 `dotnet publish` 单文件（框架依赖 / 自包含两种模式）引入 `FrameworkReference AspNetCore.App` 后的体积增量，回填 Phase 5 验收阈值。
**验收**：`onecode web` 启动后浏览器可打开首页；Ctrl+C 干净退出；无 token 请求被拒；TUI 路径回归不受影响。

### Phase 2：会话与流式对话（数据面打通）
**改动**：
- Blazor 组件骨架：`App.razor` / `MainLayout` / `ChatPage.razor`（消息流 + 输入框 + 取消按钮 + 用量条）。
- 接入 App 层共享 `TranscriptViewModel`（§3.6，已由 keyboard-first Phase 1 创建）：QueryEvent 流 → 共享视图模型；TUI `MessageListView` 已是同一模型的消费方，两端交互逻辑单测一次覆盖。
- `WebSessionService`（singleton + 电路级订阅，§3.7）：会话级唯一 pump 消费 `ChatService.StreamQueryAsync` → 广播至各电路 → `SessionViewModel` → `StateHasChanged`。
- REST：sessions / models / modes；会话级事件广播（多标签 + 重连补发）。
**验收**：网页完成一轮完整 BUILD 对话（含工具调用可视化）；刷新页面不丢历史；两个标签页实时同步。

### Phase 3：审批与交互协议（核心难点）
**改动**：
- 审批组件 `ApprovalCard.razor`：电路内待决表（RequestId → TCS），点击后 `TrySetResult`；展示 9 种 PermissionMode。
- `OneCode.App/Interaction/`：`IInteractionProtocol` + `InteractionRequest/Response` record。
- Build 门禁 Web 化：澄清提问、计划卡审批的 Web 后端实现（`PlanCard.razor`，替换 TUI `PlanCardPublisher` 注入）。
**验收**：网页触发文件写入工具 → 审批卡弹出 → 批准后工具执行；Build 模式下澄清与计划审批全流程网页完成。

### Phase 4：命令覆盖与模式切换
**改动**：
- `CommandBar.razor`：斜杠命令输入（协议化 /mode、/model、/session、/compact、/clear、/files，复用 CommandService）。
- Web 键盘接入（§3.6）：`onkeydown` → JS 互操作 → `KeybindingResolver`；`GET /api/keybindings` 下发绑定；`KeybindingValidator` 增加 Web 保留键校验集。
- 四工作模式（BUILD/PLAN/TEAM/GOAL）切换 UI 与权限模式联动。
- 设置页 `SettingsPage.razor`：复用 `ConfigManager`/`AppSettings`（Infrastructure 层、UI 无关）。表单字段与 TUI `SettingsOverlay` 对齐：编辑范围（用户/项目）、服务商（anthropic/openai/ollama）、BaseUrl、API Key（密码框）、模型/快速模型、思考与思考显示开关、通知开关、MaxTurns、Effort；保存走 `PUT /api/settings`，沿用「只提交用户实际修改的字段」策略，避免把继承值复制到目标作用域；保存后热生效（ConfigManager 热重载）。
**验收**：6 个高频命令网页可用；模式切换后工具权限行为与 TUI 一致；设置页可读/改/存配置且与 TUI `/config` 改动互通（同一 ConfigManager 状态）。

### Phase 5：打磨与文档
**改动**：`src/AGENTS.md`/`README_CN.md` 修订（O6：双宿主表述、禁造轮子表补 ASP.NET Core 条目）；`docs/commands.md` 补 Web 命令；冒烟脚本 `scripts/smoke-test-release.ps1` 增加 `onecode web` 用例；暗色主题与移动端可用性。
**验收**：门禁全绿（build/test/format/秒级启动）；单文件发布体积增量 ≤ Phase 1 spike 实测回填的阈值（首版预估框架依赖模式 < 3MB，自包含模式另行评估）。

## 5. 变更文件清单（预估）

| 层 | 文件 | 动作 |
|---|---|---|
| App | `Query/ChatService.cs`、`Query/QueryStreamEngine.cs` | 移除 Tui using（O1） |
| App | `ServiceCollectionExtensions.*.cs` | 拆分交互注册（O5） |
| App | `Interaction/`（新目录） | IInteractionProtocol + 数据模型 |
| App | `Transcript/`（keyboard-first Phase 1 已建） | 共享 TranscriptViewModel（双宿主状态层，§3.6）+ TranscriptEventBuffer（环形缓冲/快照，§3.3/§3.7） |
| Web | `OneCode.Web.csproj`、`WebAppHost.cs`、`WebSessionService.cs`、`SessionViewModel.cs`、`Components/*.razor`、`wwwroot/*` | 新增 |
| Cli | `CliModeDetector.cs`、`FastPathDispatcher.cs`、`WebHostRunner.cs` | 扩展 |
| Tests | `SessionViewModelTests`、`WebSessionServiceTests`、`WebHostIntegrationTests`（bUnit 组件测试） | 新增 |

## 6. 风险与对策

| 风险 | 对策 |
|---|---|
| 审批 TCS 泄漏（浏览器关闭未应答） | 待决表挂超时（默认拒绝）+ 连接断开事件兜底 `TrySetCanceled` |
| 多标签并发提交审批/应答 | RequestId 幂等：首次 `TrySetResult` 生效，其余丢弃并提示 |
| 多标签并发发起查询导致重复执行 | 会话级单生产者 + 会话级互斥，冲突提交返回 409（§3.7） |
| 发起审批的电路断连后无人应答 | 审批 TCS 挂会话级待决表，其他标签页可应答；全部断连才超时默认拒绝（§3.7） |
| 事件顺序在多电路广播中乱序 | 会话级单生产者（ChatService 事件流）+ 每电路独立消费，事件缓冲快照按序重建 |
| Blazor 电路内存占用（每标签页常驻） | 电路空闲超时回收（默认可调）；本地单用户场景标签数有限，可接受 |
| 电路断连导致审批 TCS 悬挂 | 待决表挂超时（默认拒绝）+ 电路 Closed 事件兜底 `TrySetCanceled` |
| Markdown/高亮依赖 JS 互操作 | JS 仅限隔离脚本（marked/highlight 内嵌副本），不引入构建链；后续可评估纯 C# 渲染库（如 Markdig） |
| 单文件发布遗漏静态资源 | EmbeddedResource + smoke-test-release.ps1 断言首页可达 |
| 与 keyboard-first 计划冲突 | 二者改动面不重叠（TUI 视图层 vs Query/宿主层）；Phase 0 的 O1/O5 与键盘计划 G 项无交集，可并行 |

## 7. 与 keyboard-first 计划的优先级建议

- 先行 Phase 0（解耦准备）：它同时是 Web Host 与后续任何宿主的地基，改动小、风险低。
- Phase 1-3（Web 主链路）与 keyboard-first 各 Phase 可交替推进；两者无文件级冲突。
- 建议顺序：Phase 0 → keyboard-first Phase 1（键盘基础）→ Web Phase 1-2 → 视用户使用反馈决定 Phase 3-5 与 TUI 优化的投入比例。

## 8. 技术选型对比：为什么是 Blazor Server

| 维度 | Blazor Server（选定） | Minimal API + SignalR + 原生 JS | Blazor WebAssembly |
|---|---|---|---|
| 前后端语言 | 全 C# | 后端 C#、前端 JS | 全 C# |
| JS/Node 构建链 | 无（JS 仅互操作隔离脚本） | 无，但原生 JS 维护性差、后期易滑向框架化 | 无 |
| QueryEvent 流接入 | 直接绑定组件状态，无序列化协议层 | 需自建 JSON DTO 信封 + Hub 方法 + 前端状态管理 | 需自建 SignalR 客户端 + 序列化协议层 |
| 与 TUI「事件→视图」结构同构性 | 高（组件≈视图、ViewModel≈状态容器） | 低（需翻译成 JS） | 中 |
| 断线重连 | 电路重建（配合事件缓冲快照） | 手写 seq 补发 | 手写补发 |
| 内存/连接成本 | 每电路常驻内存（本地单用户可接受） | 最低 | 下载体积数 MB、首屏慢（本地场景不划算） |
| 单文件发布 | 静态资源内嵌 + 组件编译进程序集 | 静态资源内嵌 | WASM payload 打包 |

**结论**：本地 localhost、单用户、纯 C# 约束下，Blazor Server 的「电路内直接消费 `IAsyncEnumerable<QueryEvent>`」是最大优势——省掉整个 JSON 协议层（WebEventMapper/信封/Hub 方法/前端状态同步），UI 代码与 TUI 一样是 C# 事件驱动模型。其内存与断连代价在本地场景均可控。