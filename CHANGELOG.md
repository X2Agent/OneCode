# Changelog

本项目的所有重要变更将记录在此文件中。

## [1.0.0] - 2026-08-30

首个正式发布。生产级 CLI AI 编程助手：.NET 10 + Terminal.Gui v2 全屏 TUI + MAF (Microsoft Agent Framework) 编排管道。

### 核心能力

- **四种工作模式**：BUILD / PLAN / TEAM / GOAL，`Tab` 循环切换，统一工作流运行时（ADR 0007）
- **44 个斜杠命令**（Builtin / Session / Diagnostic / Skill / Git 五类）与 **30 个内置工具**（文件、LSP、Web、子代理、Cron、Git Worktree、MCP）
- **权限与安全**：9 种权限模式、Shell 命令三级分类器（含 PowerShell 方言）、10 事件 × 3 执行器的 Hook 系统、可回滚编辑事务
- **多代理编排**：simple / magentic / groupchat / DAG 四种子代理拓扑，Agent 8 色身份系统
- **MCP 集成**：5 种传输协议（Stdio / SSE / HTTP Streamable / WebSocket / InProcess），多作用域配置 + OAuth 2.0
- **Hyperlight 沙箱**（preview，运行时不可用自动降级）、LSP 集成、代码语义索引
- **跨会话能力**：三级作用域记忆 + AutoDream 自动整合、跨会话 Cron 定时任务、会话恢复

### 近期架构改进（自初始提交以来）

- ADR 0007 Stage 1：WorkingMode 下沉统一、WorkflowTopology 收敛 5 处拓扑实现（净删约 130 行）
- 抽象层收敛：IFileSystem / IProcessRunner / IChatClientFactory / Config / Mcp 等抽象下沉至 OneCode.Core
- Query 流式编排重构：QueryStreamEngine 拆分为 BuildPreambleRunner / ToolAssembler / HookDispatcher / TranscriptPersistence，可变状态收敛至 StreamingSession（ADR 0006）
- 成本追踪子系统由 CostTracker 替换为 TokenLedger
- 新增 ReasoningPassback 管道：DeepSeek thinking 模式下 assistant 消息 reasoning_content 自动回传（避免 HTTP 400）
- 测试套件扩充至 222 个测试文件、1685+ Fact、129+ Theory

### 已知限制

- GOAL 模式流式输出不支持自动重试（非流式支持 PromptTooLong 恢复）
- MAF `ToolApprovalAgent` 与 `RunStreamingAsync` 不兼容，权限审批由自研中间件实现
- `Microsoft.Agents.AI.Hyperlight` 为 preview 包，沙箱功能受限
- TUI 不支持鼠标点击模式标签

### 下载

见 [GitHub Releases](https://github.com/X2Agent/OneCode/releases)：6 个平台（win/linux/osx × x64/arm64）的 self-contained 单文件压缩包，附 SHA256 校验和；或使用安装脚本一键安装。
