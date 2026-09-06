# 未提交改动审计与 MCP 方法论推广计划

> 状态：计划稿（已核实、未实施，2026-09-06）。对象为当期未提交改动集（110 文件，+5023/-957）。
> 背景：MCP 模块在本批次中完成生产化改造，本文档将同一套方法论对照审计其余模块，
> 记录经代码级核实的遗留问题与修复方案（含行号证据，不凭 diff 推断），供后续实施参考。

## 1. 改动意图（当期未提交改动集）

主线是 **MCP 模块的生产可用性改造**，同套方法辐射 LSP / Tools / TUI：

| 维度 | MCP 侧改动 | 方法 |
|---|---|---|
| 生命周期自愈 | `McpConnectionManager.AutoReconnect.cs`：ping 探活（SDK 无断连事件）→ 指数退避 → 上限 3 次转人工；显式断开、内置按需服务豁免（豁免理由注释化） | 自愈必须有上限、有豁免、有理由 |
| 状态可见性 | `McpConnectionSummary` 三态（Expected/Connected/Connecting/Failed）、`LastError`、`/mcp list` 显示失败原因 | 失败原因直达用户，不留静默状态 |
| 可观测性 | `ILoggerFactory` 贯通 SDK 日志（替代固定 `NullLogger`） | 排障黑洞清零 |
| 超时分级 | 每服务器 `InitTimeoutMs/StartupTimeoutMs` 优先、非法回落 30s + 全局启动预算 60s | per-item 配置优先，全局预算兜底 |
| 启动非阻塞 | `McpStartupPreconnector`：后台 fire-and-forget + 首条消息 5s 有界等待 + cron 显式等待；幂等启动 | 交互路径零阻塞、非交互路径显式等待 |
| 能力 | `McpToolFilter` 白名单三态语义（null=全放行/空=全拒/通配符）、`/mcp enable-tool|disable-tool|install`、TUI 配置页 | 工具治理闭环 |
| 性能修正 | `OfficialMcpRegistryClient` 服务端 search + 30s 请求预算（废除全量分页扫描）；`InProcessMcpTransportPair` 零序列化测试通道 | 绝不因慢退回全量扫描 |
| 接口瘦身 | `IMcpClient` 删 prompts/StreamableHttp 死代码；`PlanStepDto` 删 Label/Content 兼容别名 | 不留兼容层 |

## 2. 核实结论（原 8 疑点 + 新发现）

| # | 疑点 | 结论 | 证据 |
|---|---|---|---|
| P1 | LSP 重启计数器生命周期缺陷 | ✅ 确认 | `restartAttempts` 为 `HealthCheckLoopAsync` 局部字典（LspServerManager.cs:166）：放弃后永不复位；MCP 侧连接成功清零计数（McpConnectionManager.cs:382），LSP 无对等逻辑 |
| P10 | `/lsp restart` 命令不存在 | ✅ 新发现 | LspCommand 子命令仅 list/install/uninstall/status/enable/disable；崩溃日志却提示 `Use /lsp restart`，误导用户 |
| P2 | 诊断轮询先等后查 | ✅ 确认（加重） | LspNotifier.cs:74 先 Delay 再查；且超时路径把上一版本 stale 诊断当结果返回 |
| P3 | LSP 失败原因不可见 | ✅ 确认 | `StartError` 全库仅定义处（LspClient.cs:45）零消费；`LspServerStatus` 无 error 字段 |
| P4 | DeleteTool 目录删除不通知 LSP | ✅ 确认 | `didClose` 仅单文件分支；`_ = NotifyFileClosedAsync(...)` 火后不管 |
| P5 | 白名单热生效断链 | ❌ 证伪 | `ReloadToolsAsync`（McpConnectionManager.cs:530）闭环：重读配置→live client 重载→原子替换→广播 |
| P6 | 子命令进度标签断链 | ❌ 证伪 | `Dispatch.GetProgressLabel`→`ctx.GetProgressMessage`→`GetSubcommandProgressMessage` 闭环，有测试 |
| P7 | Preconnector 竞争边界 | ⚠️ 降级 | 落败方 `onCompleted`/`ct` 被丢弃属实，但当前调用序列安全；补注释不改结构 |
| P8 | AutoReconnect OCE/Dispose | ❌ 证伪 | 循环 `catch (OCE) { break; }` 正确；`DisposeAsync` Cancel+WaitAsync(5s) 完整 |

净结论：4+1 项真问题（P1/P2/P3/P4/P10），3 项证伪，1 项降级。

## 3. 修复方案

1. **P1+P10（LSP 自愈对齐 MCP）**
   - `restartAttempts` 提升为字段（`ConcurrentDictionary`），实例恢复健康即清零；手动重启成功清零（对齐 McpConnectionManager.cs:382 语义）。
   - `LspServerManager` 新增 `RestartAsync(name)`（stop + start）。
   - `LspCommand` 新增 `restart` 子命令（`serverManager` 已注入，成本低），崩溃日志文案与实际能力对齐。
2. **P2（诊断轮询）**
   - `EnhancedLspService.NotifyFileUpdatedAsync` 记录每文件 `didChange` 时刻（发送前置位，防漏快速推送）；`GetLastDidChangeUtc` 暴露。
   - `GetDiagnosticsSummaryAsync` 基线改用 didChange 时刻（替代调用时刻，快路径推送不再误判 stale）；先查后等；超时且无新诊断返回 null（不返回旧版本诊断误导模型）。
3. **P3（失败原因可见）**
   - `LspServerStatus` 增 `LastError`；`GetStatus` 映射实例启动错误；`/lsp` 状态输出展示。
4. **P4（DeleteTool × LSP）**
   - `EnhancedLspService` 新增 `NotifyDirectoryDeletedAsync`：按 uri 前缀清已跟踪版本并逐个 didClose（只对已打开文件发通知，零枚举成本）。
   - `ILspNotifier`/`NoOpLspNotifier` 同步签名；DeleteTool 目录分支调用；单文件分支改为可等待。
5. **P7**：`McpStartupPreconnector` 补落败方语义注释。

每项修复按 MCP 模块标准配回归测试；build + test 全绿、无新增警告。

## 4. Lazy 使用审计（全库 8 处，无一处用于 DI 循环）

| 位置 | 用途 | 结论 |
|---|---|---|
| `ServiceCollectionExtensions.Tools.cs:42` + `ToolCatalog._staticTools` | 组合根捕获 IServiceProvider 构建静态工具，惰性到首次使用 | 保留（注释已声明组合根所有权） |
| `McpStartupPreconnector._preconnect` | `Lazy<Task>` 恰好一次后台启动（落败方不触碰自己的工厂，天然防双启动） | 保留（可选：Interlocked+TCS 替换） |
| `PlanAgentRunDispatcher._activeStarts` | 按 key 幂等去重 | 保留 |
| `BundledSkills` / `PathsHelper.s_userHome` / `HyperlightRuntimeProbe` / `DebugLogConfig` | 静态惰性缓存（资源/探测/路径） | 保留 |

**规范红线**：禁止用 `Lazy` 打破 DI 循环依赖——循环是分层设计错误，Lazy 只会把解析失败从启动期推迟到首次使用期（更难排查）。出现循环必须重构：事件/中介、提取接口分层、工厂、调整依赖方向。本库现状无此问题（8 处均为惰性求值/恰好一次语义，非容器级 `Lazy<T>` 服务解析）。

## 5. 验收

- `dotnet build src/OneCode.slnx` 无新增警告；`dotnet test src/OneCode.slnx` 全部通过。
- 修复项均有回归测试锚点；P5/P6/P8 已闭环无需改动（避免无意义改动）。
