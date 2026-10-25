# OneCode.Tui 项目约束

> 本文件为 TUI 项目的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

## 定位

OneCode.Tui 是 OneCode 的终端前端（与 `OneCode.WebUI` 对等）：
进程入口（参数预处理 + 快路径）+ Terminal.Gui v2 全屏 TUI 实现。引擎、命令、工具与服务
全部在 `OneCode.App`，本项目通过 ProjectReference 消费，不反向依赖；两个前端之间零依赖。

运行事件契约是 `QueryEvent`；审批、提问与预算决策走交互端口（`IApprovalPromptSink` /
`IUserQuestionService` / `IBudgetDecisionService`），由 `TuiInteractionDispatcher` 投递到界面线程，
事件流不携带交互等待句柄。

```
OneCode.Tui(Exe) → OneCode.App → OneCode.Infrastructure / OneCode.Automation → OneCode.Core
```

## 入口点规范

### Program.Main 签名

```csharp
// ✅ 正确：返回 Task<int>
public static Task<int> Main(string[] args)
public static async Task<int> Main(string[] args)

// ❌ 禁止：同步入口或 void
public static void Main(string[] args)
public static int Main(string[] args)
```

### 分层启动架构

```
0. --cwd/-C 预处理（Program.Main 最前端，快路径与 DI 之前）
   └── 解析全局工作目录选项并 Directory.SetCurrentDirectory，随后从 args 剥离
   └── 实现见 LaunchWorkingDirectory；目录不存在/缺参时以退出码 2 终止

1. Fast-path 检测（零 DI 加载）
   └── --version / -v / -V（且必须是唯一参数）→ 直接返回
   └── 实现见 LaunchModeDetector + FastPathDispatcher

2. daemon 动词（全量 DI 无头宿主，不进 FastPathDispatcher——快路径语义是绕过 DI）
   └── daemon → DaemonHost.RunAsync（App 层核心组合根，不挂 TUI 注册段）
   └── 内部启动协议：/daemon start 与 OS 调度器注册共用同一条命令，非用户功能入口

3. OneCodeApp 执行（默认 Interactive 路径）
   └── OneCodeApp.Create(args) 构建 DI 容器并启动交互式 TUI（REPL）
   └── mcp / skills / install / upgrade 等入口都是 TUI slash 命令，不是 CLI 子命令
```

- Fast-path 检测在 DI 容器初始化之前执行，不得依赖任何服务（实现见 `LaunchModeDetector`）
- `LaunchMode` 有 `FastPathVersion` / `Daemon` / `Interactive` 三个取值；新增快路径参数必须同时改枚举、
  `LaunchModeDetector.Detect` 与 `FastPathDispatcher.DispatchAsync` 三处（daemon 分支在 `Program.Main`）
- 入口参数解析是自写的（`LaunchWorkingDirectory.Parse` / `LaunchModeDetector.Detect`），不引入解析框架
- `OneCodeApp`（App 组合根）负责构建 DI 容器并执行；TUI 栈注册入口为 `AddTuiServices`

## TUI 栈规范

- **依赖隔离**：`Terminal.Gui` / `Terminal.Gui.Editor` 包引用只允许出现在本项目；App 层零 Terminal.Gui 依赖
- **颜色**：所有颜色通过 `TuiPalette` 语义 token 取值，Terminal.Gui `Scheme` 通过 `TuiStyles` 组合，**禁止硬编码**颜色值
- **状态指示器**：新增全局状态（如沙箱模式 🔒、Plan 模式 📋）须在 `AgentStatusBar` 中注册
- **Terminal.Gui 限制**：不支持字体大小/行间距配置，不支持鼠标精确位置，不支持透明背景
- **设计规范**：颜色 token、网格、组件与交互规则见 [Tui/DESIGN.md](Tui/DESIGN.md)，运行时颜色唯一来源是 `TuiPalette.DesignTokens`
- **文件体量与拆分原则**：~300 行是**参考信号而非硬上限**，内聚优先于行数。超限时的拆分优先级：① 提取**真实类型**（协作者类 / 静态工具类）；② 仅当成员被框架绑定（View 的 `OnDrawingContent` / 按键处理）或强耦合共享私有状态时才用 partial；③ **禁止**为凑行数把无状态方法组或互不相关的功能捆进新 partial 文件。partial 文件名与类名对齐，头注释标明自身职责

## 构建与发布

```bash
# 开发构建
dotnet build src/OneCode.Tui/OneCode.Tui.csproj

# 运行 TUI（--version 是唯一快路径参数，其余参数进入交互式 TUI）
dotnet run --project src/OneCode.Tui/OneCode.Tui.csproj -- --version
```
