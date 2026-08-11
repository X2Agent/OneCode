# OneCode.Infrastructure 项目约束

> 本文件为 Infrastructure 层的补充约束。通用编码规范见上级 [AGENTS.md](../AGENTS.md)。
> 当本文档与上级文档冲突时，以本文档为准。

---

## 层职责定义

Infrastructure 层是系统的**外部系统适配层**，封装所有 I/O、外部服务和第三方 SDK 依赖。

| 子目录 | 职责 |
|--------|------|
| `Mcp/` | MCP 客户端管理（连接池、OAuth、多传输协议）：接口 `IMcpConnectionManager` 在本层，实现在 App 层 |
| `Git/` | Git 操作（blame 解析 + GitHub 托管提供者） |
| `Config/` | 配置解析与持久化 |
| `Ai/` | AI 模型客户端工厂、IChatClient 装饰器链、Token 估算、OpenAI 响应消毒 |
| `Agent/` | Agent 沙箱服务（HyperlightCodeActService）、Agent 状态管理（MAF `AgentSessionStateBag`，见 `SessionStateExtensions.cs`） |
| `Abstractions/` | 内部 Infrastructure 接口（仅 Infrastructure 内部使用） |
| `Remote/` | 远程 Agent 通信 |

> Memory 记忆持久化契约在 `OneCode.Core/Memory/`（`IMemoryEntryStore` / `MemoryEntry` / `MemoryScope`），实现位于 `OneCode.App/Services/Memory/`，Infrastructure 层不再持有 Memory 实现。

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
| `Microsoft.Agents.AI` + `Microsoft.Agents.AI.Workflows` + `Microsoft.Agents.AI.Mcp` + `Microsoft.Agents.AI.Tools.Shell` | MAF Agent 框架（实验性 API，需 `#pragma warning disable MAAI001`） |
| `Microsoft.Agents.AI.Hyperlight` + `Hyperlight.HyperlightSandbox.Api` + `Hyperlight.HyperlightSandbox.Guest.Python` | Hyperlight 沙箱（`HyperlightCodeActService` 内部使用） |

### 禁止的依赖

| 禁止项 | 原因 | 正确位置 |
|--------|------|---------|
| `Terminal.Gui` | UI 框架属于 App 层 | App/Tui |
| `System.CommandLine` | CLI 解析属于 Cli/App 层 | Cli |
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

MCP 连接在启动时软失败（超时 1 秒），不阻塞主流程。连接失败需记录警告日志但不中断程序启动：

```csharp
try
{
    await _mcpClientManager.ConnectAsync(serverConfig, ct).ConfigureAwait(false);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "MCP server {Name} failed to connect, skipping", serverConfig.Name);
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
