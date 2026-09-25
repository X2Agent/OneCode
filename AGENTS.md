# AGENTS.md

This file provides guidance to AI agents working with this codebase.
Keep it updated as the project evolves.

## Project Overview

OneCode .NET 是一个基于 .NET 10 的生产级 CLI AI 编程助手，采用 Terminal.Gui v2 全屏 TUI + Microsoft.Extensions.AI 抽象 + Microsoft.Agents.AI (MAF) 编排框架。四种工作模式（BUILD / PLAN / TEAM / GOAL），45 个斜杠命令，31 个工具。

**语言**：代码注释与文档以中文为主。详细规范见 [src/AGENTS.md](src/AGENTS.md)（强制编码规范）及各 csproj 的专属 AGENTS.md。

## Build & Test Commands

```bash
dotnet restore src/OneCode.slnx
dotnet build src/OneCode.slnx        # 无新增警告（历史警告不得扩大）
dotnet test src/OneCode.slnx         # 全部通过
dotnet publish src/OneCode.Cli/OneCode.Cli.csproj -c Release   # 发布 CLI
```

要求 .NET SDK 10。测试栈：xUnit v3 + FluentAssertions + NSubstitute。

## Project Architecture

```
src/
├── OneCode.Cli/            # CLI 入口 · 快速路径分发（6 文件）
├── OneCode.App/            # 组合与实现层：Modes/Tools/Commands/Tui/Services/DI 注册（~400 文件）
├── OneCode.Core/           # 纯接口与领域模型（仅依赖 *.Abstractions）
├── OneCode.Infrastructure/ # 外部系统适配：MCP / MAF 管道 / 配置 / Git / Memory
├── OneCode.Automation/     # 后台调度：Cron / ModelCatalog 刷新 / YOLO 规则加载
└── OneCode.Tests/          # xUnit v3 测试套件
```

依赖方向单向：Cli → App → Infrastructure → Core。SDK 特化代码只允许出现在 Infrastructure。

## Coding Conventions

- **强制规范**：全部见 [src/AGENTS.md](src/AGENTS.md)。要点：简洁优先、不留兼容层、不保留死代码、C# 最新语法、源生成器优先、Central Package Management、单类 ≤500 行（硬上限 600）、构造注入 ≤8 参数。
- **测试**：见 [src/OneCode.Tests/AGENTS.md](src/OneCode.Tests/AGENTS.md)——测试即防回归，8 类无意义测试禁写；跨存储边界的契约测试须走真实写入端，守卫类逻辑须做反证。
- **Prompt 管理**：文件化 prompt（三层覆盖），缺失处理双策略见 [src/OneCode.App/AGENTS.md](src/OneCode.App/AGENTS.md)。
- **快捷键**：默认绑定源头在 `src/OneCode.Core/Keybindings/KeybindingDefaults.cs`，参考 [docs/keybindings.md](docs/keybindings.md)。
- **命令注册真相源**：`src/OneCode.App/Commands/CommandServiceCollectionExtensions.cs` 的 `AddCommands()`，新增/删除命令须同步 [docs/commands.md](docs/commands.md)。
- **MAF 接入**：装配顺序、扩展点与现状对照见 [docs/maf/integration-guide.md](docs/maf/integration-guide.md)；集成边界与禁令见 [docs/adr/0007-maf-integration-boundaries.md](docs/adr/0007-maf-integration-boundaries.md)；结构化输出采用策略（结构化请求 + 文本降级）见 [docs/adr/0008-structured-output-text-fallback.md](docs/adr/0008-structured-output-text-fallback.md)。
- **子代理派工**：派工入口、profile 能力矩阵、TaskService 生命周期与 DAG 语义见 [docs/sub-agents.md](docs/sub-agents.md)；「不起子代理的决定权归属」边界（为何不用 MAF BackgroundAgents 作底层）见 [docs/adr/0011-background-agents-delegation-boundary.md](docs/adr/0011-background-agents-delegation-boundary.md)。
- **决策模型接入**：抽象归属、接入点白名单与禁令见 [docs/adr/0012-decision-model-integration-jev.md](docs/adr/0012-decision-model-integration-jev.md)——决策模型（如 TypeSafe Jev）只能收窄已授权候选集或把 Allow 升级为 Ask/Deny，**绝不能产生 Allow**；上游未定型期间实验类型不进依赖面。

## Agent Guidelines

- 修改任何项目前先读该项目的 AGENTS.md；冲突时以子目录文档为准。
- PR 标题格式 `[dotnet] <描述>`；提交前必须 build + test 通过。
- TODO 注释必须关联 GitHub Issue：`// TODO(#123): ...`
- **文档即债务**：改动公开行为（类名 / 路径 / 语义 / 配置项）后，须全文搜索并同步引用它的文档与代码注释。
  幽灵类名与失效路径会长期滞留（历史：一个不存在的类名曾污染 6 个文档）。改淘汰/门控等语义时，
  除 ADR 外还需核查 `docs/memory-overview.md`、`docs/background-services.md` 等模块总览文档。
- **不留历史痕迹**：文档只描述**当前状态**。失效内容直接把正文改正，不在文末追加"现状更新 / 勘误表 /
  演进说明 / 取证时点"——同一处保留新旧两版说法，读者会读到互相矛盾的结论；变更历史交给 git。
  代码注释的同款规则见 [src/AGENTS.md](src/AGENTS.md) §2.2。

## Tool Permissions

- 禁止直接实例化 `HttpClient`（用 `IHttpClientFactory`）。
- App 层禁止跨 Infrastructure 边界直接文件 I/O（用 `IFileSystem` 抽象）。
- Terminal.Gui 依赖仅允许在 `OneCode.App/Tui/` 子目录内使用。
