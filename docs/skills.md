# OneCode 内置技能（Bundled Skills）

OneCode 的 Skill 系统是一种轻量级的"斜杠命令工作流"：每个 skill 是一段预定义的 prompt 模板，用户输入 `/skill-name <参数>` 后，模板被渲染并交给 LLM 执行。

技能与 `/commit`、`/review` 等 Prompt 类命令的区别在于：技能是**数据**（markdown 文本），不是**代码**（Command 子类）。新增技能只需写一个 `.md` 文件，无需修改 C# 代码。

---

## 技能来源与加载

技能有**三个来源**，按优先级合并后通过 `SkillCommandSource`（`IDynamicCommandSource`）注册为动态斜杠命令：

| 来源 | 路径 | 作用域 |
|---|---|---|
| **内置技能** | `BundledSkills.cs` 硬编码 | 所有用户共享，随版本发布 |
| **用户技能** | `~/{候选目录}/skills/` | 当前用户全局，跨项目共享 |
| **项目技能** | `<项目>/{候选目录}/skills/` | 当前项目，团队共享 |

> 读取/发现按优先级枚举三个候选配置目录：`.onecode` → `.agent` → `.claude`（`ConfigDirPaths.EnumerateExisting`，见 `Infrastructure/Config/Constants.cs` 的 `ConfigDirCandidates`）；写入/安装始终使用主目录 `.onecode`。

同名技能**后加载者覆盖先加载者**：`BundledSkills`（硬编码）→ 打包技能目录 → 用户技能 → 项目技能（`SkillCatalog.LoadUserInvocableSkills` 按字典后写覆盖）。因此实际生效优先级为**项目 > 用户 > 内置**——项目级同名技能最后写入，会覆盖用户级/内置的同名技能。

---

## 参数占位符

技能 prompt 模板支持两类占位符，均在调用斜杠命令时由 `SkillCatalog.Render` 运行时替换：

| 占位符 | 替换规则 | 示例 |
|---|---|---|
| `$ARGUMENTS` | 替换为全部参数拼接（`/skill-name foo bar` → `foo bar`） | `/remember 新工具必须 sealed + AddTool 注册` |
| `{xxx}` | **命名占位符**：从 prompt 中推断参数名（`ArgumentNames`），按位置映射 `args[i]`；技能只有一个（或零个）命名占位符时，该占位符吸收全部拼接参数；未匹配到值的占位符保持原样留给 LLM 理解 | `/batch 修复登录页样式` → `{instruction}` 被替换为 `修复登录页样式` |

> **注意**：多占位符技能按位置映射（如 `/loop` 的 `{task}` ← 第 1 个参数、`{expected}` ← 第 2 个参数）；参数不足时对应占位符替换为空字符串；占位符名不在参数名列表中时保持原样。

---

## 内置技能清单（9 个）

以下技能随 OneCode 发布，定义在 `src/OneCode.Core/Skills/BundledSkills.cs`。

### /batch

用 git worktree 隔离的大规模并行编排。

**用法**：

```
/batch <instruction>
```

**作用**：引导 LLM 将一个大规模可并行化的改动分解为 5-30 个独立单元，每个单元在独立的 git worktree 中执行，最后合并回主分支。

**四阶段流程**：
1. **Research and Plan** — 理解范围、分解为独立单元、确定 e2e 测试方案
2. **Create Worktrees** — `git worktree add ../worktree-<N> -b batch-<N>`
3. **Dispatch Workers** — 每个 worktree 派发一个 worker agent
4. **Collect and Merge** — 合并所有 worktree 回 main

**占位符**：`{instruction}`（运行时替换：吸收全部参数）

> 与 `ParallelAgentsTool`（DAG 并行调度，同一工作目录）的区别：`/batch` 用 git worktree 做文件级隔离（每个 worker 独立工作树）。

---

### /debug

系统性调试方法论。

**用法**：

```
/debug <issue-description>
```

**作用**：引导 LLM 按六步调试法工作：复现 → 隔离 → 假设 → 测试假设 → 修复 → 验证修复。

**占位符**：`{issue}`（运行时替换：吸收全部参数）

---

### /loop

迭代执行直到结果匹配目标。

**用法**：

```
/loop <task>
```

**作用**：引导 LLM 反复执行任务并与期望输出对比，不正确则分析修正后重试，直到正确或达到最大迭代次数。

**占位符**：`{task}` + `{expected}`（运行时按位置替换：第 1/2 个参数）

> 适用于需要"生成 → 验证 → 修正"循环的场景，如生成匹配特定格式的输出。

---

### /stuck

卡住时的恢复策略。

**用法**：

```
/stuck <current-state>
```

**作用**：当 LLM 陷入重复操作无进展时，引导其暂停 → 评估已尝试的方法 → 换一种根本性不同的策略 → 必要时用 `AskUserQuestion` 向用户求助。

**占位符**：`{context}`（运行时替换：吸收全部参数）

> 可在 LLM 表现出"转圈"行为时手动触发，打断无效循环。

---

### /verify

变更完整性验证检查清单。

**用法**：

```
/verify [context]
```

**作用**：引导 LLM 按五项检查清单验证变更：Build → Test → Lint → 手动验证 → 边界情况。

**占位符**：`{context}`（运行时替换：吸收全部参数）

> 适用于代码变更完成后的收尾验证。

---

### /simplify

变更后代码简化审查。

**用法**：

```
/simplify [changes-summary]
```

**作用**：引导 LLM 在实现变更后审查并简化：通读所有改动 → 去除重复 → 简化逻辑 → 检查约定 → 删除死代码（未使用的 import、变量、函数）。

**占位符**：`{changes}`（运行时替换：吸收全部参数）

> 适用于重构或新功能实现完成后的"收尾打扫"。

---

### /skillify

把当前对话沉淀为可复用技能文件。

**用法**：

```
/skillify <skill-name>
```

**作用**：分析对话历史中的重复模式或工作流，在 `.onecode/skills/<skill-name>.md` 生成一个新技能文件，包含 YAML frontmatter（`name`、`description`、`argument-hint`）+ 可执行指令体 + `$ARGUMENTS` 占位符。

**占位符**：`$ARGUMENTS`（被运行时替换）

> 生成后该技能立即可用，通过 `/skills` 可验证，通过 `/<skill-name>` 可调用。

---

### /remember

把**项目规范**写入 `AGENTS.md`（不是 MEMORY.md）。

**用法**：

```
/remember <project-rule>
```

**作用**：由 LLM 读取并更新项目根目录的 `AGENTS.md`，追加为持久编码/流程规则（注入为 Project Context）。适合 must/never/prefer 类约定；写入时会去重、保持条目简洁。

**占位符**：`$ARGUMENTS`（被运行时替换）

> **与 `/memory` 的边界**（二者不要混用）：
>
> | | `/remember` | `/memory add` |
> |--|-------------|----------------|
> | 落点 | `AGENTS.md` | `MEMORY.md` |
> | 用途 | 项目规范、团队共享规则 | 可检索事实/偏好 |
> | 注入 | Project Context | Memory 索引 + `search_memories` |
>
> 示例：`/remember 新工具必须 sealed 并用 AddTool 注册` → AGENTS.md；  
> `/memory add 这个仓库 OAuth 用 DPAPI` → MEMORY.md。

---

### /verify-content

内容质量与准确性审查。

**用法**：

```
/verify-content <content>
```

**作用**：审查指定内容的准确性、完整性、清晰度、一致性、代码正确性（如有）、安全性（如有）。输出按严重级别（Critical / Warning / Suggestion）列出每个问题，并在有重大问题时提供修正版本，结尾给出总体评估（Pass / Pass with minor issues / Fail）。

**占位符**：`$ARGUMENTS`（被运行时替换）

> 适用于审查 LLM 生成的文档、方案、报告等非代码内容。

---

## 自定义技能

### 文件格式

自定义技能是一个 markdown 文件，放在 `~/.onecode/skills/`（用户级）或 `<项目>/.onecode/skills/`（项目级）下（`.agent`/`.claude` 候选目录下的技能也会被发现，但安装/生成始终写入 `.onecode`）。支持两种目录结构：

```
# 结构一：单文件
~/.onecode/skills/
  └── my-skill.md          # 文件名即技能名

# 结构二：目录
~/.onecode/skills/
  └── my-skill/
      └── SKILL.md          # 目录名即技能名
```

### Markdown 内容

技能文件的第一行 `#` 标题会被提取为技能描述（显示在 `/skills` 列表中）。文件体即为 prompt 模板，支持 `$ARGUMENTS` 占位符：

```markdown
# My Custom Skill

这是一个自定义技能的 prompt。

## 用户输入

$ARGUMENTS

## 指令

1. 做某事
2. 做另一件事
```

### 生成技能

用 `/skillify` 可以从当前对话自动生成一个技能文件，无需手动编写。

### 安装技能

用 `/install` 可以从本地目录或 git 仓库安装技能：

```
/install ./my-skill              # 本地目录 → 项目级
/install ./my-skill -g           # 本地目录 → 用户级
/install https://github.com/user/skill-repo.git
```

---

## 技能发现与调用

| 方式 | 说明 |
|---|---|
| `/skills` | 列出所有可用技能（内置 + 用户 + 项目），显示名称和一行描述 |
| `/skills list` | 同上 |
| `/skills show <name>` 或 `/skills <name>` | 查看指定技能的详情（预览，不执行） |
| `/<skill-name> <args>` | **执行**技能的唯一入口（斜杠补全支持 Tab） |
| `SkillTool`（LLM 工具） | LLM 在对话中自主调用技能 |

> `/skills run` 已移除，避免与 `/<skill-name>` 双路径重复。

> 技能通过 `SkillCommandSource` 动态加载，不需要在 `CommandServiceExtensions.cs` 中注册。新增或删除内置技能时，修改 `BundledSkills.cs` 的 `LoadBundledSkills()` 方法；新增自定义技能时，放入对应目录即可，无需改动代码。

---

## 技术细节

### 注册与消费链路

```
BundledSkills.All (静态字典)
    ↓
SkillCommandSource.LoadCommandsAsync()   →  生成 SkillProxyCommand（动态斜杠命令）
    ↓
SkillTool                                 →  LLM 工具调用入口
    ↓
AgentSkillsProviderFactory               →  注入到 Agent context（LLM 可见技能列表）
    ↓
SkillsCommand                             →  /skills 命令，列出所有技能
```

### Prompt 渲染

`SkillProxyCommand.ExecuteAsync` 调用时重新解析技能（使 frontmatter/正文编辑在去抖刷新前即生效），再经 `SkillCatalog.Render(skill, args)` 渲染：

```csharp
// SkillCatalog.Render 核心逻辑：
var joined = string.Join(" ", args);

// 1. 命名占位符按位置映射：ArgumentNames[i] ← args[i]（不足补空串）
// 2. 替换 $ARGUMENTS ← joined
// 3. {xxx} 正则替换：
//    - 名字在参数名表中 → 对应参数值
//    - 技能只有 0/1 个命名占位符 → 吸收全部 joined
//    - 其他 → 保持原样（留给 LLM 理解）
return CommandResult.Prompt(resolved);
```

- `$ARGUMENTS` 与 `{xxx}` 均被运行时替换（规则见[参数占位符](#参数占位符)）
- 返回 `CommandResult.Prompt`，由 query 流交给 LLM 执行

### 真相源

内置技能的注册真相源：`src/OneCode.Core/Skills/BundledSkills.cs` 的 `LoadBundledSkills()` 方法。本文档与此保持同步，新增或删除内置技能时请同时更新。
