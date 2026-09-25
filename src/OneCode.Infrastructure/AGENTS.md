# OneCode.Infrastructure 项目约束

> 本文件为 Infrastructure 层的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

## 层职责定义

Infrastructure 层是系统的**外部系统适配层**，封装所有 I/O、外部服务和第三方 SDK 依赖。

| 子目录 | 职责 |
|--------|------|
| `Mcp/` | 传输级 MCP 客户端与配置适配：`McpClient` 封装官方 ModelContextProtocol SDK（stdio / SSE / HTTP 传输），自定义 WebSocket / InProcess transport，`.mcp.json` 解析（`McpConfigParser`）与多作用域加载（`McpMultiScopeConfigLoader`），官方 registry 客户端（`OfficialMcpRegistryClient`）。连接池管理器 `McpConnectionManager` 在 App 层（`OneCode.App/Services/Mcp/`），其接口 `IMcpConnectionManager` 在 Core 层（`OneCode.Core/Mcp/`） |
| `Git/` | Git 操作（blame 解析 + GitHub 托管提供者） |
| `Config/` | 配置解析与持久化 |
| `Ai/` | AI 模型客户端工厂、IChatClient 装饰器链、Token 估算、OpenAI 响应消毒 |
| `Agent/` | **MAF 管道装配层**：Agent 构建与中间件栈（`AgentPipelineBuilder`）、Harness 产品化默认值（`OneCodeHarnessDefaults` / `PipelineProfile`）、上下文压缩策略（`CompactionPipelineBuilder` / `GuardedSummarizationCompactionStrategy`）、工具审批标记与自动批准规则（`ToolApprovalMarker` 装配期标记 + `ToolApprovalMarkingContextProvider` 请求期标记 provider + `HarnessProviderTools` provider 工具名单/元数据/按名放行规则 + `AutoApprovalRulesFactory`）、Run 级中间件（`RunMiddleware/`：BudgetGuard / PromptTooLongRecovery / UsageTracking）、会话状态扩展（`SessionStateExtensions`）、Hyperlight 代码执行服务（`HyperlightCodeActService`）、SSH shell（`SshShellExecutor`） |
| `Abstractions/` | 内部 Infrastructure 接口（仅 Infrastructure 内部使用） |
| `Remote/` | 远程 Agent 通信 |

> Memory 记忆持久化契约在 `OneCode.Core/Memory/`（`IMemoryEntryStore` / `MemoryEntry` / `MemoryScope`），
> 文件实现位于 `OneCode.Infrastructure/Memory/`（`MemoryEntryStore` / `MemdirPaths`）；
> 领域编排（检索、提示词注入、AutoDream 治理）留在 App 层。

---

## 依赖约束

### 允许的依赖

| 依赖 | 用途 |
|------|------|
| `OneCode.Core` | Core 接口与领域模型（只能向上依赖） |
| `System.*` BCL | 文件系统、网络、进程 |
| `Microsoft.Extensions.*` | DI、日志（MEL console + 自定义 file provider）、HTTP、缓存、配置、文件 Glob |
| `YamlDotNet` | YAML 解析（MCP 配置、主题文件） |
| `ModelContextProtocol` | MCP 官方 SDK |
| `Microsoft.ML.Tokenizers` + Tokenizer 数据包 | Token 估算（Cl100kBase / O200kBase） |
| `System.Text.Json` | JSON 序列化 |
| `CliWrap` | 子进程调用（Git、Hook 执行） |
| `SSH.NET` | SSH 远程执行（SshRemoteService） |
| `SkiaSharp` | 图像处理（截图缩放、格式转换） |
| `Microsoft.Extensions.Http.Resilience` | HTTP 弹性/重试策略 |
| `Microsoft.Extensions.Caching.Memory` | 内存缓存 |
| `Microsoft.Extensions.FileSystemGlobbing` | 文件 Glob 匹配 |
| `System.ClientModel` | AI 客户端模型基类 |
| `Anthropic` | Anthropic API SDK（`ChatClientFactory` 内部使用，App 层不直接引用） |
| `Microsoft.Extensions.AI.OpenAI` | OpenAI 兼容客户端 SDK（`ChatClientFactory` 内部使用） |
| `Microsoft.Agents.AI` + `Microsoft.Agents.AI.Workflows` + `Microsoft.Agents.AI.Harness` + `Microsoft.Agents.AI.Mcp` + `Microsoft.Agents.AI.Tools.Shell` | MAF Agent 框架 1.22.0（实验性 API 的诊断 ID `MAAI001` 由 `src/Directory.Build.props` 的全局 `NoWarn` 统一抑制；Mcp 提供 MCP 工具 AIFunction 桥接 + Tasks extension；Harness 提供 `HarnessAgent` 装配） |
| `Microsoft.Agents.AI.Hyperlight` + `Hyperlight.HyperlightSandbox.Api` + `Hyperlight.HyperlightSandbox.Guest.Python` | Hyperlight 沙箱（`HyperlightCodeActService` 内部使用） |

### 禁止的依赖

| 禁止项 | 原因 | 正确位置 |
|--------|------|---------|
| `Terminal.Gui` | UI 框架属于 App 层 | App/Tui |
| `OneCode.App` | 反向依赖，破坏分层 | -- |
| 直接实例化 `HttpClient` | 绕过 DI 连接池 | 注入 `IHttpClientFactory` |

---

## Git 实现规范（Git/）

### 进程调用策略

Git 操作通过 `IProcessRunner` 执行子进程调用，用于 commit/push/diff 等写操作：

```csharp
// ✅ Git 操作：通过 IProcessRunner
var result = await _processRunner.ExecuteWithArgumentListAsync("git", ["commit", "-m", message], ct: ct);
```

> `GitInfo` 通过构造函数注入 `IProcessRunner`，禁止直接使用 `Process.Start`。

### git blame 解析规范

`GitBlameEntry` 记录类型应捕获：CommitHash、AuthorName、AuthorEmail、AuthorTime、FilePath、LineNumber、Content。解析 `--porcelain` 格式以获得机器可读输出。

---

## MCP 实现规范（Mcp/）

### 传输协议选择

| 传输 | 使用场景 |
|------|---------|
| `Stdio` | 本地进程 MCP Server（最常用） |
| `SSE` | HTTP Server-Sent Events 远程服务 |
| `HTTP Streamable` | 现代 HTTP 流式 MCP |
| `WebSocket` | 双向实时通信 |
| `InProcess` | 同进程测试 / 内置服务器 |

### 连接失败容忍

MCP 连接在启动时软失败（默认超时 30 秒，每台服务器持有独立超时窗口，可经 `initTimeoutMs` / `startupTimeoutMs` 覆盖），不阻塞主流程。连接失败需记录警告日志但不中断程序启动：

```csharp
try
{
    await mcpConnectionManager.ConnectOneAsync(name, ct).ConfigureAwait(false);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "MCP server {Name} failed to connect, skipping", name);
}
```

---

## 构建与测试

```bash
# 构建 Infrastructure 项目
dotnet build src/OneCode.Infrastructure/OneCode.Infrastructure.csproj

# 运行 Infrastructure 相关测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~OneCode.Infrastructure"

# 运行 Memory 相关测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~MemoryEntryStore"

# 运行 Git 相关测试
dotnet test src/OneCode.Tests/OneCode.Tests.csproj --filter "FullyQualifiedName~Git"
```
