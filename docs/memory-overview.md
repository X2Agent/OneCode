# 记忆模块（Memory Module）

> 本文档介绍 OneCode 记忆子系统的设计思路、模块总览与使用方式。架构决策与实现细节参见 [ADR 0004: 记忆模块架构设计](./adr/0004-memory-module-design.md)。

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

模块按"记忆生命周期 + 作用域"两个维度划分为 2 个子系统：

| 子系统 | 生命周期 | 作用域 | 存储形式 | 写入者 |
|--------|---------|--------|---------|--------|
| 结构化条目记忆 | 永久（含 TTL / LRU 淘汰） | 用户级 / 项目级 | `MEMORY.md` 结构化条目 | 用户手动 + AutoDream 自动 |
| 会话记忆 | 会话内 + 跨会话持久 | 单会话 | JSON Lines + 元数据 | LLM 自动提取 + 用户手动 |

> **架构演进**：旧版本的"键值记忆存储（KV Store）"子系统已移除，所有结构化记忆统一存入 `MEMORY.md`。详见 [ADR 0004](./adr/0004-memory-module-design.md#1-统一结构化条目存储)。Team 模式的多 Agent 共享记忆不设独立子系统——Team 成员经 `MemoryFileContextProvider`（`search_memories`）共享检索同一份 project 级 `MEMORY.md`，决策见 [ADR 0004 §8](./adr/0004-memory-module-design.md#8-决策不实现独立团队记忆子系统)。

---

## 2. 设计思路

### 2.1 核心原则

1. **存储与业务解耦**：`IMemoryEntryStore` 抽象物理存储（当前 `MEMORY.md`，未来可换 SQLite），业务层不感知文件路径
2. **按需注入**：记忆不无脑全量塞入 system prompt，而是"摘要索引常驻 + 相关条目按需检索"，控制 token 消耗
3. **作用域隔离**：用户级与项目级记忆物理隔离，`/cd` 切换项目时自动重路由
4. **自动积累**：通过 AutoDream 后台服务自动整合会话历史，用户无需手动维护
5. **容错降级**：LLM 提取失败时回退到启发式规则；文件损坏时跳过并记录日志

### 2.2 记忆的生命周期

```
用户对话
  │
  ├─ 即时约束 ──▶ 会话记忆（Session Memory）
  │  "记住用 DPAPI 加密"         注入当前会话 + 跨重启持久
  │
  └─ 持久事实 ──▶ 结构化条目记忆（MEMORY.md）
     /memory add 或 AutoDream    跨会话复用，摘要常驻 prompt
```

### 2.3 注入策略

记忆通过两条路径进入 Agent 上下文：

| 路径 | 机制 | 说明 |
|------|------|------|
| **System Prompt 注入** | `PromptConfigBuilder` 调用 `MemoryService.LoadMemoryPromptAsync` | 摘要索引常驻；构建 prompt 时若已知 query，附加 Top 相关条目 |
| **工具按需检索** | `MemoryFileContextProvider` 暴露 `search_memories` 工具 | LLM 主动检索完整记忆内容 |

会话记忆通过对应的 `AIContextProvider`（`SessionMemoryContextProvider`）在 Agent 调用前注入。

---

## 3. 模块总览

```text
                         ┌─────────────────────────────────────────────┐
                         │              System Prompt                  │
   PromptConfigBuilder ──▶  {{memory_section}} ← MemoryService         │
                         │  (条目摘要索引 + 相关条目)                   │
                         └─────────────────────────────────────────────┘
                                          ▲
                ┌─────────────────────────┴─────────────────────────┐
                │                                                   │
  MemoryFileContextProvider                             SessionMemoryContextProvider
  (search_memories 工具)                                 (Provide + Store 双向)
                │                                                   │
                ▼                                                   ▼
  ┌─────────────────────┐                          ┌──────────────────────┐
  │  MemoryService      │                          │ SessionMemoryService │
  │ (条目加载/检索)     │                          │ (会话事实 CRUD)      │
  └────────┬────────────┘                          └──────────┬───────────┘
           │                                                  │
           ▼                                                  ▼
  ┌─────────────────────┐                          ┌──────────────────────┐
  │ IMemoryEntryStore   │                          │ Conversation.Metadata│
  │ (MemoryEntryStore)  │                          │ + Metadata 键      │
  └────────┬────────────┘                          └──────────────────────┘
           │
           ▼
  ~/.onecode/memory/MEMORY.md
  {cwd}/.onecode/memory/MEMORY.md
           ▲
           │ 写入
  ┌────────┴────────┐
  │ AutoDreamService│  ← 后台自动整合会话历史
  └─────────────────┘
```

### 3.1 结构化条目记忆

**定位**：OneCode 的"主记忆库"。所有结构化记忆——用户手动添加（`/memory add`）与 AutoDream 自动提取——统一存为 `MemoryEntry` 条目。

**核心特性**：
- 双作用域：用户级（`~/.onecode/memory/MEMORY.md`）与项目级（`{cwd}/.onecode/memory/MEMORY.md`）
- Key 格式 `{category}:{short-id}`，如 `fact:build-command`、`manual:oauth-dpapi`
- 支持 TTL 过期与 LRU 容量淘汰（上限 200 条/作用域）
- 相关性检索基于 token 匹配评分，Top 6 注入 prompt

**谁会写入**：
- 用户通过 `/memory add` 手动添加（`source=manual`，永不过期）
- AutoDream 后台服务自动提取（`source=autodream`，可有 TTL）

### 3.2 会话记忆

**定位**：会话范围内的事实/偏好记忆。记录用户在对话中表达的"记住……"、"不要……"、"优先……"等约束。

**核心特性**：
- 存储于 `Conversation.Metadata`（`sessionMemories` 键），随会话文件 `~/.onecode/sessions/{sessionId}.jsonl` 持久化
- 双向 ContextProvider：注入 Top 5 条（按重要性排序）+ 响应后节流提取
- 四重节流防止过度提取：最小消息数、消息增量、轮次间隔、Token 增量
- LLM 摘要提取失败时回退到启发式规则（偏好信号词过滤）

### 3.3 AutoDream 自动整合

**定位**："睡眠式记忆整合"后台服务。当用户积累了足够多的新会话后，自动启动轻量 Agent 回顾会话，提取关键信息写入 `MEMORY.md`。

**核心特性**：
- 默认开启，双门控防止频繁触发（≥ 6 小时 + ≥ 3 个新会话）
- 增量变更 JSON 格式（`upsert` / `delete`），单次最多 50 条
- 输出经 `SanitizeKey` / `SanitizeValue` 清洗，防 `MEMORY.md` 结构注入
- 跨进程锁保护（`autodream.lock`），僵尸锁 2 小时可抢占

详见 [后台服务文档 - AutoDream 记忆整合](./background-services.md#5-autodream-记忆整合)。

---

## 4. 使用介绍

### 4.1 `/memory` 命令

管理可检索记忆（会话事实 + `MEMORY.md`）。**不要**用它写项目编码规范——规范请用 [`/remember`](./skills.md#remember) 更新 `AGENTS.md`。

| 子命令 | 语法 | 说明 |
|--------|------|------|
| `list` | `/memory` 或 `/memory list` | 列出会话记忆 + 持久化条目（含过期标记） |
| `add` | `/memory add [--user] <text>` | 添加 MEMORY.md 条目；默认项目级，`--user` 写入用户级 |
| `remove` | `/memory remove <n>`（别名 `delete`） | 删除第 n 条持久化条目 |
| `clear` | `/memory clear [--all]` | 清空项目级条目；`--all` 同时清空用户级 |
| `autodream trigger` | `/memory autodream trigger` | 手动触发 AutoDream 整合 |
| `autodream status` | `/memory autodream status` | 查看 AutoDream 状态 |

`/memory list` 输出示例：

```text
Session memories:
  1. [manual] 优先使用 DPAPI 加密 OAuth 凭据
  2. [auto] 项目使用 dotnet build，耗时约 45 秒

Persistent memory entries:
  1. [global/manual] manual:oauth-dpapi
       本项目所有 OAuth 凭据必须使用 DPAPI 加密存储。
  2. [project/autodream] fact:build-command [EXPIRED]
       Build with `dotnet build src/OneCode.sln`. Typical duration ~45s.
```

### 4.2 `search_memories` 工具

Agent 可通过 `search_memories` 工具按需检索完整记忆内容。传入自然语言 query，返回评分 Top 6 的匹配条目（含完整 Value、作用域、评分）。

此工具作为 system prompt 注入的补充——prompt 中仅含摘要索引，完整内容需主动检索。

### 4.3 AutoDream 自动积累

AutoDream 默认开启，无需配置。用户正常使用积累会话后，后台自动：

1. 回顾自上次整合以来的新会话
2. 提取事实/约定/教训/纠正等可复用知识
3. 以增量变更写入 `MEMORY.md`（用户级或项目级）
4. 清理过期条目 + LRU 淘汰

用户可通过 `/memory autodream trigger` 手动触发，`/memory autodream status` 查看状态。

### 4.4 手动维护 MEMORY.md

`MEMORY.md` 是结构化条目文件，用户也可直接编辑。文件格式见 [ADR 0004 §2](./adr/0004-memory-module-design.md#2-memorymd-文件格式与数据模型)。

**注意事项**：
- 手动编辑时保持 `## {key}` 作为条目分隔符，`- key: value` 作为元数据行
- 手动添加的条目建议用 `manual:` 前缀的 Key，与 AutoDream 提取的条目区分
- Agent 不通过工具直接写 `MEMORY.md`——所有程序化写入经 `IMemoryEntryStore` 或 AutoDream 清洗管线

---

## 5. 存储目录速查

| 子系统 | 作用域 | 物理路径 |
|--------|--------|---------|
| 结构化条目记忆 | 用户级（全局） | `~/.onecode/memory/MEMORY.md` |
| 结构化条目记忆 | 项目级 | `{cwd}/.onecode/memory/MEMORY.md` |
| 会话记忆 | 会话级 | `~/.onecode/sessions/{sessionId}.jsonl`（`sessionMemories` 元数据键） |
| AutoDream 状态 | 项目级 | `{cwd}/.onecode/memory/`（lock、时间戳文件） |

> **已移除**：旧版本的 `~/.onecode/memory-store/` 与 `{cwd}/.onecode/memory-store/`（KV Store）目录不再使用。

---

## 6. 相关文档

- [ADR 0004: 记忆模块架构设计](./adr/0004-memory-module-design.md) — 架构决策、数据模型、实现细节
- [后台服务文档 - AutoDream 记忆整合](./background-services.md#5-autodream-记忆整合) — AutoDream 后台服务机制
- [命令文档](./commands.md) — `/memory` 命令完整说明
- [内置技能 /remember](./skills.md#remember) — 写入 `AGENTS.md` 项目规范（与 Memory 子系统分离）

---

## 附录：与 AGENTS.md /remember 的边界

`/memory` 与 `/remember` 都像「留下以后还要用的信息」，但落点与注入路径不同：

| | `/memory add` | `/remember` |
|--|---------------|-------------|
| 落点 | `MEMORY.md`（`~/.onecode/memory/` 或 `{cwd}/.onecode/memory/`） | 仓库根 `AGENTS.md` |
| 形态 | 结构化 `MemoryEntry`（可检索、可淘汰） | 自由 Markdown 规范条文 |
| 写入 | 命令直接落盘，无 LLM | Skill → LLM 读写 `AGENTS.md` |
| 注入 | System Prompt 的 Memory 区 + `search_memories` | Project Context（与规则文件同类） |
| 适合 | 事实、偏好、决策、可回忆知识 | 编码约定、构建流程、must/never 规则 |
| 自动整合 | AutoDream **只写** MEMORY.md | AutoDream **故意不写** AGENTS.md |

**选用口诀**：

- 「下次对话可能要**回忆**」→ `/memory add`
- 「这个仓库里 Agent **必须遵守**」→ `/remember`（或手改 / `/init` 维护 AGENTS.md）
