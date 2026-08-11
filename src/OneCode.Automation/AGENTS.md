# OneCode.Automation 项目约束

> 本文件为 Automation 层的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

## 层职责定义

Automation 层是系统的**后台调度与启动加载层**，承载所有以 `BackgroundService` / `IHostedService` 形式运行、且**仅依赖 Core 抽象**的周期性或启动期任务。

定位判断准则：
- **下沉到 Automation**：服务是后台调度/启动加载性质，且依赖面仅限 Core 抽象（如 `ICronParser`、`IModelCatalogCache`、`YoloRuleStore`）。
- **不下沉到 Infrastructure**：Infrastructure 是"外部系统适配层"（SDK / IO / 协议），Automation 是"后台调度层"。两者职责不同，不可混用。
- **不下沉到 App**：App 层聚焦业务编排 + TUI，后台调度逻辑独立成层可减少 App 的依赖面与启动开销。

| 子目录 | 职责 |
|--------|------|
| `Cron/` | Cron 调度核心：扫描 `~/{ConfigDir}/cron/*.json`、文件监视、轮询、到期触发；通过 `ICronJobExecutor` 反向调用 App 层执行器 |
| `ModelCatalog/` | models.dev 快照刷新：启动同步加载磁盘缓存，定期从远程刷新 |
| `Yolo/` | YOLO 规则文件加载：启动时异步加载 `~/.onecode/yolo_rules.json` |

---

## 依赖约束

### 允许的依赖

| 依赖 | 用途 |
|------|------|
| `OneCode.Core` | Core 接口与领域模型 |
| `OneCode.Infrastructure` | Infrastructure 实现 |
| `System.*` BCL | 文件系统、定时器、`FileSystemWatcher` |
| `Microsoft.Extensions.Hosting` | `BackgroundService` / `IHostedService` 基类 |
| `Microsoft.Extensions.Logging.Abstractions` | 日志接口 |
| `Cronos` | Cron 表达式解析（实现 `ICronParser`） |

### 禁止的依赖

| 禁止项 | 原因 | 正确位置 |
|--------|------|---------|
| `OneCode.App` | 反向依赖，破坏分层 | App 通过 DI 反向注入接口（`ICronJobExecutor` 等）给 Automation |
| `Terminal.Gui` | UI 框架 | App/Tui |
| `Microsoft.Extensions.AI` / `IChatClient` | AI 调用属于 App 业务编排 | App |
| `Anthropic` / `Microsoft.Agents.AI.*` / `Hyperlight.*` | AI SDK 属于 Infrastructure | Infrastructure |
| `System.CommandLine` | CLI 解析属于 Cli/App | Cli |

### 反向依赖处理

Automation 需要调用 App 层运行时（如 Cron 触发后要把 prompt 交给会话执行）时，**必须**通过以下方式，禁止 ProjectReference：

1. 在 Automation 层定义接口（如 `ICronJobExecutor`），接口语义描述"做什么"而非"怎么做"
2. App 层实现该接口并注册到 DI 容器
3. Automation 通过构造函数注入该接口，运行时由 DI 解析到 App 实现

```csharp
// ✅ 正确：Automation 定义接口，App 实现，DI 反向注入
// OneCode.Automation/Cron/ICronJobExecutor.cs
public interface ICronJobExecutor
{
    Task ExecuteJobAsync(string prompt, CancellationToken ct);
}

// OneCode.App/Services/Cron/CronJobExecutor.cs（App 层实现）
public sealed class CronJobExecutor : ICronJobExecutor { ... }

// OneCode.App/ServiceCollectionExtensions.Business.cs（DI 注册）
services.AddSingleton<CronJobExecutor>();
services.AddSingleton<ICronJobExecutor>(sp => sp.GetRequiredService<CronJobExecutor>());
services.AddCronScheduler();  // Automation 提供的扩展方法
```

---

## 服务实现规范

### BackgroundService 通用要求

- 必须 `sealed`，构造函数注入依赖（primary constructor 优先）
- `ExecuteAsync` 必须响应 `stoppingToken`，循环内使用 `Task.Delay(interval, stoppingToken)`
- 异常处理：`OperationCanceledException` 直接 break 退出；其他异常记录 `LogWarning` 后继续下一轮，避免后台循环因单次失败退出
- 不允许 `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            // do periodic work
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in cleanup cycle");
        }
    }
}
```

### 启动加载服务（IHostedService）规范

- 仅做"启动时一次性加载"，不进入长循环；`StartAsync` 应快速返回，重活走 `Task.Run` + 异常吞咽记录
- 文件不存在 / 解析失败不应阻断启动，需有兜底（如默认值、空集合、走降级路径）

### 调度间隔常量

调度间隔必须定义为 `private static readonly TimeSpan`，并附注释说明选择理由：

```csharp
// 30 秒：Cron 轮询间隔，既时又不过度占用 CPU
private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

// 24 小时：models.dev 缓存本身 7 天过期，每天检查一次足够及时
private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
```

---

## Cron 子模块规范（Cron/）

### 调度行为

- 启动时同步扫描 `~/{ConfigDir}/cron/*.json`，加载所有 `CronJobEntry`
- 使用 `FileSystemWatcher` 监视目录变更，触发去抖动重载（推荐 2 秒去抖）
- 轮询间隔 30 秒（既时又不过度），到期 job 委托给 `ICronJobExecutor.ExecuteJobAsync`
- 持久化 cron（`Durable = true`）需通过 `ONECODE_DURABLE_CRON=true` 显式启用；默认仅会话级

### CronJobEntry 持久化

- 文件命名：`{id}.json`，id 由 `CronCreateTool` 生成
- 路径遍历防护：id 必须经过 sanitize（仅允许字母数字与 `-`）
- 文件写入使用 `WriteAsync` + 临时文件 + 原子替换，避免半写入

---

## 构建与测试

```bash
# 构建 Automation 项目
dotnet build src/OneCode.Automation/OneCode.Automation.csproj

# 运行 Automation 相关测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~OneCode.Automation"

# 运行 Cron 相关测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~Cron"
```
