# OneCode.Core 项目约束

> 本文件为 Core 项目的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

## 依赖约束（不可违反）

Core 是整个系统的契约层，其依赖纯洁性决定了架构的健康度。

### 允许的依赖

| 依赖 | 用途 | 理由 |
|------|------|------|
| BCL（`System.*`） | 基础类型 | 运行时内置 |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | DI 接口 | 仅接口定义 |
| `Microsoft.Extensions.Logging.Abstractions` | 日志接口 | 仅接口定义 |
| `Microsoft.Extensions.AI.Abstractions` | AI 抽象接口 | 仅接口定义 |

### Cron 依赖必须经 Core 契约

`Cronos` **不属于 Core 允许的依赖**，必须通过接口下沉到 Automation：

- Cron 解析：`ICronParser`（Core 契约） + `CronosCronParser`（Automation 实现）

`CronExpressionHelper` 是基于 `ICronParser` 的纯字符串辅助类，不得引用 Cronos。

### 禁止的依赖

| 禁止项 | 原因 | 正确位置 |
|--------|------|---------|
| `Serilog` / `NLog` 等日志实现 | 具体实现，客户端单机程序用 `Microsoft.Extensions.Logging` 即可 | Infrastructure（注：当前项目已移除 Serilog 依赖） |
| `YamlDotNet` | 具体实现 | Infrastructure |
| `Microsoft.Extensions.Http` | 具体实现 | Infrastructure |
| `System.CommandLine` | CLI 框架 | Cli |
| `Terminal.Gui` | UI 框架 | App |
| `ModelContextProtocol` | 协议实现 | Infrastructure |
| `Microsoft.ML.Tokenizers` | 计算实现 | Infrastructure |
| 任何 `OneCode.*` 项目 | 反向依赖 | -- |

### 新增依赖的评审流程

在 Core 中引入新依赖前，必须回答：

1. **这是接口/抽象还是具体实现？** → 只有接口/抽象允许
2. **它是否属于 `MS.Extensions.*.Abstractions` 范畴？** → 优先选择官方 Abstractions
3. **能否在 Core 中定义接口、在 Infrastructure 中引用实现？** → 优先此方案
4. **是否有 3 个以上项目需要此依赖？** → 否则考虑下沉

---

## 接口定义规范

Core 中的接口是全系统的契约，修改需考虑向后兼容。

### 命名与文件

- 接口命名：`IXxx`，文件命名：`IXxx.cs`
- 命名空间：`OneCode.{模块}`（如 `OneCode.Permissions`、`OneCode.Tools`）
- 一个文件一个主接口（可包含密切相关的辅助类型）

### 接口设计原则

```csharp
// ✅ 正确：小接口，单一职责
public interface IToolMetadata
{
    string Name { get; }
    ToolRisk Risk { get; }
}

// ✅ 正确：按能力拆分的小接口
public interface IToolClassification
{
    ToolRisk AssessRisk(JsonElement input);
}

// ❌ 禁止新增：上帝接口
public interface IMyFeature : IMyFeatureCore, IMyFeatureValidation, IMyFeaturePermission
{
    // 新接口不应聚合所有能力，消费者按需依赖小接口。
}
```

### 接口稳定性

- **公共接口**（`public`）：开发期可直接修改签名，不保留 `[Obsolete]` 过渡期
- **内部接口**（`internal`）：可自由修改，但需更新 `InternalsVisibleTo` 项目
- **新增方法**：优先提供默认实现（C# 8 接口默认方法）或扩展方法，避免破坏现有实现

---

## Result\<T> 类型

`Result<T>` / `Result` 是 Core 中定义的操作结果类型，位于 `OneCode.Core.Results` 命名空间。

```csharp
// 用法（非 BCL 内置，是自定义类型）
public async Task<Result<string>> ReadFileAsync(string path, CancellationToken ct = default)
{
    try
    {
        return Result.Success(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
    }
    catch (FileNotFoundException)
    {
        return Result.Failure("File not found");
    }
}

// 同步用法（仅用于 CPU 密集型非 I/O 操作）
public Result<int> ParseNumber(string input)
{
    return int.TryParse(input, out var value)
        ? Result.Success(value)
        : Result.Failure("Invalid number format");
}
```

- 用于预期失败（文件不存在、验证失败等）
- 异常用于意外/不可恢复情况
- I/O 操作必须使用 async 版本，不得同步阻塞
- 详见 `OneCode.Core/Results/Result.cs`

---

## 构建与测试

```bash
# 构建 Core 项目
dotnet build src/OneCode.Core/OneCode.Core.csproj

# 运行 Core 相关测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~ResultTests"

# 运行所有 Core 命名空间下的测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~OneCode.Core"
```
