# OneCode.Cli 项目约束

> 本文件为 CLI 项目的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

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
   └── 实现见 CliWorkingDirectory；目录不存在/缺参时以退出码 2 终止

1. Fast-path 检测（零 DI 加载）
   └── --version / -v / -V（且必须是唯一参数）→ 直接返回
   └── 实现见 CliModeDetector + FastPathDispatcher

2. OneCodeApp 执行（默认 FullCli 路径）
   └── OneCodeApp.Create(args) 构建 DI 容器并启动交互式 TUI（REPL）
   └── mcp / skills / install / upgrade 等入口都是 TUI slash 命令，不是 CLI 子命令
```

- Fast-path 检测在 DI 容器初始化之前执行，不得依赖任何服务（实现见 `CliModeDetector`）
- `CliMode` 只有 `FastPathVersion` 与 `FullCli` 两个取值；新增快路径参数必须同时改枚举、
  `CliModeDetector.Detect` 与 `FastPathDispatcher.DispatchAsync` 三处
- 入口参数解析是自写的（`CliWorkingDirectory.Parse` / `CliModeDetector.Detect`），不引入解析框架
- `OneCodeApp` 负责构建 DI 容器并执行

---

## 构建与发布

```bash
# 开发构建
dotnet build src/OneCode.Cli/OneCode.Cli.csproj

# 运行 CLI（--version 是唯一快路径参数，其余参数进入交互式 TUI）
dotnet run --project src/OneCode.Cli/OneCode.Cli.csproj -- --version
```
