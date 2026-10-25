# TUI 项目边界：OneCode.Tui 与 App 契约层

**状态**: Accepted
**日期**: 2026-10-22
**关联**: [UI 形态战略](./0013-ui-surface-strategy.md)（TUI 为默认前端、浏览器客户端立项）、
[TUI 极致化路线图](../plan/tui-roadmap.md)（P0 引擎解耦）、
[后台代理派工边界](./0011-background-agents-delegation-boundary.md)（daemon 复用同一组合根）

## 语境

ADR 0013 定稿 TUI 为主交互前端后，代码形态与命名出现三处错配：

1. **入口项目名与实体不符**：入口项目只有 6 个文件（`Program` + 快路径分发），产品实体却是
   Terminal.Gui v2 全屏 TUI；「CLI」是行式 CLI 时代的遗留叫法。
2. **Terminal.Gui 只靠目录约定隔离**：视图/浮层/渲染器寄生在组合层 `OneCode.App/Tui/` 子目录，
   包引用在 App csproj，且 `TuiGlobalUsings.cs` 的 `global using Terminal.Gui.*` 使 Terminal.Gui 类型
   对**整个 App 编译单元**可见——任何 App 文件都能裸用 `View`/`Color`/`Key`，目录约定不构成约束。
3. **引擎与前端耦合**：流式管道（`QueryStreamService`/`OrchestrationStreamService`）直接产出 `TuiEvent`，
   Plan/Goal 运行时、Build 回放、`UserQuestionService` 等 18 个引擎文件消费 `OneCode.App.Tui` 契约类型，
   使「把 Tui/ 整体搬入独立项目」会形成 `Tui → App → Tui` 项目循环引用。

## 决策

### 1. 入口项目改名：OneCode.Cli → OneCode.Tui

项目目录、csproj、`AssemblyName`/`RootNamespace`、命名空间、解决方案、测试引用、构建/安装脚本、CI 工作流、
发布产物名（`OneCode.Tui(.exe)`）与文档引用全部同步。入口职责不变：`--cwd/-C` 预处理 → 快路径（`--version`）→
`OneCodeApp` 交互启动。

### 2. 项目边界：实现入 Tui，契约留 App

| 归属 | 内容 |
|---|---|
| `OneCode.Tui`（Exe） | 进程入口 + 快路径分发；Terminal.Gui 视图/控件/浮层/渲染器/样式（`Tui/`）；TUI 装配件（`TuiHost`/`TuiHostConfigurator`/`InteractiveModeExecutor`/`InteractiveBootstrapService`/`TuiContextFactory`/`Tui*Dependencies`）；`DESIGN.md` |
| `OneCode.App` | 引擎与业务（Streaming/PlanMode/GoalMode/Agent/Loop/Mcp/Tools/Commands/Session）；**UI 契约层**：`QueryEvent` 事件层级、`PlanCardTypes`/`SlashCommandEntry`/`McpConfigTypes` 等纯 DTO、交互端口与 App 侧统一实现（零 Terminal.Gui）。阶段 B 后 `Tui/` 契约层已删除，最终形态见 [UI 契约单层化](./0017-ui-contract-single-layer.md) |

依赖方向：`OneCode.Tui → OneCode.App → OneCode.Infrastructure / OneCode.Automation → OneCode.Core`。
`Terminal.Gui`、`Terminal.Gui.Editor`、`Markdig` 包引用只存在于 `OneCode.Tui`；App 侧 `TuiGlobalUsings.cs` 随视图迁出。

### 3. 定类规则：按引用定类

一个文件能否迁入 `OneCode.Tui`，唯一判据是「谁引用它」而非「它看起来像 UI」：

- 只被前端代码或测试消费 → 迁入（命名空间随项目归属调整为 `OneCode.Tui`）
- 被任何留 App 的类型消费 → 留下（契约类型归入 `OneCode.App.Query`、`OneCode.App.Services.Interaction` 等 App 命名空间）
- 迁走集必须在引用上自洽封闭：任何留 App 的文件不得引用迁走类型（否则 App → Tui 反向依赖）
- XML doc 的 `<see cref>` 是编译期依赖（`GenerateDocumentationFile=true`），留 App 文件中对迁走类型的
  cref 一律改为 `<c>` 纯文本

### 4. DI 按领域归属拆分

| 注册入口 | 位置 | 内容 |
|---|---|---|
| `AddStreamingServices` | `OneCode.App/Services/Streaming/StreamingServiceCollectionExtensions.cs` | UI 无关：流式管道、Build 回放、`RunEventBridge` |
| `AddAppRuntimeServices` | `OneCode.App/Services/AppRuntimeServiceCollectionExtensions.cs` | 斜杠命令管线、模式桥、`AppStartupService` |
| `AddInteractionCoreServices` | `OneCode.App/Services/Interaction/InteractionCoreServiceCollectionExtensions.cs` | 交互端口默认实现：无头 dispatcher 与三个 Core 端口的交互式实现 |
| `ReplaceInteractionDispatcher` | `OneCode.App/Services/Interaction/InteractionServiceCollectionExtensions.cs` | 前端交互投递通道：替换 `IInteractionDispatcher` |
| `AddTuiServices` | `OneCode.Tui/Tui/TuiServiceCollectionExtensions.cs` | Terminal.Gui 栈与交互装配件、`StartupFlowCoordinator`/`TrustService`、`InteractiveShellRunner → InteractiveModeExecutor.ExecuteAsync`、`ReplaceInteractionDispatcher<TuiInteractionDispatcher>` |

组合根 `OneCodeApp.Create(args, Action<IServiceCollection>? configureTuiStack)` 在核心注册之后调用回调；
`RunAsync` 经 App 侧 `InteractiveShellRunner` 委托驱动前端——App 因此不需要引用 `OneCode.Tui`。
无头宿主（daemon / attach）注册核心服务后自行驱动引擎，不注册该委托、也不走 `RunAsync`。

## 否决的方案

| 方案 | 否决原因 |
|---|---|
| 将 `OneCode.App/Tui/` 整体搬入 `OneCode.Tui` | 18 个引擎文件消费 `TuiEvent` 等契约 → `Tui → App → Tui` 项目循环引用，无法编译 |
| 契约整体先下沉 `OneCode.Core` | `TuiEvent` 依赖 `OneCode.App.Query.SubagentProgressPhase`；审批/提问事件内嵌 `TaskCompletionSource` 回传，属交互端口而非领域契约。下沉需先完成流式管道 QueryEvent 化（见路线图 §3.2） |
| 保留旧入口项目名，仅把 TUI 实现移入 | 入口项目与 TUI 实现仍分居两个项目，命名与实体继续错配；且 `OneCode.Tui` 项目名将无处安放 |
| 用 `InternalsVisibleTo("OneCode.Tui")` 让 App 的 internal 类型跨程序集可见 | 用程序集可见性换编译便利，破坏边界语义。实际只需将跨程序集消费的常量类（`GoalLoopDefaults`）改 `public` |

## 后果

- **Terminal.Gui 被项目边界强制隔离**：App 中任何 Terminal.Gui 类型使用都会编译失败；包引用、global using、
  `using Terminal.Gui` 三层 grep 均零命中。
- **DI 注册基线重排一次**：`ServiceRegistrationSnapshot.approved.txt` 按「核心块 + TUI 块」重生成并人工审查，
  重排登记项一条不少（`StartupFlowCoordinator`/`TrustService` 从 `AddSetupServices` 移入 TUI 块；
  条目总数随后续立项自然增长，不在此写死）。
- **跨程序集可见性收敛**：仅 `GoalLoopDefaults`（子目标轮次常量，`TodoStripView` 渲染消费）由 `internal` 提升为 `public`。
- **阶段 B 已完成**：引擎侧不再产出 UI 形状事件，流式管道与 Goal/Build/Loop 全部改产 `QueryEvent`；
  交互收敛为 Core 端口；`TuiEvent` 层级、映射器与旁路桥删除，`OneCode.App/Tui/` 目录不再存在。
  契约与端口的最终形态见 [UI 契约单层化](./0017-ui-contract-single-layer.md)。
- **新入口命令**：`dotnet run --project src/OneCode.Tui/OneCode.Tui.csproj`；发布产物 `OneCode.Tui(.exe)`，
  安装脚本仍将其重命名为 `onecode`。
