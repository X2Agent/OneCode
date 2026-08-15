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
   └── --version / ps / logs / kill → 直接返回

2. System.CommandLine 解析
   └── 参数 → CliInvocation 强类型描述
   └── --dump-system-prompt → 创建 DI 容器，组装系统 prompt 并打印后退出

3. ClaudeCodeApp 执行
   └── REPL / auth / mcp / skills
```

- Fast-path 检测在 DI 容器初始化之前执行，不得依赖任何服务（实现见 `CliModeDetector`）
- `--dump-system-prompt` 需要完整 DI（PromptConfigBuilder / Memory / Context），因此在 FullCli 路径中处理，不走 Fast-path
- `CliInvocation` 是纯数据 record，不包含逻辑
- `ClaudeCodeApp` 负责构建 DI 容器并执行

### System.CommandLine 用法

- 使用 `System.CommandLine` v2 API（`RootCommand`、`Option<T>`、`Argument<T>`）
- 命令定义在 `BuildRootCommand()` 中，保持集中管理
- 子命令（mcp / skills / update）各自有独立的 `Build*Command()` 方法
- 所有异步回调必须传递 `CancellationToken`

---

## 构建与发布

```bash
# 开发构建
dotnet build src/OneCode.Cli/OneCode.Cli.csproj

# 运行 CLI
dotnet run --project src/OneCode.Cli/OneCode.Cli.csproj -- --help
```
