# OneCode .NET — 生产级 CLI AI 编程助手

> **导读**：OneCode .NET 是一个生产级 CLI AI 编程助手。项目采用 .NET 10 + MAF (Microsoft Agent Framework) 1.22.0 构建，基于 Terminal.Gui v2 全屏 TUI，并引入了 Hyperlight 沙箱、LSP 集成、代码索引、DAG 并行调度等增强功能。

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
| Agent 框架 | MAF 1.22.0 (HarnessAgent + 中间件管道) |
| 发布方式 | .NET 自包含单文件 |

> **已知限制**：
> - `Microsoft.Agents.AI.Hyperlight` (preview)：沙箱功能受限，默认启用（运行时不可用则静默降级）
> - GOAL 模式流式输出不支持自动重试（非流式模式支持 PromptTooLong 恢复）
> - Team 子 Agent 审批经 Team 工作流桥呈现：成员保留 MAF `ToolApprovalAgent`，审批请求以 `RequestInfoEvent` 浮出后由 `TeamWorkflowRunner` 推 `OrchestrationEvent.ApprovalRequest`；Main 路径为流式审批拆分（见 M4 完全事件驱动审批）

---

## 项目结构

```
src/
├── OneCode.Cli/                 # CLI 入口 · 快速路径分发（6 文件 + 1 AGENTS.md）
│   ├── Program.cs              #   主入口
│   ├── CliModeDetector.cs      #   快速路径检测
│   └── FastPathDispatcher.cs   #   特殊模式快速分发
│
├── OneCode.App/                 # 工具实现 · 命令 · TUI · 服务组合（472 文件）
│   ├── Tools/                  #   31 个工具（通过 AddToolInstance<T> 注册）
│   ├── Commands/               #   44 斜杠命令
│   ├── Services/               #   业务服务（Agent、Memory、Plan、Skills 等 22 子模块，各带 XxxServiceCollectionExtensions）
│   ├── Query/                  #   查询编排（ChatService / QueryStreamEngine / StreamingSession）
│   ├── Tui/                    #   Terminal.Gui v2 全屏界面
│   └── Session/                #   会话上下文与事件溯源存储
│
├── OneCode.Core/                # 纯接口与领域模型（167 文件，仅依赖 3 个 Microsoft.Extensions.*.Abstractions 抽象包）
│   ├── Permissions/            #   权限系统（8 种模式 + Bash/PowerShell 分类器）
│   ├── Hooks/                  #   11 种钩子事件 + 3 种执行器
│   ├── Keybindings/            #   按键绑定系统
│   ├── Commands/               #   命令抽象
│   └── Domain/                 #   领域模型
│
├── OneCode.Infrastructure/      # 外部系统适配（103 文件）
│   ├── Mcp/                    #   MCP 协议（5 种传输）
│   ├── Agent/                  #   MAF 管道构建 + Harness opt-out
│   ├── Middleware/             #   MAF 函数级中间件 + Invariants + Contracts
│   ├── Ai/                     #   ChatClient 工厂与装饰器
│   └── Config/                 #   settings.json 配置管理 + 常量定义
│
├── OneCode.Automation/          # 后台调度服务（Cron / ModelCatalog 刷新 / YOLO 规则加载，9 文件）
│
└── OneCode.Tests/               # xUnit v3 测试套件（262 文件，1942 Fact + 150 Theory）
    └── AGENTS.md               #   测试规范约束
```

---

## 分层架构

```
┌─────────────────────────────────────────────────────────────────┐
│  OneCode.Cli  (6 文件)                                          │
│  CLI 入口 · 快速路径分发 · CliModeDetector · FastPathDispatcher │
└──────────────────────────────┬──────────────────────────────────┘
                               │
┌──────────────────────────────▼──────────────────────────────────┐
│  OneCode.App  (472 文件)                                        │
│  工具实现 · 命令 · TUI · 服务组合 · MAF 集成中枢                 │
│  31 Tools · 44 Commands · Query 编排 · 领域自有 DI 注册类       │
└───────────┬─────────────────────────────────┬───────────────────┘
            │                                 │
┌───────────▼──────────┐          ┌───────────▼───────────────────┐
│  OneCode.Core        │◄─────────│  OneCode.Infrastructure        │
│  (167 文件)          │          │  (103 文件)                    │
│  纯接口与领域模型     │          │  外部系统适配                  │
│  Permissions/Hooks/  │          │  MCP · Ai · Config ·         │
│  Tasks/Tools/Domain  │          │  Agent/MAF · Middleware · LSP │
└──────────────────────┘          └──────────────┬────────────────┘
                                                 │
                                    ┌────────────▼────────────────┐
                                    │  OneCode.Automation (9 文件) │
                                    │  后台调度：Cron / 刷新 / YOLO │
                                    └─────────────────────────────┘

            OneCode.Tests (262 文件) → 测试以上所有层
```

**依赖方向**：Cli → App → Infrastructure → Core（单向；App 另引用 Automation）。`Automation` 仅依赖 Core + Infrastructure，通过 DI 反向注入 App 实现的接口（如 `ICronJobExecutor`）。Core 层保持纯抽象，不引入外部实现依赖。

---

## 产品能力矩阵

| # | 能力 | 完整度 | 关键实现 |
|---|------|:------:|---------|
| 1 | Plan Mode 规划优先模式 | 95% | `PlanModeService` + `SubmitPlan`/`UpdatePlanStep` 工具 |
| 2 | Subagents 并行子 Agent | 97% | `AgentTool` + `ParallelAgentsTool` + `ForkedAgentRunner` |
| 3 | Skills 斜杠命令工作流 | 92% | `BundledSkills`（9 个内置）+ 文件/MCP 技能 |
| 4 | Hooks 生命周期扩展 | 95% | 11 事件 × 3 执行器（Command / Notification / Http） |
| 5 | MCP Servers 外部服务集成 | 90% | `McpConnectionManager`（5 种传输协议） |
| 6 | AGENTS.md 目录级约束 | 88% | 8 个 AGENTS.md 约束文档（仓库 / src / 各项目） |
| 7 | Memory 跨会话记忆 | 95% | `MemoryService` + `AutoDreamService` |
| 8 | Code Search 代码检索 | 94% | `GrepTool` + `SymbolSearchTool` + `FindReferencesTool` |
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

# 携带初始 prompt 进入 TUI（默认 BUILD 模式直接执行）
onecode "修复登录页的 CSS 问题"

# 指定工作目录启动
onecode --cwd /path/to/project "分析项目结构"

# 查看版本
onecode --version
```

> **Headless / CI 模式**：非交互式终端（无 TTY）下自动进入无交互路径（`AskUserQuestion` 等交互工具返回错误而不是阻塞）。权限模式通过 `settings.json` 的 `permissionMode` 键配置（可选值见 [权限模式](#权限与安全系统)）。

**工作模式切换**：在 TUI 中通过 `Tab` 键循环切换 BUILD → PLAN → TEAM → GOAL。代码审查请在 TUI 中使用 `/review --staged` 斜杠命令。

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

工具通过 `AddToolInstance<T>` 扩展方法在 DI 注册时统一登记（`src/OneCode.App/Tools/ToolServiceCollectionExtensions.cs` + `OneCode.Automation` 的 Cron 工具），由 `ToolCatalog` 消费**显式 AIFunction 工厂**构建工具列表（无反射），共 31 个工具。

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
| FindReferences | 查找符号引用（LSP） |
| ApplyWorkspaceEdit | 应用 LSP workspace edit |
| SymbolSearch | 代码符号搜索（Code Index） |
| Lsp | LSP 语言服务操作 |

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
| AskUserQuestion / AskUserQuestions | 向用户提问（单题 / 多题向导） |

### 任务与定时

| 工具 | 功能 |
|------|------|
| Task | 后台任务管理 |
| Cron | 跨会话定时任务（`action` = create / list / delete / pause / resume，单工具 action 路由） |

### Plan Mode

| 工具 | 功能 |
|------|------|
| SubmitPlan | 提交计划 |
| UpdatePlanStep / CompletePlanVerification | 执行计划状态更新与验证（CompletePlanExecution 由编排层自动推导） |

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
.Use(PromptTooLongRecoveryRunMiddleware) ← PromptTooLong 异常恢复（截断重试）
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
.UseToolApproval(...)                 ← MAF 工具审批（Harness 未接管时）
    ↓
.Build() → AIAgent
```

> 工具调用时序观测由 Harness 默认启用的 OpenTelemetry 提供（`DisableOpenTelemetry` 保持 false），OneCode 不再重复挂一层。

**共享 AIContextProvider（由 `SharedContextProviderBuilder.BuildCommon` 按能力枚举装配）**：

| Provider | 能力（`AgentCapability`） |
|----------|------|
| `SkillProviderFactory` → MAF `AgentSkillsProvider` | `Skills` |
| `MemorySearchProviderFactory` → MAF `TextSearchProvider`（`search_memories`） | `MemorySearch` |
| Harness `FileMemoryProvider`（`file_memory_*` 会话工作记忆） | `FileMemory` |
| `DesignContextProvider` | `DesignContext` |
| `LspDiagnosticContextProvider` | `LspDiagnostics` |
| `ShellEnvironmentProvider` | `ShellEnvironment` |
| `CodeActProvider`（Hyperlight 沙箱，MAF `HyperlightCodeActProvider`） | `CodeAct` |

> Main 路径另追加 `ModeInstructionProvider`（取代 MAF `AgentModeProvider`，仅注入指令不给 `mode_set` 工具）与模式专属 Provider（Plan/Build/Goal）。
> `CompactionProvider` **不在此列表**——策略经 `HarnessAgentOptions.CompactionStrategy` 交给 Harness 挂载，见 [compact-thresholds.md](docs/compact-thresholds.md)。
> Harness `TodoProvider`（`todos_*`）按 profile 启用，同样不在此列表（由 `HarnessAgentOptions.DisableTodoProvider` 门控）。

### 四种工作模式

| 模式 | 行为特征 | 适用场景 | 视觉色系 |
|------|----------|----------|----------|
| **BUILD** | 用户输入 → Agent 直接分析并执行 | 小改动、探索性任务、快速修复 | 绿色 |
| **PLAN** | 用户输入 → Agent 生成计划卡片 → 用户批准 → 执行 | 复杂重构、多步骤任务 | 蓝色 |
| **TEAM** | team.yaml 驱动多 Agent 协作（模式由团队 YAML 固定声明：Magentic / GroupChat / ParallelDag，运行期不可覆盖，只能通过切换团队改变执行方式） | 方案评审、复杂多步协调 | 紫色 |
| **GOAL** | Agent 自主分解目标、迭代执行、AI 验证完成度 | 开放式高层目标 | 青色 |

**模式切换方式**：
- `Tab`（输入框内）→ 循环 BUILD → PLAN → TEAM → GOAL

### 权限与安全系统

8 种 `PermissionMode`：

| 模式 | 行为 |
|------|------|
| Default | 每次危险操作都询问用户 |
| Plan | 规划模式，只读不执行（PLAN 工作模式自动派生） |
| Auto | 自动模式（配合 Yolo 分类器） |
| AcceptEdits | 自动批准文件编辑 |
| BypassPermissions | 跳过所有权限检查（Layer 0 安全不变量仍生效） |
| DontAsk | 不询问，直接拒绝危险操作 |
| GoalAuto | GOAL 模式自动派生：自主执行不中断，危险 Shell 直接 Deny |
| Team | TEAM 模式自动派生：多 Agent 协作，危险命令走事件审批 |

> Plan / GoalAuto / Team 由对应工作模式经 `WorkingModeBridge` 自动派生，`/permissions` 不接受这三个值
> （会造成权限轴与模式轴不一致的半状态）——切换工作模式用 Tab / Alt+1..4。
> **会话级一次性配置**：`/permissions bypass --session` 让本次对话自动执行所有操作而不写配置文件；
> 审批弹窗中也可直接选「本次对话全部允许」。两者都不影响 Layer 0 安全不变量（危险命令 / 敏感路径 / 符号链接越狱）。

**12 层渐进式安全带机制**：核心循环 → 工具调度 → 计划 → 子代理 → 按需知识 → 上下文压缩 → 持久化任务 → 后台任务 → 代理团队 → 团队协议 → 自主代理 → 工作树隔离。

**命令分类器**：
- `BashCommandClassifier`：Shell 命令 Safe / Warning / Dangerous 三级分类
- `PowerShellCommandClassifier`：500+ 行实现，覆盖别名解析、编码绕过检测、管道复合命令分析
- `YoloClassifier`：基于规则的自动分类器，支持 allow / deny / ask 三种策略

### 子代理与多代理

```
主代理 (MainAgentRunner)
    ├── simple    → 单次派生子代理（ForkedAgentRunner）
    ├── magentic  → Orchestrator + Workers 并发（MAF MagenticWorkflow）
    ├── groupchat → 循环发言（MAF GroupChatWorkflow）
    └── DAG       → 依赖并行（ParallelAgentsTool → AgentTaskWorkflowCompiler + AgentTaskWorkflowHost）

配置：~/.onecode/teams/{name}/team.yaml
```

**Agent 8 色身份系统**：orchestrator 紫、researcher 蓝、planner 绿、executor 橙、reviewer 黄、tester 红、debugger 粉、assistant 青。

### 记忆系统

两级作用域：User（全局永久 `~/.onecode/memory/MEMORY.md`）/ Project（仓库本地 `{cwd}/.onecode/memory/MEMORY.md`）。`AutoDreamService` 后台自动整合记忆（四重门控 + 跨进程文件锁，配置走 `settings.json` 的 `autodream.*` 键）。

### Hook 系统

11 种钩子事件覆盖全生命周期（PreToolUse / PostToolUse / Notification / UserPromptSubmit / SessionStart / Stop / StopFailure / PreCompact / PostCompact / SessionEnd / GoalStageInvoke），3 种执行器（Command / Notification / Http）。退出码 `2` 可阻断工具调用。

### MCP 集成

5 种传输协议（Stdio / SSE / HTTP Streamable / WebSocket / InProcess），多作用域配置加载（user / project / local 三层同名覆盖，另有内置服务清单兜底）。

启动采用后台预连接：trust 流程通过后连接在后台进行，TUI 首屏立即渲染；首条消息发送前对进行中的连接做 ≤5s 有界收尾（超时放行，工具下一轮附挂）。状态栏以三态显示连接进度（`连接中 x/y` → `已连 n · Nt` → 全部失败时 `MCP: ✗k 失败`），启动失败另有一次性 startup hint 给出逐台明细与 `/mcp connect` 重试指引，失败原因在 `/mcp list` 中始终可见。`/mcp install` 默认安装后立即连接验证（`--no-connect` 跳过），坏包当场暴露。

### 上下文压缩

多级压缩策略：Harness 策略入口（`HarnessAgentOptions.CompactionStrategy`）驱动 in-pipeline 自动压缩（工具调用瘦身 + LLM 摘要，含摘要守卫与预算校验）+ `AutoCompactService`（转写规模提示）+ 手动 `/compact`（`CompactService`，支持全文/部分两种模式）。

---

## TUI 交互

### 四层布局

```
┌─ Title Bar ──────────────────────────────────────────┐  1 row
│ ● Code Assistant  [● Magentic]  │ orchestrator │ F1 F2 F3 ?│
├──────────────────────────────────────────────────────┤
│                                                       │
│              CHAT — 唯一主视图                          │  Dim.Fill()
│              （BUILD / PLAN / TEAM / GOAL 共享）        │
│                                                       │
├─ Status Bar ─────────────────────────────────────────┤  1 row
│  Opus · $0.04 · Sandbox          BUILD · 13:19       │
├─ Input Line ─────────────────────────────────────────┤  3+ rows
│ BUILD ❯ _                                  Tab切模式  │
└──────────────────────────────────────────────────────┘
```

**设计原则**：Chat 是唯一永久主视图，无侧边栏、无底部永久面板，辅助功能按需弹出覆盖层。

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

`/add-dir` `/compact` `/config` `/copy` `/cron` `/design-init` `/exit` `/fastmodel` `/files` `/help` `/hooks` `/init` `/keybindings` `/loop` `/lsp` `/model` `/permissions` `/prompts` `/skills` `/team` `/think` `/upgrade` `/version`

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
| **Agent 间消息路由** | `TeamOrchestrationService` 多 Agent 协作编排，Channel<T> 消息传递 |
| **AutoDream 记忆整合** | 后台自动提取关键信息写入记忆文件 |
| **Git Worktree 管理** | `EnterWorktreeTool` / `ExitWorktreeTool`，任务级隔离 |
| **Cron 定时任务** | 单 `Cron` 工具（`action` = create/list/delete/pause/resume），跨会话持续运行 |
| **自包含单文件发布** | .NET 10 self-contained + single-file |
| **多级上下文压缩** | MAF 自动压缩 + 手动 `/compact`（全文/部分）|
| **Token 精确估算** | `TokenBreakdownEstimator` 多模型 token 精确计算 |
| **VCR 录制回放** | `VcrService` API 调用录制与回放 |
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
| GOAL 模式流式输出 | 不支持自动重试（非流式模式支持 PromptTooLong 恢复） |
| Team 子 Agent 审批 | 审批请求经 Team 工作流桥（`TeamWorkflowRunner` → `OrchestrationEvent.ApprovalRequest`）呈现；「总是允许」暂按单次批准生效（standing rule 包装未经验证，见 ADR 0007 §5.1） |
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
