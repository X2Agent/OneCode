# OneCode .NET — 生产级 TUI AI 编程助手

> **导读**：OneCode .NET 是一个生产级 TUI AI 编程助手。项目采用 .NET 10 + MAF (Microsoft Agent Framework) 1.26.0 构建，基于 Terminal.Gui v2 全屏 TUI，并引入了 Hyperlight 沙箱、LSP 集成、代码索引、DAG 并行调度等增强功能。

> **免责声明**: 本仓库内容仅用于技术研究和科研爱好者交流学习参考，**严禁任何个人、机构及组织将其用于商业用途、盈利性活动、非法用途及其他未经授权的场景。** 若内容涉及侵犯您的合法权益、知识产权或存在其他侵权问题，请及时联系我们，我们将第一时间核实并予以删除处理。

**仓库地址**: [https://github.com/X2Agent/OneCode](https://github.com/X2Agent/OneCode)  
**当前版本**: 1.0.0  
**语言**: 中文 

---

## 目录

- [技术栈](#技术栈)
- [项目结构](#项目结构)
- [分层架构](#分层架构)
- [产品能力矩阵](#产品能力矩阵)
- [安装与使用](#安装与使用)
- [工具系统](#工具系统)
- [核心架构特性](#核心架构特性)
- [TUI 交互](#tui-交互)
- [斜杠命令](#斜杠命令)
- [增强功能亮点](#增强功能亮点)
- [配置与命令参考](#配置与命令参考)
- [已知限制](#已知限制)
- [开发规划](#开发规划)

---

## 技术栈

| 维度 | 技术选型 |
|------|----------|
| 运行时 | .NET 10 |
| UI 框架 | Terminal.Gui v2.4 |
| 异步模型 | Task + async/await |
| 包管理 | NuGet（Central Package Management，版本统一由 `src/Directory.Packages.props` 固定） |
| 测试框架 | xUnit v3 + NSubstitute + FluentAssertions |
| Agent 框架 | MAF 1.26.0 (HarnessAgent + 中间件管道) |
| 发布方式 | .NET 自包含单文件 |

> **已知限制**：
> - `Microsoft.Agents.AI.Hyperlight` (preview)：沙箱功能受限，默认启用（运行时不可用则静默降级）
> - 上下文超限（PromptTooLong）不做自动截断重试：流式 run 以错误收场并提示执行 `/compact` 压缩会话历史

---

## 项目结构

```
src/
├── OneCode.Tui/                 # 进程入口 + Terminal.Gui v2 全屏界面
│   ├── Program.cs              #   主入口
│   ├── LaunchModeDetector.cs   #   快速路径检测
│   ├── FastPathDispatcher.cs   #   特殊模式快速分发
│   └── Tui/                    #   视图 · 浮层 · 渲染器 · TUI 装配（TuiHost / ReplShell / OneCodeToplevel）
│
├── OneCode.WebUI/               # Blazor Server 浏览器客户端（进程内消费 QueryEvent）
│   ├── Program.cs              #   启动入口：AddOneCodeCore + 交互端口替换 + loopback 绑定
│   ├── Components/             #   Chat 页面 · 消息流 · 工具卡片 · 中断卡片 · 活动抽屉
│   └── Services/               #   ChatSessionClient · 交互端口实现 · Markdown 渲染
│
├── OneCode.App/                 # 工具实现 · 命令 · 服务组合（不含 UI 形状）
│   ├── Tools/                  #   27 个工具（通过 AddToolInstance<T> 注册）
│   ├── Commands/               #   45 斜杠命令
│   ├── Services/               #   业务服务（Agent、Memory、Plan、Skills 等子模块，各带 XxxServiceCollectionExtensions）
│   ├── Query/                  #   查询编排 + 唯一 UI 事件契约（QueryEvent / StreamingSession / QueryStreamEngine）
│   └── Session/                #   会话上下文与事件溯源存储
│
├── OneCode.Core/                # 契约 + 领域模型 + 无副作用纯内存引擎（仅依赖 3 个 Microsoft.Extensions.*.Abstractions 抽象包）
│   ├── Permissions/            #   权限系统（7 种模式 + Bash/PowerShell 分类器）
│   ├── Hooks/                  #   6 种开放钩子拦截点 + 3 种执行器
│   ├── Keybindings/            #   按键绑定系统
│   ├── Commands/               #   命令抽象
│   └── Domain/                 #   领域模型
│
├── OneCode.Infrastructure/      # 外部系统适配（120 文件）
│   ├── Mcp/                    #   MCP 协议（5 种传输）
│   ├── Agent/                  #   MAF 管道构建 + Harness opt-out
│   ├── Middleware/             #   MAF 函数级中间件 + Invariants + Contracts
│   ├── Ai/                     #   ChatClient 工厂与装饰器
│   └── Config/                 #   settings.json 配置管理 + 常量定义
│
├── OneCode.Automation/          # 后台调度服务（Cron / ModelCatalog 刷新 / YOLO 规则加载，9 文件）
│
└── OneCode.Tests/               # xUnit v3 测试套件（370 文件，2433 Fact + 183 Theory）
    └── AGENTS.md               #   测试规范约束
```

---

## 分层架构

```
┌─────────────────────────────────────────────────────────────────┐
│  OneCode.Tui  /  OneCode.WebUI  （对等前端，均进程内）            │
│  Terminal.Gui 视图/浮层 · Blazor 组件 · 进程入口/快路径分发     │
└──────────────────────────────┬──────────────────────────────────┘
                               │
┌──────────────────────────────▼──────────────────────────────────┐
│  OneCode.App  (365 文件)                                        │
│  工具实现 · 命令 · 服务组合 · UI 契约 · MAF 集成中枢            │
│  27 Tools · 45 Commands · Query 编排 · 领域自有 DI 注册类       │
└───────────┬─────────────────────────────────┬───────────────────┘
            │                                 │
┌───────────▼──────────┐          ┌───────────▼───────────────────┐
│  OneCode.Core        │◄─────────│  OneCode.Infrastructure        │
│  (168 文件)          │          │  (120 文件)                    │
│  契约 + 领域模型      │          │  外部系统适配                  │
│  + 纯内存引擎         │          │  MCP · Ai · Config ·         │
│  Permissions/Hooks/  │          │  Agent/MAF · Middleware ·     │
│  Tasks/Tools/Domain  │          │  Keybindings · Persistence   │
└──────────────────────┘          └──────────────┬────────────────┘
                                                 │
                                    ┌────────────▼────────────────┐
                                    │  OneCode.Automation (9 文件) │
                                    │  后台调度：Cron / 刷新 / YOLO │
                                    └─────────────────────────────┘

            OneCode.Tests (370 文件) → 测试以上所有层
```

**依赖方向**：Tui → App → Infrastructure → Core（单向；App 另引用 Automation）。`Automation` 仅依赖 Core + Infrastructure，通过 DI 反向注入 App 实现的接口（如 `ICronJobExecutor`）。Core 层保持纯抽象，不引入外部实现依赖。

---

## 产品能力矩阵

| # | 能力 | 完整度 | 关键实现 |
|---|------|:------:|---------|
| 1 | Plan Mode 规划优先模式 | 95% | `PlanModeService` + `SubmitPlan` 工具；批准后由受控 Build run 按 MAF 待办执行 |
| 2 | Subagents 并行子 Agent | 97% | `AgentTool` + `ParallelAgentsTool` + `ForkedAgentRunner` |
| 3 | Skills 斜杠命令工作流 | 92% | `BundledSkills`（8 个内置）+ 文件/MCP 技能 |
| 4 | Hooks 生命周期扩展 | 95% | 6 种开放拦截点 × 3 执行器（Command / Notification / Http） |
| 5 | MCP Servers 外部服务集成 | 90% | `McpConnectionManager`（5 种传输协议） |
| 6 | AGENTS.md 目录级约束 | 88% | 8 个 AGENTS.md 约束文档（仓库 / src / 各项目） |
| 7 | Memory 跨会话记忆 | 95% | `MemoryService` + `AutoDreamService` |
| 8 | Code Search 代码检索 | 94% | `GrepTool` + `LspTool`（symbol 检索 / 引用查找，LSP 优先、索引与文本搜索回退） |
| 9 | Multi-file Edits 跨文件编辑 | 88% | `EditTool`（精确匹配）+ `WriteTool` + `ApplyWorkspaceEditTool` |
| 10 | Git Integration | 92% | 6 个 Git 命令（branch / commit / diff / rebase / review / stash） |
| 11 | Deep Reasoning 深度思考 | 88% | `EffortThinking`（4 级强度） |
| 12 | Web Search 网络搜索 | 87% | `WebSearchTool`（Tavily + DuckDuckGo 故障转移） |
| 13 | Terminal Execution | 92% | `BashTool`（含 powershell 方言）+ `BackgroundRunTool` |
| 14 | Headless Mode CI/CD 模式 | 90% | 无 TTY 自动进入无交互路径；权限由 `permissionMode` 配置 |
| 15 | Code Review 代码审查 | 85% | `/review` 斜杠命令（--staged / LSP / blame / 增量） |
| 16 | Sandboxed Execution 沙箱 | 85% | `HyperlightCodeActService`（默认启用，工作目录只读暴露为 `/input`） |
| 17 | Background Tasks 后台任务 | 92% | `TaskTool` + `Cron` 工具（create/list/delete/pause/resume） |

**综合完成度：17/17 FULL · 平均 91%**

---

## 安装与使用

### 安装

**Windows (PowerShell)**：
```powershell
irm https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.ps1 | iex
```

**Linux / macOS**：
```bash
curl -fsSL https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.sh | bash
```

安装脚本从 `X2Agent/OneCode` 的 latest GitHub Release 下载与当前系统匹配的资产，并在无法获取 Release 时直接失败，不会回退到虚构版本。安装后直接运行 `onecode` 进入交互界面（`onecode --version` 查看版本）。

### 手动构建

```powershell
git clone https://github.com/X2Agent/OneCode.git
cd OneCode
./scripts/build.ps1 -Mode Publish -Runtime win-x64
```

默认发布到仓库根目录 `artifacts/Publish/<RID>/`。正式 Release 使用 self-contained + single-file；推送 `v<major>.<minor>.<patch>` Tag 后，GitHub Actions 会执行 Build → Test → 跨平台 Publish → 冒烟测试 → ZIP/TAR.GZ 打包 → SHA256 → GitHub Release。

### 快速开始

```bash
# 进入交互式 TUI（默认 BUILD 模式）
onecode

# 指定工作目录启动
onecode --cwd /path/to/project

# 查看版本
onecode --version
```

> **Headless / CI 模式**：非交互式终端（无 TTY）下自动进入无交互路径（`AskUserQuestion` 等交互工具返回错误而不是阻塞）。权限模式通过 `settings.json` 的 `permissionMode` 键配置（可选值见 [权限模式](#权限与安全系统)）。

**工作模式切换**：在 TUI 中通过 `Tab` 键循环切换 BUILD → PLAN → GOAL。代码审查请在 TUI 中使用 `/review` 斜杠命令（默认三视角并行审查 + 聚合去重；`--focus` 单视角，`--staged` 限定范围）。

### 使用 OpenCode Zen 免费模型

OneCode 支持通过 [OpenCode Zen](https://opencode.ai/zen) 免费使用多种 AI 模型（包括 DeepSeek V4 Flash），无需注册账号。在 `settings.json` 中配置：

```json
{
  "provider": "openai",
  "baseUrl": "https://opencode.ai/zen/v1",
  "apiKey": "public",
  "model": "deepseek-v4-flash-free"
}
```

或通过环境变量：

```bash
ONECODE_PROVIDER_OVERRIDE=openai
ONECODE_BASE_URL=https://opencode.ai/zen/v1
ONECODE_API_KEY=public
ONECODE_MODEL=deepseek-v4-flash-free
```

> **原理**：Zen 后端接受 `"public"` 作为 API Key，允许访问所有免费模型（付费模型自动过滤）。如需使用付费模型，请在 [opencode.ai/auth](https://opencode.ai/auth) 注册并获取真实 API Key 替换 `"public"`。完整免费模型列表见 [models.dev/providers/opencode](https://models.dev/providers/opencode)。

---

## 工具系统

工具通过 `AddToolInstance<T>` 扩展方法在 DI 注册时统一登记（`src/OneCode.App/Tools/ToolServiceCollectionExtensions.cs` + `OneCode.Automation` 的 Cron 工具），由 `ToolCatalog` 消费**显式 AIFunction 工厂**构建工具列表（无反射），共 27 个工具。

### Shell 与后台执行

| 工具 | 功能 |
|------|------|
| Bash | shell 命令执行（含安全分类；通过 `shell` 参数支持 powershell 方言） |
| BackgroundRun / BackgroundWait | 后台命令执行与等待 |

### 文件操作

| 工具 | 功能 |
|------|------|
| Read | 文件读取（分页、二进制检测） |
| Write | 创建 / 覆写文件 |
| Edit | 搜索替换编辑（要求唯一匹配） |
| Delete | 删除文件 / 目录 |
| LS | 目录列表 |
| Glob | Glob 模式文件搜索 |
| Grep | 正则内容搜索（ripgrep 风格） |

### 代码 / LSP

| 工具 | 功能 |
|------|------|
| ApplyWorkspaceEdit | 应用 LSP workspace edit |
| Lsp | LSP 语言服务操作（diagnostics / definition / hover / references / workspaceSymbol 等） |

### Web 工具

| 工具 | 功能 |
|------|------|
| WebFetch | 抓取网页（HTTP→Markdown；SPA/JS 页面返回降级提示，由模型决定是否用 BrowserFetch 渲染） |
| WebSearch | 网络搜索（Tavily + DuckDuckGo 故障转移） |
| BrowserFetch | 真实无头浏览器抓取（按需连接内置 playwright MCP，一次调用完成渲染，返回 ARIA 快照） |

### Agent / 子代理

| 工具 | 功能 |
|------|------|
| Agent | 派生子代理（general-purpose / Explore / Plan，支持后台运行） |
| ParallelAgents | DAG 依赖并行调度（MAF 工作流运行时） |
| AskUserQuestion | 向用户提问（单题对话框 / 多题向导） |

### 任务与定时

| 工具 | 功能 |
|------|------|
| Task | 后台任务管理 |
| Cron | 跨会话定时任务（`action` = create / list / delete / pause / resume，单工具 action 路由） |

### Plan Mode

| 工具 | 功能 |
|------|------|
| SubmitPlan | 提交计划 |

### Git Worktree

| 工具 | 功能 |
|------|------|
| EnterWorktree / ExitWorktree | Git Worktree 进出管理 |

### MCP

| 工具 | 功能 |
|------|------|
| mcp__{server}__{tool} | MCP 服务器工具（自动前缀，直接可调用） |
| ListMcpResources / ReadMcpResource | MCP 资源枚举与读取 |

### 元工具

| 工具 | 功能 |
|------|------|
| ToolSearch | 工具搜索（自动追加） |

---

## 核心架构特性

### MAF Agent 管道

```
IChatClient → .AsBuilder() → 注入共享 7 种 AIContextProvider（+ Main 模式专属）
    ↓
HarnessAgent
    ↓
[Run 级中间件 — 包裹整个 Agent Run]
.Use(BudgetGuardRunMiddleware)        ← 预算熔断（pre-execution 检查，token 超限短路）
.Use(UsageTrackingRunMiddleware)      ← Token 用量记录（更新 TokenLedger）
    ↓
[Function 级中间件 — 包裹每次工具调用]
.Use(SafetyInvariantMiddleware)       ← 安全不变量检查
.Use(HookMiddleware)                  ← Hook 包裹（Pre/Post 工具调用）
.Use(ToolCallEventMiddleware)         ← 工具调用事件发射
.Use(PermissionAndLimitMiddleware)    ← 权限校验 + 工具调用上限
.Use(StateMachineMiddleware)          ← 状态机管理
.Use(EditTransactionMiddleware)       ← 编辑事务（可回滚的原子操作）
.Use(EditGuardMiddleware)             ← 编辑契约前置校验 + 编辑后验证（编译/测试）
.Use(ToolExecutionBudgetMiddleware)   ← 工具执行结果预算（防止无限循环）
.Use(ToolResultUnwrapMiddleware)      ← ToolResult 解包
.Use(ToolArgumentBindingMiddleware)   ← 工具参数绑定容错（数组参数归一化，绑定失败转为可自纠正的 ToolResult.Error）
.UseToolApproval(...)                 ← MAF 工具审批（Harness 未接管时）
    ↓
.Build() → AIAgent
```

> 工具调用时序观测由 Harness 默认启用的 OpenTelemetry 提供（`DisableOpenTelemetry` 保持 false），OneCode 不再重复挂一层。

**共享 AIContextProvider（由 `SharedContextProviderBuilder.BuildCommon` 按能力枚举装配）**：

| Provider | 能力（`AgentCapability`） |
|----------|------|
| `SkillProviderFactory` → MAF `AgentSkillsProvider` | `Skills` |
| `MemoryContextProvider`（`ReadOnlyAIContextProviderBase` 长期记忆全文注入） | `MemorySearch` |
| Harness `FileMemoryProvider`（`file_memory_*` 会话工作记忆） | `FileMemory` |
| `DesignContextProvider` | `DesignContext` |
| `LspDiagnosticContextProvider` | `LspDiagnostics` |
| `ShellEnvironmentProvider` | `ShellEnvironment` |
| `CodeActProvider`（Hyperlight 沙箱，MAF `HyperlightCodeActProvider`） | `CodeAct` |

> Main 路径另追加 `ModeInstructionProvider`（取代 MAF `AgentModeProvider`，仅注入指令不给 `mode_set` 工具）与模式专属 Provider（Plan/Build/Goal）。
> `CompactionProvider` **不在此列表**——策略经 `HarnessAgentOptions.CompactionStrategy` 交给 Harness 挂载，见 [compact-thresholds.md](docs/compact-thresholds.md)。
> Harness `TodoProvider`（`todos_*`）按 profile 启用，同样不在此列表（由 `HarnessAgentOptions.DisableTodoProvider` 门控）。

### 三种工作模式

| 模式 | 行为特征 | 默认权限规则 | 适用场景 | 视觉色系 |
|------|----------|--------------|----------|----------|
| **BUILD** | 自由提问直接执行；从 PLAN 批准计划后转入受控 Build：沿用同一套交互式审批规则，但可用工具按批准策略收窄，并走计划快照与收口验证 | `Default`（只读工具直接通过，写文件与非只读 Shell 弹窗审批，支持「允许本次 / 始终允许 / 本次会话全部允许」） | 代码实现、问题修复、已批准计划落地 | 绿色 |
| **PLAN** | 只读调研（读工具、只读 Shell 与规划辅助工具）后生成结构化计划卡片，经用户批准后切入 BUILD 执行 | `Plan`（只读 + 规划辅助工具放行，写文件与非只读 Shell 拒绝） | 复杂重构、多步骤任务 | 蓝色 |
| **GOAL** | 在隔离 Git Worktree 中自主分解子目标、逐子目标独立事务与 Git 检查点、硬门禁 + Judge 验收后合入；每条用户消息作为一个 GoalRun 的目标 | `GoalAuto`（工作区内读写与常规开发命令直接放行，破坏性高危 Shell 拦截拒绝） | 开放式高层目标 | 青色 |

> **GOAL 前置条件**：新建 GoalRun 要求目标目录是 Git 仓库且工作区无未提交改动；运行期目标分支 HEAD 或工作区指纹漂移会导致发布失败。同会话已有非终态 GoalRun 时不能换目标；中断运行用 `/resume` 恢复（要求 Goal 定义未变化）。

**模式切换方式**：
- `Tab`（输入框内，空闲时）→ 循环 BUILD → PLAN → GOAL；run 活跃期间忽略，补全/占位建议/选择器优先

### 权限与安全系统

7 种 `PermissionMode`：

| 模式 | 行为 |
|------|------|
| Default | 每次危险操作都询问用户 |
| Plan | 规划模式，只读不执行（PLAN 工作模式自动派生） |
| Auto | 自动模式（配合 Yolo 分类器） |
| AcceptEdits | 自动批准文件编辑 |
| BypassPermissions | 跳过所有权限检查（Layer 0 安全不变量仍生效） |
| DontAsk | 不询问，直接拒绝危险操作 |
| GoalAuto | GOAL 模式自动派生：自主执行不中断，危险 Shell 直接 Deny |

> Plan / GoalAuto 由对应工作模式经 `WorkingModeBridge` 自动派生，`/permissions` 不接受这两个值
> （会造成权限轴与模式轴不一致的半状态）——切换工作模式用 Tab。
> **会话级一次性配置**：`/permissions bypass --session` 让本次对话自动执行所有操作而不写配置文件；
> 审批弹窗中也可直接选「本次对话全部允许」。两者都不影响 Layer 0 安全不变量（危险命令 / 敏感路径 / 符号链接越狱）。

**10 层渐进式安全带机制**：核心循环 → 工具调度 → 计划 → 子代理 → 按需知识 → 上下文压缩 → 持久化任务 → 后台任务 → 自主代理 → 工作树隔离。

**命令分类器**：
- `BashCommandClassifier`：Shell 命令 Safe / Warning / Dangerous 三级分类
- `PowerShellCommandClassifier`：500+ 行实现，覆盖别名解析、编码绕过检测、管道复合命令分析
- `YoloClassifier`：基于规则的自动分类器，支持 allow / deny / ask 三种策略

### 子代理与多代理

```
主代理 (MainAgentRunner)
    ├── simple    → 单次派生子代理（ForkedAgentRunner）
    └── DAG       → 依赖并行（ParallelAgents 工具与 GOAL 步内并行共用
                   AgentTaskWorkflowCompiler + AgentTaskWorkflowHost 底座）

```


### 记忆系统

两级作用域：User（全局永久 `~/.onecode/memory/MEMORY.md`）/ Project（仓库本地 `{cwd}/.onecode/memory/MEMORY.md`）。`AutoDreamService` 后台自动整合记忆（四重门控 + 跨进程文件锁，配置走 `settings.json` 的 `autodream.*` 键）。

### Hook 系统

6 种开放钩子拦截点覆盖核心执行边界（`input` / `output` / `pre_tool_call` / `post_tool_call` / `pre_compact` / `post_compact`），3 种执行器（Command / Notification / Http）。退出码 `2` 可阻断工具调用或输入输出。

### MCP 集成

5 种传输协议（Stdio / SSE / HTTP Streamable / WebSocket / InProcess），多作用域配置加载（user / project / local 三层同名覆盖，另有内置服务清单兜底）。

启动采用后台预连接：trust 流程通过后连接在后台进行，TUI 首屏立即渲染；首条消息发送前对进行中的连接做 ≤5s 有界收尾（超时放行，工具下一轮附挂）。状态栏以三态显示连接进度（`连接中 x/y` → `已连 n · Nt` → 全部失败时 `MCP: ✗k 失败`），启动失败另有一次性 startup hint 给出逐台明细与 `/mcp connect` 重试指引，失败原因在 `/mcp list` 中始终可见。`/mcp install` 默认安装后立即连接验证（`--no-connect` 跳过），坏包当场暴露。

### 上下文压缩

多级压缩策略：Harness 策略入口（`HarnessAgentOptions.CompactionStrategy`）驱动 in-pipeline 自动压缩（工具调用瘦身 + LLM 摘要，含摘要守卫与预算校验）+ `AutoCompactService`（转写规模提示）+ 手动 `/compact`（`CompactService`，支持全文/部分两种模式）。

---

## TUI 交互

### 三层布局

```
┌──────────────────────────────────────────────────────┐
│                                                       │
│              CHAT — 唯一主视图                          │  Dim.Fill()
│              （BUILD / PLAN / GOAL 共享）              │
│      计划侧栏按需弹出（Shift+PageUp/PageDown 翻页）      │
│                                                       │
├─ Status Bar ─────────────────────────────────────────┤  1 row
│  Opus · 🔒 Sandbox               BUILD · 13:19       │
├─ Input Line ─────────────────────────────────────────┤  3+ rows
│ BUILD ❯ _                                  Tab切模式  │
└──────────────────────────────────────────────────────┘
```

**设计原则**：Chat 是唯一永久主视图，无标题栏、无底部永久面板，辅助功能（计划侧栏、
审批、选择器）按需弹出覆盖层。

### 覆盖层系统

| 覆盖层 | 触发键 | 类型 |
|--------|--------|------|
| 审查模式 | `/diff` | 覆盖层 |
| 设置 / 会话恢复 | `/config` · `/session` | 覆盖层 |
| Diff 详情 | Review 中 Enter | 半屏覆盖 |

斜杠命令通过输入 `/` 补全发现与执行（无独立命令面板快捷键）。

### 输入系统

- **多行输入**：`Shift+Enter` / `Alt+Enter` 换行，输入区 3-6 行自适应
- **提交消息**：`Enter` 提交
- **历史记录**：↑↓ 浏览；`Ctrl+↑` 召回上一条以便编辑重发
- **智能粘贴**：Ctrl+V 自动处理多行内容 / 图片 / 路径
- **斜杠命令补全**：输入 `/` 自动弹出命令列表，Tab 循环，Enter 接受
- **会话搜索**：`/find <关键词>` · `/find next`

### 全局快捷键

| 快捷键 | 动作 |
|--------|------|
| Ctrl+C | 忙时中断 · 空闲退出 |
| Ctrl+D | 退出程序 |
| Esc | 模型响应时中断 agent；补全激活时关闭补全 |
| Tab | 切换工作模式 / 补全 |
| /diff | 审查 Git 变更（覆盖层） |
| /help | 命令与快捷键说明 |

---

## 斜杠命令

45 个斜杠命令按 `CommandCategory` 分为 5 类（详见 [docs/commands.md](docs/commands.md)）：

### Builtin 类别命令（23 个）

`/add-dir` `/compact` `/config` `/copy` `/cron` `/daemon` `/design-init` `/exit` `/fastmodel` `/files` `/help` `/hooks` `/init` `/keybindings` `/loop` `/lsp` `/model` `/permissions` `/prompts` `/skills` `/think` `/upgrade` `/version`

### Session 类别命令（11 个）

`/checkpoint` `/close` `/export` `/find` `/insights` `/memory` `/new` `/queue` `/rename` `/resume` `/session`

### Diagnostic 类别命令（3 个）

`/doctor` `/gc-stats` `/status`

### Skill 类别命令（2 个）

`/install` `/mcp`

### Git 类别命令（6 个）

`/branch` `/commit` `/diff` `/rebase` `/review` `/stash`

---

## 增强功能亮点

以下为 OneCode 独有的增强功能：

| 功能 | 说明 |
|------|------|
| **Hyperlight 沙箱** | 微虚拟机隔离代码执行（CodeAct），工作目录只读暴露为 `/input`；运行时（Windows WHP / Linux KVM）不可用时静默降级 |
| **LSP 集成** | `EnhancedLspService` + `LspServerManager`，编辑后实时诊断更新 |
| **代码语义索引** | `CodeIndexService` 语义符号搜索（精确 / 模糊 / 关键字匹配） |
| **DAG 并行调度** | `ParallelAgentsTool` → MAF 工作流运行时（`AgentTaskWorkflowCompiler` + `AgentTaskWorkflowHost`），依赖驱动的并行 |
| **AutoDream 记忆整合** | 后台自动提取关键信息写入记忆文件 |
| **Git Worktree 管理** | `EnterWorktreeTool` / `ExitWorktreeTool`，任务级隔离 |
| **Cron 定时任务** | 单 `Cron` 工具（`action` = create/list/delete/pause/resume），跨会话持续运行 |
| **自包含单文件发布** | .NET 10 self-contained + single-file |
| **多级上下文压缩** | MAF 自动压缩 + 手动 `/compact`（全文/部分）|
| **Token 精确估算** | `TokenBreakdownEstimator` 多模型 token 精确计算 |
| **SSH 远程执行** | `SshRemoteService` 基于 SSH.NET 的远程命令执行 |
| **飞书/企业微信通知** | Hook 通知支持飞书和企业微信 |

---

## 配置与命令参考

`docs/` 目录下提供以下工程参考文档：

| 文档 | 说明 |
|------|------|
| [docs/commands.md](docs/commands.md) | 全部 45 个斜杠命令的功能说明、用法与参数详解，按 `Builtin` / `Session` / `Diagnostic` / `Skill` / `Git` 5 类组织 |
| [docs/settings.md](docs/settings.md) | `settings.json` 全部合法配置项、默认值、优先级与环境变量说明 |
| [docs/skills.md](docs/skills.md) | 8 个内置技能（BundledSkills）的逐个说明、参数占位符规则、自定义技能开发指南 |

---

## 已知限制

| 限制项 | 说明 |
|--------|------|
| 上下文超限（PromptTooLong） | 不做自动截断重试；流式 run 失败收场时提示执行 `/compact` 压缩会话历史后重试 |
| 鼠标点击模式标签 | Terminal.Gui 中未实现鼠标处理 |
| Hyperlight 沙箱 | `Microsoft.Agents.AI.Hyperlight` 为 preview 包，沙箱功能受限（运行时不可用时自动降级） |

---

## 开发规划

项目的后续开发方向主要包括：

- **可控性重构**：共享上下文 → 行为契约 → Workflow/Eval → 审计度量
- **功能增强**：Code Review 结构化、沙箱执行增强、LSP 深度集成
- **测试改进**：覆盖率提升、安全关键测试补充
- **长期方向**：Workflow YAML 验收、可观测性仪表盘、记忆回路优化

---

## 许可证

本仓库内容仅用于技术研究和教育目的。知识产权归原公司所有，若有侵权请联系删除。
