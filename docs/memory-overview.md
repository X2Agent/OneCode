# 记忆模块（Memory Module）

> 本文档介绍 OneCode 记忆子系统的设计思路、模块总览与使用方式。架构决策与实现细节参见 [记忆模块架构设计](./adr/0004-memory-module-design.md)。

---

## 目录

- [1. 概述](#1-概述)
- [2. 设计思路](#2-设计思路)
- [3. 模块总览](#3-模块总览)
- [4. 使用介绍](#4-使用介绍)
- [5. 存储目录速查](#5-存储目录速查)
- [6. 相关文档](#6-相关文档)
- [附录：与 AGENTS.md /remember 的边界](#附录与-agentsmd-remember-的边界)

---

## 1. 概述

OneCode 的记忆模块旨在让 Agent **跨会话、跨项目地积累与复用知识**，而非每次交互从零开始。

| 子系统 | 生命周期 | 作用域 | 存储形式 | 写入者 |
|--------|---------|--------|---------|--------|
| 结构化条目记忆 | 永久（manual）或 TTL 自动过期（autodream 默认 30 天） | 用户级 / 项目级 | `MEMORY.md` 结构化条目（单作用域上限 50 条） | 用户手动 + AutoDream 自动 |

> **会话工作记忆**不属本模块：它由原生 MAF `FileMemoryProvider` 承载，落在 `{工作目录}/.onecode/agent-file-memory/`（根按会话工作目录解析、随项目隔离，见 `FileMemoryStorePaths`），是 AutoDream 整合的首选数据源，归属见 [MAF 集成边界与禁止清单](./adr/0007-maf-integration-boundaries.md)。
>
> 子代理派工（Agent/ParallelAgents）不设独立记忆子系统——主会话、子代理与 AutoDream 统一经 `MemoryContextProvider` 注入同一份项目级与用户级 `MEMORY.md`。

---

## 2. 设计思路

### 2.1 核心原则

1. **存储保持轻量**：`IMemoryEntryStore` 封装 `MEMORY.md` 结构化分段读写（`OneCode.Infrastructure/Memory/MemoryEntryStore`），保留 `## {key}` 分段与 `source / updated_at / expires_at` 元数据，不引入 SQLite。
2. **单一读取入口**：通过 `MemoryContextProvider`（继承 `ReadOnlyAIContextProviderBase`）经 `AIContext.Instructions` 注入有效记忆的**完整正文**（剥离内部管理属性，受 `8,000` 字符总预算约束），无需二次调用检索工具。
3. **三道防线防记忆爆炸**：
   - 写入端：AutoDream 整合代理挂载 `MemoryContextProvider` 读取既有记忆，同名 Key 原地 `upsert`、使用 `supersedes` 合并替换碎片化旧 Key、使用 `delete` 清理失效条目；
   - 存储端：自动记忆默认 30 天（`720h`）TTL 过期，单作用域硬上限 `MaxEntries = 50`（按 `source == "manual"` 豁免手动条目后，按 `UpdatedAt` 淘汰最旧自动条目）；
   - 读取端：`MaxMemoryContextChars = 8_000` 字符预算兜底（先输出手动记忆分区、再输出自动记忆分区，各分区内按 `Project` 优先于 `User` → `UpdatedAt` 降序）。
4. **任务完成驱动**：仅在 `BUILD` 模式任务正常完成且未回滚时由 `QueryStreamEngine.FinalizeAsync` 触发后台增量整合，无定时空转轮询。

---

## 3. 模块总览

```text
  主会话 / 子代理 / AutoDream
           │
           ▼
  ┌─────────────────────────┐
  │  MemoryContextProvider  │  ← 唯一读取入口（AIContext.Instructions 注入完整正文，<= 8000 字符）
  └────────────┬────────────┘
               │
               ▼
  ┌─────────────────────────┐
  │      MemoryService      │  ← 剥离内部元数据，按 manual > Project > User > UpdatedAt 组装
  └────────────┬────────────┘
               │
               ▼
  ┌─────────────────────────┐
  │    IMemoryEntryStore    │  ← 单作用域上限 50 条，TTL 过期清理，manual 保护闸
  │   (MemoryEntryStore)    │
  └────────────┬────────────┘
               │
               ▼
  ~/.onecode/memory/MEMORY.md
  {cwd}/.onecode/memory/MEMORY.md
               ▲
               │ 增量变更 JSON (upsert / supersedes / delete)
  ┌────────────┴────────────┐
  │    AutoDreamService     │  ← BUILD 任务完成触发 + C# 零 Token 增量快检
  └─────────────────────────┘
```

### 3.1 结构化条目记忆

**核心特性**：
- 双作用域：用户级（`~/.onecode/memory/MEMORY.md`）与项目级（`{cwd}/.onecode/memory/MEMORY.md`）
- Key 格式：`{category}:{short-id}`（如 `fact:build-command`、`/memory add` 生成的语义化 `manual:use-dotnet-build-src-10142130`）
- 容量与 TTL：单作用域最多 50 条；AutoDream 条目默认 TTL 720 小时（30 天），`manual` 条目永不过期且豁免淘汰
- **Project 条目优先于 User 条目**呈现（无论在手动记忆还是自动记忆分区），每条均带 `(project)` / `(global)` 作用域标注

### 3.2 AutoDream 任务级整合

- **触发时机**：仅当 `BUILD` 模式（含普通交互式 `BUILD` 与 `PLAN` 批准后转入的受控 `BUILD`）在 `QueryStreamEngine.FinalizeAsync` 正常完成且未回滚时触发（`PLAN` 与 `GOAL` 模式不触发）；另支持 `/memory autodream trigger` 手动强制触发。
- **零 Token 候选快检**：先在 C# 层检查当前内存会话是否存在有效对话轮次或存在 working-memory 笔记，无候选直接跳过。
- **数据源与单轮无工具整理**：基于 `{工作目录}/.onecode/agent-file-memory/` 工作记忆笔记、当前内存会话（`ISessionManager.ForegroundConversation`）的 `User` / `Assistant` 对话轮次以及 `MemoryContextProvider` 注入的当前 `MEMORY.md`，执行单次无工具 LLM 整理。

详见 [后台服务文档 - AutoDream 记忆整合](./background-services.md#5-autodream-记忆整合)。

---

## 4. 使用介绍

### 4.1 `/memory` 命令

管理持久化记忆（`MEMORY.md` 条目）。**不要**用它写项目编码规范——规范请用 [`/remember`](./skills.md#remember) 更新 `AGENTS.md`。

| 子命令 | 语法 | 说明 |
|--------|------|------|
| `list` | `/memory` 或 `/memory list` | 列出持久化条目（项目级优先于全局，含过期标记） |
| `add` | `/memory add [--user] <text>` | 添加 MEMORY.md 条目（自动提取关键词生成 `manual:{slug}-{MMddHHmm}`）；默认项目级，`--user` 写入用户级 |
| `remove` | `/memory remove <n>`（别名 `delete`） | 删除第 n 条持久化条目 |
| `clear` | `/memory clear [--all]` | 清空项目级条目；`--all` 同时清空用户级 |
| `autodream trigger` | `/memory autodream trigger` | 手动强制触发 AutoDream 整合 |
| `autodream status` | `/memory autodream status` | 查看 AutoDream 状态 |

### 4.2 手动维护 MEMORY.md

`MEMORY.md` 是结构化条目文件，用户也可直接编辑。文件格式见 [记忆模块架构设计](./adr/0004-memory-module-design.md)。

**注意事项**：
- 手动编辑时保持 `## {key}` 作为条目分隔符；解析器仅消费已知元数据键（`source`、`category`、`created_at`、`updated_at`、`expires_at`），正文中的普通 `- key: value` 列表会被完整保留
- 手动添加的条目使用 `manual:` 前缀的 Key 且写入 `source: manual`：AutoDream 写入闸（`AutoDreamMemoryApplier`）同时拦截 `manual:` Key 前缀与 `source == "manual"` 防止覆盖或删除，存储淘汰闸（`PruneAsync`）按 `source == "manual"` 豁免手动条目；若直接手改 `MEMORY.md` 新增永久条目，请同时保留 `manual:` Key 前缀与 `- source: manual` 元数据
- Agent 不通过工具直接写 `MEMORY.md`——所有程序化写入经 `IMemoryEntryStore` 或 AutoDream 清洗管线

---

## 5. 存储目录速查

| 子系统 | 作用域 | 物理路径 |
|--------|--------|---------|
| 结构化条目记忆 | 用户级（全局） | `~/.onecode/memory/MEMORY.md` |
| 结构化条目记忆 | 项目级 | `{cwd}/.onecode/memory/MEMORY.md` |
| AutoDream 状态 | 项目级 | `{cwd}/.onecode/memory/`（`autodream.lock`） |
| 会话工作记忆（AutoDream 首选数据源） | 项目级 | `{工作目录}/.onecode/agent-file-memory/`（随会话工作目录解析，见 `FileMemoryStorePaths`） |

---

## 6. 相关文档

- [记忆模块架构设计](./adr/0004-memory-module-design.md) — 架构决策、数据模型、实现细节
- [后台服务文档 - AutoDream 记忆整合](./background-services.md#5-autodream-记忆整合) — AutoDream 后台服务机制
- [命令文档](./commands.md) — `/memory` 命令完整说明
- [内置技能 /remember](./skills.md#remember) — 写入 `AGENTS.md` 项目规范（与 Memory 子系统分离）

---

## 附录：与 AGENTS.md /remember 的边界

| | `/memory add` | `/remember` |
|--|---------------|-------------|
| 落点 | `MEMORY.md`（`~/.onecode/memory/` 或 `{cwd}/.onecode/memory/`） | 仓库根 `AGENTS.md` |
| 形态 | 结构化 `MemoryEntry`（带作用域与可选 TTL） | 自由 Markdown 规范条文 |
| 写入 | 命令直接落盘，无 LLM | Skill → LLM 读写 `AGENTS.md` |
| 注入 | `MemoryContextProvider` 经 `AIContext.Instructions` 全文注入 | Project Context（与规则文件同类） |
| 适合 | 事实、偏好、排错教训、用户纠正 | 编码约定、构建流程、must/never 规则 |
| 自动整合 | AutoDream **只写** MEMORY.md | AutoDream **故意不写** AGENTS.md |
