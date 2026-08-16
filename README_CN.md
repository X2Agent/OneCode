# OneCode .NET — 生产级 CLI AI 编程助手

> **导读**：OneCode .NET 是一个生产级 CLI AI 编程助手，基于 Claude Code TypeScript 版本的完整 C# 重写。项目采用 .NET 10 + MAF (Microsoft Agent Framework) 1.11 构建，在忠实移植原版核心能力的同时，引入了 Hyperlight 沙箱、LSP 集成、代码索引、DAG 并行调度等增强功能。

> **免责声明**: 本仓库内容仅用于技术研究和科研爱好者交流学习参考，**严禁任何个人、机构及组织将其用于商业用途、盈利性活动、非法用途及其他未经授权的场景。** 若内容涉及侵犯您的合法权益、知识产权或存在其他侵权问题，请及时联系我们，我们将第一时间核实并予以删除处理。

**仓库地址**: [https://github.com/X2Agent/OneCode](https://github.com/X2Agent/OneCode)  
**当前版本**: 1.0.0  
**语言**: [English](README.md) | **中文** 

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
- [深度分析文档](#深度分析文档-docs)
- [已知限制](#已知限制)
- [开发规划](#开发规划)

---

## 技术栈

| 维度 | TypeScript 原版 | C# 重写版 |
|------|----------------|-----------|
| 运行时 | Bun (Node.js) | .NET 10 |
| UI 框架 | React + Ink | Terminal.Gui v2.4 |
| 异步模型 | Promise | Task + async/await |
| 包管理 | npm/bun | NuGet (CPM) |
| 测试框架 | Jest | xUnit v3 + NSubstitute + FluentAssertions |
| Agent 框架 | 自研 while 循环 | MAF 1.11 (ChatClientAgent + 中间件管道) |
| 发布方式 | JavaScript bundle | .NET 自包含单文件 |

**MAF 包依赖**：

| 包名 | 版本 | 状态 | 用途 |
|------|------|------|------|
| `Microsoft.Agents.AI` | 1.11.1 | 稳定 | 核心 Agent 框架 |
| `Microsoft.Agents.AI.Workflows` | 1.11.1 | 稳定 | 工作流引擎（Magentic / GroupChat） |
| `Microsoft.Agents.AI.Mcp` | 1.10.0-alpha | ⚠️ 实验性 | MCP 协议集成，API 可能变更 |
| `Microsoft.Agents.AI.Hyperlight` | 1.9.0-preview | ⚠️ 实验性 | 沙箱代码执行，默认启用 |
| `Microsoft.Agents.AI.Tools.Shell` | 1.10.0-preview | ⚠️ 实验性 | Shell 工具集成 |
| `Microsoft.Extensions.AI` | 10.* | 稳定 | AI 抽象层（OpenAI / Anthropic） |

**其他关键依赖**：

| 包名 | 版本 | 用途 |
|------|------|------|
| `Terminal.Gui` | 2.4.13 | 全屏终端 UI（Kitty 键盘协议） |
| `ModelContextProtocol` | 1.4.0 | MCP 官方 SDK |
| `Markdig` | 1.3.2 | Markdown 渲染 |
| `Hyperlight` | 0.5.0 | 微虚拟机沙箱 |
| `SSH.NET` | 2025.1.0 | SSH 远程执行 |
| `YamlDotNet` | 18.1.0 | YAML 配置 |
| `DiffPlex` | 1.9.0 | Diff 计算 |
| `Cronos` | 0.13.0 | Cron 表达式 |

> **已知限制**：
> - `Microsoft.Agents.AI.Mcp` (alpha)：API 可能在未来版本变更
> - `Microsoft.Agents.AI.Hyperlight` (preview)：沙箱功能受限，默认启用（运行时不可用则静默降级）
> - GOAL 模式流式输出不支持自动重试（非流式模式支持 PromptTooLong 恢复）
> - MAF `ToolApprovalAgent` 与 `RunStreamingAsync` 不兼容，权限审批通过自研中间件实现

---

## 项目结构

```
src/
├── OneCode.Cli/                 # CLI 入口 · 快速路径分发（4 文件）
│   ├── Program.cs              #   主入口
│   ├── CliModeDetector.cs      #   快速路径检测
│   └── FastPathDispatcher.cs   #   特殊模式快速分发
│
├── OneCode.App/                 # 工具实现 · 命令 · TUI · 服务组合（345 文件）
│   ├── Tools/                  #   45+ 工具实现（9 个 ToolProvider，63 个 AIFunction）
│   ├── Commands/               #   42 斜杠命令
│   ├── Middleware/             #   14 个管道中间件（9 实现 + 5 契约/不变量）
│   ├── Services/               #   业务服务（Agent、Memory、Plan、Skills 等 18 子模块）
│   ├── Tui/                    #   Terminal.Gui v2 全屏界面（63 文件）
│   ├── AgentPipelineBuilder.cs #   MAF 管道工厂（核心）
│   └── ServiceCollectionExtensions*.cs  #   DI 注册
│
├── OneCode.Core/                # 纯接口与领域模型（126 文件，仅依赖 3 个 Microsoft.Extensions.*.Abstractions 抽象包）
│   ├── Permissions/            #   权限系统（7 种策略 + Bash 分类器）
│   ├── Hooks/                  #   27 事件生命周期扩展
│   ├── Tasks/                  #   任务服务抽象
│   ├── Tools/                  #   ITool / ToolBuilder Fluent API
│   ├── Keybindings/            #   按键绑定系统（6 文件）
│   ├── Commands/               #   命令抽象（10 文件）
│   ├── Domain/                 #   领域模型
│   └── SystemPrompt/           #   系统提示词（7 文件）
│
├── OneCode.Infrastructure/      # 外部系统适配（56 文件）
│   ├── Mcp/                    #   MCP 协议（5 种传输 + OAuth 2.0）
│   ├── Git/                    #   Git 文件系统（无需 git 二进制）
│   ├── Memory/                 #   文件系统记忆存储
│   └── Config/                 #   YAML 配置管理 + 常量定义
│
└── OneCode.Tests/               # xUnit v3 测试套件（85+ 文件，1385+ 测试方法）
    ├── *Tests.cs               #   单元测试
    └── AGENTS.md               #   测试规范约束
```

---

## 分层架构

```
┌─────────────────────────────────────────────────────────────────┐
│  OneCode.Cli  (4 文件)                                          │
│  CLI 入口 · 快速路径分发 · CliModeDetector · FastPathDispatcher │
└──────────────────────────────┬──────────────────────────────────┘
                               │
┌──────────────────────────────▼──────────────────────────────────┐
│  OneCode.App  (345 文件)                                        │
│  工具实现 · 命令 · TUI · 服务组合 · MAF 集成中枢                  │
│  45+ Tools · 42 Commands · 14 Middleware · AgentPipelineBuilder │
└───────────┬─────────────────────────────────┬───────────────────┘
            │                                 │
┌───────────▼──────────┐          ┌───────────▼───────────────────┐
│  OneCode.Core    │◄─────────│  OneCode.Infrastructure        │
│  (126 文件)          │          │  (56 文件)                     │
│  纯接口与领域模型     │          │  外部系统适配                  │
│  Permissions/Hooks/  │          │  MCP · Git · Memory · Process │
│  Tasks/Tools/Domain  │          │  Config · LSP · CodeIndex     │
└──────────────────────┘          └───────────────────────────────┘

            OneCode.Tests (85+ 文件) → 测试以上所有层
```

**依赖方向**：Cli → App → Core ← Infrastructure，Core 层保持纯抽象，不引入外部实现依赖。

---

## 产品能力矩阵

| # | 能力 | 完整度 | 关键实现 |
|---|------|:------:|---------|
| 1 | Plan Mode 规划优先模式 | 95% | `PlanModePermissionStrategy` + `EnterPlanModeTool` |
| 2 | Subagents 并行子 Agent | 97% | `AgentTool` + `ParallelAgentsTool` + `ForkedAgentRunner` |
| 3 | Skills 斜杠命令工作流 | 92% | `BundledSkills`（9 个内置）+ `SkillTool` |
| 4 | Hooks 生命周期扩展 | 95% | `HookRegistry`（10 事件 × 3 执行器） |
| 5 | MCP Servers 外部服务集成 | 90% | `McpConnectionManager`（5 种传输协议） |
| 6 | AGENTS.md 目录级约束 | 88% | 6 个目录级约束文档 |
| 7 | Memory 跨会话记忆 | 95% | `MemoryService` + `AutoDreamService` |
| 8 | Code Search 代码检索 | 94% | `GrepTool` + `SymbolSearchTool` + `FindReferencesTool` |
| 9 | Multi-file Edits 跨文件编辑 | 88% | `EditTool`（精确匹配）+ `WriteTool` |
| 10 | Git Integration | 92% | 7 个 Git 命令 + `GitFilesystem`（无需 git 二进制） |
| 11 | Deep Reasoning 深度思考 | 88% | `EffortThinking`（4 级强度） |
| 12 | Web Search 网络搜索 | 87% | `WebSearchTool`（Brave + DuckDuckGo 双引擎） |
| 13 | Terminal Execution | 92% | `BashTool` + `PowerShellTool` + `REPLTool` |
| 14 | Headless Mode CI/CD 模式 | 90% | `--no-input` + `--permission-mode` |
| 15 | Code Review 代码审查 | 85% | `/review` 斜杠命令（--staged / --output json / LSP / blame / 增量） |
| 16 | Sandboxed Execution 沙箱 | 85% | `HyperlightCodeActService`（默认启用，自动挂载工作目录） |
| 17 | Background Tasks 后台任务 | 92% | `TaskService` + 6 个 Task 工具 + `CronTools` |
| 18 | Theming 主题系统 | 85% | `TuiTheme`（6 套内置 + 自定义） |

**综合完成度：17/18 FULL · 1/18 PARTIAL · 平均 91%**

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

安装脚本从 `X2Agent/OneCode` 的 latest GitHub Release 下载与当前系统匹配的资产，并在无法获取 Release 时直接失败，不会回退到虚构版本。安装后运行 `onecode --help` 开始使用。

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

# Headless 模式（CI/CD）—— --no-input 禁用交互输入
onecode --no-input "分析项目结构" --permission-mode acceptEdits

# 指定权限模式启动
onecode --permission-mode acceptEdits "重构用户认证模块"
```

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
ONECODE_PROVIDER=openai
ONECODE_BASE_URL=https://opencode.ai/zen/v1
ONECODE_API_KEY=public
ONECODE_MODEL=deepseek-v4-flash-free
```

> **原理**：Zen 后端接受 `"public"` 作为 API Key，允许访问所有免费模型（付费模型自动过滤）。如需使用付费模型，请在 [opencode.ai/auth](https://opencode.ai/auth) 注册并获取真实 API Key 替换 `"public"`。完整免费模型列表见 [models.dev/providers/opencode](https://models.dev/providers/opencode)。

---

## 工具系统

工具通过 9 个 `ToolProvider` 分组注册，总计 **63 个 AIFunction**（45+ 个工具类型）。

### Shell 工具

| 工具 | 功能 |
|------|------|
| Bash | Unix shell 命令执行（含安全分类） |
| PowerShell | Windows PowerShell / pwsh 执行 |
| REPL | 交互式 REPL 会话 |

### 文件操作

| 工具 | 功能 |
|------|------|
| Read | 文件读取（分页、二进制检测） |
| Write | 创建 / 覆写文件 |
| Edit | 搜索替换编辑（要求唯一匹配） |
| LS | 目录列表 |
| Glob | Glob 模式文件搜索 |
| Grep | 正则内容搜索（ripgrep 风格） |

### Web 工具

| 工具 | 功能 |
|------|------|
| WebFetch | 抓取网页（HTTP→Markdown；已连接 Playwright MCP 时自动用 navigate/snapshot 兜底 SPA） |
| WebSearch | 网络搜索（Brave + DuckDuckGo） |

### 任务管理

| 工具 | 功能 |
|------|------|
| TodoWrite / TodoRead | 会话级待办清单 |
| TaskCreate / TaskList / TaskGet / TaskStop / TaskUpdate / TaskOutput | 后台任务 CRUD |
| BackgroundRun / BackgroundWait | 后台命令执行 |

### MCP / LSP

| 工具 | 功能 |
|------|------|
| mcp__{server}__{tool} | MCP 服务器工具（自动前缀，直接可调用） |
| ListMcpResources / ReadMcpResource | MCP 资源枚举与读取 |
| LspTool | LSP 语言服务集成 |
| SymbolSearch | 代码符号搜索（Code Index） |
| FindReferences | 查找符号引用（LSP） |

### Agent / Skill

| 工具 | 功能 |
|------|------|
| Agent | 派生子代理（支持 fork / worktree / remote 模式） |
| Skill | 技能调用（斜杠命令） |
| Concurrent | 并发执行多个子代理 |
| Workflow | YAML / JSON 多步骤工作流 |
| ParallelAgents | DAG 调度并行代理 |
| SendMessage | Agent 间消息传递 |

### Plan Mode / Team

| 工具 | 功能 |
|------|------|
| EnterPlanMode / ExitPlanMode | 规划模式控制 |
| CreatePlan / VerifyPlanExecution | 执行计划管理 |
| TeamCreate / TeamDelete / TeamList / TeamAdd / TeamRemove / TeamStatus | 多代理团队管理 |

### 实用工具

| 工具 | 功能 |
|------|------|
| mcp__playwright__* | 浏览器自动化（可选；`/mcp add playwright --transport stdio --command npx --args @playwright/mcp@latest --scope user --connect`） |
| AskUserQuestion | 向用户提问 |
| EnterWorktree / ExitWorktree | Git Worktree 管理 |
| CronCreate / CronDelete / CronList | 定时任务 |
| Snip / CtxInspect / VerifyPlanExecution | 检查工具组 |
| ToolSearch | 工具搜索（自动追加） |
| SubscribePR / UnsubscribePR / ListPRSubscriptions / PollPR | PR 订阅（4 个方法） |

---

## 核心架构特性

### MAF Agent 管道

```
IChatClient → .AsBuilder() → 注入 8 种 AIContextProvider
    ↓
ChatClientAgent
    ↓
[Run 级中间件 — 包裹整个 Agent Run]
.Use(BudgetGuardRunMiddleware)        ← 预算熔断（pre-execution 检查，超支短路）
.Use(UsageTrackingRunMiddleware)      ← Token 成本记录（更新 CostTracker）
.Use(PromptTooLongRecoveryRunMiddleware) ← PromptTooLong 异常恢复（截断重试）
    ↓
[Function 级中间件 — 包裹每次工具调用]
.Use(SafetyInvariantMiddleware)       ← 安全不变量检查
.Use(HookMiddleware)                  ← Hook 包裹（Pre/Post 工具调用）
.Use(ToolCallEventMiddleware)         ← 工具调用事件发射
.Use(PermissionAndLimitMiddleware)    ← 权限校验 + 工具调用上限
.Use(StateMachineMiddleware)          ← 状态机管理
.Use(EditTransactionMiddleware)       ← 编辑事务（可回滚的原子操作）
.Use(VerificationMiddleware)          ← 编辑后验证（编译/测试）
.Use(RecoverableErrorMiddleware)      ← API 级错误恢复
.Use(ToolExecutionBudgetMiddleware)   ← 工具执行预算（防止无限循环）
.Use(ToolResultUnwrapMiddleware)      ← ToolResult 解包
.Use(ContractMiddleware)              ← 行为契约验证
.Use(SequenceDetectionMiddleware)     ← 序列检测（防止重复操作）
.Use(TaskRecoveryMiddleware)          ← 任务恢复
    ↓
.UseOpenTelemetry()                   ← 审计日志（通过 ActivitySource 转发到 ILogger）
    ↓
.Build() → AIAgent
```

**8 种 AIContextProvider**：

| Provider | 用途 |
|----------|------|
| `MemoryFileContextProvider` | 按需记忆检索（`search_memories` 工具触发） |
| `SessionMemoryContextProvider` | 会话级事实记忆自动提取与注入 |
| `PlanModeAttachmentProvider` | Plan 模式指令注入 |
| `AgentSkillsProvider` | Skills 热替换（文件变更自动重载） |
| `AgentModeProvider` | 工作模式感知（Build / Plan / Team / Goal） |
| `ShellEnvironmentProvider` | Shell 执行环境上下文 |
| `HyperlightCodeActProvider` | Hyperlight 沙箱执行上下文 |
| `CompactionProvider` | 上下文压缩（超出 token 预算时自动触发） |

### 四种工作模式

| 模式 | 行为特征 | 适用场景 | 视觉色系 |
|------|----------|----------|----------|
| **BUILD** | 用户输入 → Agent 直接分析并执行 | 小改动、探索性任务、快速修复 | 绿色 |
| **PLAN** | 用户输入 → Agent 生成计划卡片 → 用户批准 → 执行 | 复杂重构、多步骤任务 | 蓝色 |
| **TEAM** | team.yaml 騨动多 Agent 协作（Magentic / GroupChat） | 方案评审、复杂多步协调 | 紫色 |
| **GOAL** | Agent 自主分解目标、迭代执行、AI 验证完成度 | 开放式高层目标 | 青色 |

**模式切换方式**：
- `Tab`（输入框内）→ 循环 BUILD → PLAN → TEAM → GOAL

### 权限与安全系统

7 种 `PermissionMode`：

| 模式 | 行为 |
|------|------|
| Default | 每次危险操作都询问用户 |
| Plan | 规划模式，只读不执行 |
| AcceptEdits | 自动批准文件编辑 |
| BypassPermissions | 跳过所有权限检查 |
| Bubble | 气泡式权限提示 |
| AutoMode | 自动模式（配合 Yolo 分类器） |
| DontAsk | 不询问，直接拒绝危险操作 |

**12 层渐进式安全带机制**：核心循环 → 工具调度 → 计划 → 子代理 → 按需知识 → 上下文压缩 → 持久化任务 → 后台任务 → 代理团队 → 团队协议 → 自主代理 → 工作树隔离。

**命令分类器**：
- `BashCommandClassifier`：Shell 命令 Safe / Warning / Dangerous 三级分类
- `PowerShellCommandClassifier`：504 行实现，覆盖别名解析、编码绕过检测、管道复合命令分析
- `YoloClassifier`：基于规则的自动分类器，支持 allow / deny / ask 三种策略

### 子代理与多代理

```
主代理 (MainAgentRunner)
    ├── simple    → 单次派生子代理（ForkedAgentRunner）
    ├── magentic  → Orchestrator + Workers 并发（MAF MagenticWorkflow）
    ├── groupchat → 循环发言（RoundRobinGroupChatManager）
    └── DAG       → 拓扑排序 + 依赖并行（ParallelAgentsTool + AgentTaskDagScheduler）

通信：SendMessageTool + TeammateMailbox（Channel<T>）
配置：~/.onecode/teams/{name}/team.yaml
```

**Agent 8 色身份系统**：orchestrator 紫、researcher 蓝、planner 绿、executor 橙、reviewer 黄、tester 红、debugger 粉、assistant 青。

### 记忆系统

三级作用域：User（全局永久 `~/.onecode/memory/`）/ Session（当前会话）/ Repo（仓库本地 `.onecode/memory/`）。`AutoDreamService` 后台自动整合记忆（四重门控 + 跨进程文件锁，配置走 `settings.json` 的 `autodream.*` 键）。

### Hook 系统

27 个事件覆盖全生命周期，7 种执行器（Agent / Callback / Command / Function / Http / Notification / Prompt）。退出码 `2` 可阻断工具调用。

### MCP 集成

5 种传输协议（Stdio / SSE / HTTP Streamable / WebSocket / InProcess），多作用域配置加载（user / workspace / project），OAuth 2.0 完整支持。

### 上下文压缩

多级压缩策略：AutoCompact（80% 阈值）→ MicroCompact（单条过长）→ SnipCompact（手动 `/compact`）→ ContextCollapse（上下文超限）。

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

### 主题系统

6 套内置主题 + YAML/JSON 自定义主题，运行时热切换：default / dark / light / solarized-dark / solarized-light / monokai。

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

42 个斜杠命令覆盖完整开发工作流：

### Git 工作流命令（6 个）

`/branch` / `/commit` / `/diff` / `/rebase` / `/review` / `/stash`

### 会话与上下文命令（8 个）

`/add-dir` / `/checkpoint` / `/compact` / `/export` / `/files` / `/rename` / `/session` / `/status`

### 配置与系统命令（11 个）

`/config` / `/doctor` / `/exit` / `/fastmodel` / `/help` / `/init` / `/install` / `/keybindings` / `/model` / `/permissions` / `/upgrade`

### AI / Agent 命令（5 个）

`/hooks` / `/lsp` / `/mcp` / `/memory` / `/think`

### 工具与调试命令（3 个）

`/copy` / `/insights` / `/version`

### Skill 与扩展命令（4 个）

`/skills` / `/tools` / `/prompts`

---

## 增强功能亮点

以下功能为 C# 重写版独有，TypeScript 原版不具备：

| 功能 | 说明 |
|------|------|
| **Hyperlight 沙箱** | 微虚拟机隔离代码执行，支持文件挂载控制和网络域名白名单 |
| **LSP 集成** | `EnhancedLspService` + `LspServerManager`，编辑后实时诊断更新 |
| **代码语义索引** | `CodeIndexService` 语义符号搜索（精确 / 模糊 / 关键字匹配） |
| **DAG 并行调度** | `ParallelAgentsTool` + `AgentTaskDagScheduler`，依赖驱动的并行 |
| **Agent 间消息路由** | `TeamOrchestrationService` + `TeammateMailbox`，Channel<T> 消息传递 |
| **AutoDream 记忆整合** | 后台自动提取关键信息写入记忆文件 |
| **Git Worktree 管理** | `EnterWorktreeTool` / `ExitWorktreeTool`，任务级隔离 |
| **Cron 定时任务** | `CronTools` 跨会话持续运行的定时任务 |
| **自包含单文件发布** | .NET 10 self-contained + single-file |
| **Vim 模式** | 5 种子模式完整 Vim 键绑定（Normal / Insert / Visual / VisualLine / Replace） |
| **主题系统** | 6 套内置主题 + 自定义，运行时热切换，Agent 8 色区分 |
| **多级上下文压缩** | Auto / Micro / Snip / Collapse 四级策略 |
| **Token 精确估算** | `TokenBreakdownEstimator` 多模型 token 精确计算 |
| **VCR 录制回放** | `VcrService` API 调用录制与回放 |
| **SSH 远程执行** | `SshRemoteService` 基于 SSH.NET 的远程命令执行 |
| **飞书/企业微信通知** | Hook 通知支持飞书和企业微信 |

---

## 配置与命令参考

`docs/` 目录下提供以下工程参考文档：

| 文档 | 说明 |
|------|------|
| [docs/commands.md](docs/commands.md) | 全部 42 个斜杠命令的功能说明、用法与参数详解，按 `Builtin` / `Session` / `Diagnostic` / `Skill` / `Git` 5 类组织 |
| [docs/settings.md](docs/settings.md) | `settings.json` 全部合法配置项、默认值、优先级与环境变量说明 |
| [docs/skills.md](docs/skills.md) | 9 个内置技能（BundledSkills）的逐个说明、参数占位符规则、自定义技能开发指南 |

---

## 深度分析文档 (`docs/`)

基于网络公开资料与社区讨论整理的 Claude Code v2.1.88 分析报告，中英双语。

```
docs/
├── en/                                        # English
│   ├── 01-telemetry-and-privacy.md            # Telemetry & Privacy
│   ├── 02-hidden-features-and-codenames.md    # Codenames & Feature Flags
│   ├── 03-undercover-mode.md                  # Undercover Mode
│   ├── 04-remote-control-and-killswitches.md  # Remote Control
│   └── 05-future-roadmap.md                   # Future Roadmap
│
└── zh/                                        # 中文
    ├── 01-遥测与隐私分析.md
    ├── 02-隐藏功能与模型代号.md
    ├── 03-卧底模式分析.md
    ├── 04-远程控制与紧急开关.md
    └── 05-未来路线图.md
```

---

## 已知限制

| 限制项 | 说明 |
|--------|------|
| `Microsoft.Agents.AI.Mcp` (alpha) | API 可能在未来版本变更 |
| `Microsoft.Agents.AI.Hyperlight` (preview) | 沙箱功能受限，默认启用（运行时不可用则静默降级） |
| GOAL 模式流式输出 | 不支持自动重试（非流式模式支持 PromptTooLong 恢复） |
| MAF `ToolApprovalAgent` | 与 `RunStreamingAsync` 不兼容，权限审批通过自研中间件实现 |
| 鼠标点击模式标签 | Terminal.Gui 中未实现鼠标处理 |
| 依赖版本锁定 | 约 15 个浮动版本包待锁定（发版后处理） |

---

## 开发规划

项目的后续开发方向和重构内容详见 [重构优化规划文档](OneCode.重构优化规划.md)，主要包含：

- **可控性重构**：4 个 Phase（共享上下文 → 行为契约 → Workflow/Eval → 审计度量）
- **功能增强**：Code Review 结构化、沙箱执行增强、LSP 深度集成
- **测试改进**：覆盖率提升、安全关键测试补充
- **长期方向**：Workflow YAML 验收、可观测性仪表盘、记忆回路优化

---

## 许可证

本仓库内容仅用于技术研究和教育目的。知识产权归原公司所有，若有侵权请联系删除。
