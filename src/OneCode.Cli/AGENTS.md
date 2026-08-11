# OneCode.Cli 项目约束

> 本文件为 CLI 项目的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

## AOT 发布约束（最高优先级）

本项目启用 `PublishTrimmed` + `EnableAotAnalyzer` + `TrimMode=partial`，所有代码必须 AOT 兼容。

### 禁止使用的 API

| 禁止项 | 原因 | 替代方案 |
|--------|------|---------|
| `Type.GetProperty()` / `Type.GetField()` | 反射在 trim 后不可用 | 源生成器 |
| `Activator.CreateInstance()` | 反射实例化 | 工厂模式 / 源生成器 |
| `dynamic` 关键字 | 运行时动态分派 | 强类型 + 泛型 |
| `Newtonsoft.Json` | 反射密集型 | `System.Text.Json` + `JsonSerializerContext` |
| `System.Reflection.Emit` | 运行时 IL 生成 | 源生成器 / 表达式树 |

### JSON 序列化

- **必须**使用 `JsonSerializerContext` 源生成器，禁止依赖反射序列化
- 当前 `JsonSerializerIsReflectionEnabledByDefault` 仍为 `true`（技术债务），新代码**不得**依赖此默认行为
- 新增 DTO 类型时，必须在 `OutputJsonContext` 或对应的 `JsonSerializerContext` 子类中注册：
  ```csharp
  [JsonSerializable(typeof(MyDto))]
  [JsonSerializable(typeof(MyResponse))]
  public partial class MyJsonContext : JsonSerializerContext { }
  ```
- 待全面迁移后，将 `JsonSerializerIsReflectionEnabledByDefault` 设为 `false`（参见 `TODO(#dotnet/json-source-gen)`）

### 第三方库 AOT 兼容性

引用新的 NuGet 包前，必须验证其 trim 兼容性：
1. 检查库是否标注 `[AssemblyMetadata("Trimmable", "true")]`
2. 运行 `dotnet publish -c Release` 后检查 trim 警告
3. 如有 trim 警告，评估是否可用源生成器替代或添加 `DynamicDependency` 标注

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

### 三层启动架构

```
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

# AOT 发布测试（必须通过，无 trim 警告）
dotnet publish src/OneCode.Cli/OneCode.Cli.csproj -c Release

# 运行 CLI
dotnet run --project src/OneCode.Cli/OneCode.Cli.csproj -- --help
```
