# 标准技能目录与 `.onecodeignore` 开发设计

## 1. 文档目的

本文规划两个相互独立、但都服务于“项目上下文可控性”的功能：

1. 兼容 Agent Skills 生态中的标准技能目录，例如 `.agents/skills`。
2. 增加项目级 `.onecodeignore`，让用户能够声明 OneCode 不应主动发现、搜索或注入上下文的文件。

本文是实现规划，不改变当前权限模型、Prompt 分层、Session/Workflow 持久化语义，也不引入可执行配置文件。

## 2. 当前实现基线

### 2.1 技能发现

当前技能入口集中在 `SkillCatalog`：

- 内置技能来自 `BundledSkills.All`。
- 用户级和项目级技能通过 `ConfigDirPaths.EnumerateExisting(..., "skills")` 发现。
- 当前配置目录候选为 `.onecode`、`.claude`（历史 `.agent` 候选目录已弃用并移除）。
- 支持两种文件布局：`skills/name.md` 和 `skills/name/SKILL.md`。
- 技能通过 frontmatter 解析，只有 `user-invocable: true` 的技能注册为动态斜杠命令。
- 后加载的同名技能覆盖先加载的技能，当前有效优先级为项目级 > 用户级 > 内置技能。

主要入口：

- `src/OneCode.App/Services/Skills/SkillCatalog.cs`
- `src/OneCode.Infrastructure/Config/ConfigDirPaths.cs`
- `src/OneCode.Infrastructure/Config/Constants.cs`
- `src/OneCode.Tests/SkillCatalogTests.cs`

### 2.2 文件过滤

当前 `FileIgnore` 提供内置目录和文件规则，主要由 `GlobTool` 使用：

- 目录规则包括 `bin`、`obj`、`.git`、`node_modules`、缓存目录等。
- 文件规则包括日志、临时文件、覆盖率输出等。
- `IsIgnored` 已经具备额外 glob 规则和 whitelist 参数。
- `GrepTool` 不直接调用 `FileIgnore`，而是通过 `ITextSearchService` 使用 ripgrep 或 C# fallback。

主要入口：

- `src/OneCode.App/Tools/FileIgnore.cs`
- `src/OneCode.App/Tools/GlobTool.cs`
- `src/OneCode.App/Tools/GrepTool.cs`
- `src/OneCode.App/Services/Search/ITextSearchService.cs`（实际路径以当前实现为准）

## 3. 目标与非目标

### 3.1 目标

- 让已有 Agent Skills 生态中的项目技能无需复制即可被 OneCode 发现。
- 不破坏当前 `.onecode`、`.claude` 技能布局和覆盖规则。
- 让 `.onecodeignore` 至少统一影响 Glob、LS/tree 类扫描、Grep 和上下文文件发现。
- 忽略规则应当是数据，不执行 shell、脚本或命令替换。
- 规则解析失败时采用可诊断的 fail-open 策略：保留内置忽略规则，但不静默屏蔽用户指定的全部项目文件。
- 所有路径都必须以工作目录为边界，继续遵守现有 `IFileSystem`、`IWorkingDirectoryAccessor` 和 `PathsHelper` 约束。

### 3.2 非目标

- 不实现 Crush 的 `crushrc`，不允许在配置加载阶段执行 Bash 或 `$(command)`。
- 不修改权限模式、YOLO 规则、信任目录和 `allowedDirectories` 的语义。
- 不把 `.onecodeignore` 当作访问控制。忽略文件只控制主动发现和上下文收集，不能绕过工具自身的路径安全校验。
- 不修改 Git 的行为，也不替代 `.gitignore`。
- 第一阶段不支持远程技能仓库自动下载、版本锁定或自动更新。
- 第一阶段不要求所有第三方 MCP 工具都理解 `.onecodeignore`；只能约束 OneCode 自己控制的文件访问入口。

## 4. 功能一：标准技能目录兼容

### 4.1 支持的目录

技能发现分为“用户级”和“项目级”。写入、安装和 `/skillify` 仍然只使用 `.onecode/skills`；其他目录只读发现。

#### 项目级目录

按低到高的覆盖顺序处理：

1. `<project>/.agents/skills`
2. `<project>/.cursor/skills`
3. `<project>/.onecode/skills`
4. `<project>/.claude/skills`

现有 `.onecode` 与 `.claude` 的相互优先级保持不变；新增生态目录作为兼容来源，不能覆盖现有 OneCode 本地技能。若未来需要改变优先级，应单独发布设计变更并提供冲突诊断。

#### 用户级目录

第一阶段支持以下路径：

1. `%USERPROFILE%/.agents/skills`
2. `%USERPROFILE%/.cursor/skills`
3. `%USERPROFILE%/.onecode/skills`
4. `%USERPROFILE%/.claude/skills`

跨平台实现使用 `PathsHelper.UserHome`，不在业务代码中硬编码 Windows 环境变量。Linux/macOS 使用对应的 home 目录；如后续需要兼容 XDG 目录，应增加独立的路径解析策略，不在本次功能中混入。

### 4.2 发现规则

沿用现有技能文件格式和解析器：

- `<dir>/<name>.md`
- `<dir>/<name>/SKILL.md`

目录遍历只读取当前技能根目录的直接子目录和直接 `.md` 文件，不递归扫描任意深度，避免把项目文档误识别为技能。

名称解析顺序：

1. frontmatter 中的 `name`；
2. `SKILL.md` 所在目录名；
3. 单文件 `.md` 的文件名。

解析失败、文件不可读、名称为空或 frontmatter 不满足现有约束时：

- 跳过该文件；
- 不影响其他技能加载；
- 在 debug 日志中记录完整路径和失败原因；
- `/skills` 诊断输出可选显示跳过计数，但不得输出技能正文或敏感 frontmatter 值。
- 同一技能根目录内同时存在 `foo.md` 与 `foo/SKILL.md` 时，目录布局（`foo/SKILL.md`）优先于单文件 `foo.md`，并记录冲突诊断；
- 同级条目按 `StringComparer.OrdinalIgnoreCase` 排序后再处理，不依赖操作系统枚举顺序，跨平台结果可复现；
- 上述排序与裁决规则同时适用于快照缓存路径（见 §4.6），缓存与非缓存加载结果必须一致。

### 4.3 覆盖和冲突

技能名比较继续使用 `StringComparer.OrdinalIgnoreCase`。

有效覆盖优先级：

```text
内置技能
  < 用户级生态目录
  < 用户级 OneCode 目录
  < 项目级生态目录
  < 项目级 OneCode/兼容目录
```

同一优先级内按固定目录顺序加载，不依赖操作系统的枚举顺序。建议把目录候选定义为不可变常量，并由 `SkillCatalog` 统一消费，避免 `SkillsCommand`、`SkillChangeWatcher` 和 `SkillCatalog` 各自维护一份列表。

冲突诊断建议记录：

- 生效技能名；
- 生效文件路径；
- 被覆盖文件路径；
- 来源作用域：builtin/user/project；
- 是否来自兼容目录。

默认不把冲突当成错误，因为现有行为就是后加载覆盖。

### 4.4 文件监听

`SkillChangeWatcher` 必须使用与 `SkillCatalog` 相同的目录候选解析函数：

- 新增标准目录后，创建目录或文件应触发刷新；
- 删除或修改技能后，应重新构建动态命令；
- 不应为每个候选目录复制一套监听逻辑；
- 监听失败只影响该目录，不应终止主会话。

建议抽象一个只负责“候选目录顺序和作用域”的解析结果，例如：

```text
SkillDirectoryCandidate
  Path
  Scope: User | Project | Bundled
  Source: OneCode | AgentSkills | Crush | Cursor | Claude
  Priority
```

该类型应留在 App/Infrastructure 现有技能路径边界内，不必为了目录枚举新增 Core 接口。

### 4.5 安全边界

- 标准目录只读发现；安装、生成和覆盖写入仍落到 `.onecode/skills`。
- 不从技能 frontmatter 执行命令，不展开 shell 表达式。
- 技能正文仍然是交给模型的指令，不代表权限授权；工具调用继续经过 OneCode 权限系统。
- 项目技能属于项目输入，首次使用外部项目时应继续遵守现有信任提示策略。

### 4.6 技能解析缓存

`SkillCatalog.Find` 与 `LoadUserInvocableSkills` 每次调用都会全量重扫描所有技能目录并重新解析 frontmatter。候选目录从 3 个扩到 6 个后，每次 `/<skill-name>` 执行的扫描成本翻倍，因此技能发现需要与 `.onecodeignore` 快照（§5.6）同级但独立的缓存设计。

#### 快照结构

技能快照按工作目录维护，包含：

- 每个技能目录的枚举状态（目录 mtime 或内容版本）；
- 解析成功/跳过的技能文档列表（含来源作用域与是否来自兼容目录）；
- 覆盖裁决后的最终技能表与冲突诊断记录。

#### 失效与刷新

- 快照失效由 `SkillChangeWatcher` 驱动：debounce 通道触发刷新时同时使技能快照失效并重建；
- 命中有效快照时直接返回，不重新枚举目录、不重新解析全部文件；
- 工作目录切换时必须丢弃旧快照，不复用其他工作区的解析结果；
- 初始实现不持久化技能缓存，进程重启后重建。

#### 编辑后立即生效语义

现有 `SkillProxyCommand.ExecuteAsync` 每次调用都重新解析目标技能，使正文编辑在 watcher 去抖刷新前即生效。引入快照后保留该语义：对命中技能做一次轻量 mtime/内容版本校验，未变化直接返回快照结果，已变化则单独重读该技能并更新快照条目；新增/删除/覆盖裁决等结构性变化仍由 watcher 驱动全量失效。

#### 一致性约束

- 快照重建必须复用无缓存路径的同一解析函数，避免缓存路径与直读路径行为分叉；
- 解析顺序按 §4.2 确定性规则生成，保证缓存与非缓存结果可复现。

## 5. 功能二：`.onecodeignore`

### 5.1 文件位置和作用域

第一阶段只支持工作目录根部的：

```text
<working-directory>/.onecodeignore
```

不支持用户级全局 ignore，也不支持配置文件中嵌入 ignore 内容。这样可以避免不同项目之间规则串扰，也不需要增加新的配置优先级。

后续如确有需求，可增加父目录继承或子目录规则，但必须先明确路径边界、规则合并和性能模型。

### 5.2 语法

采用 `.gitignore` 风格的 glob 语法，但只实现 OneCode 需要的子集：

- 空行：忽略；
- `#` 开头：注释；
- 普通模式：匹配文件或目录；
- `*`：匹配单个路径片段中的任意字符；
- `**`：跨越多个目录层级；
- `?`：匹配单个字符；
- 末尾 `/`：表示目录；
- 开头 `!`：取消忽略；规则按行出现顺序应用，后出现的规则覆盖此前匹配（`.gitignore` 的 last-match-wins 语义）；
- 开头 `/`：相对于工作目录根部匹配；
- 反斜杠用于转义特殊字符。

示例：

```gitignore
# Secrets and local state
.env
.env.*
!.env.example

# Generated output
artifacts/
**/generated/

# Large local data
*.sqlite
data/**
```

规则统一转换为 `/` 分隔符后匹配相对于工作目录的路径。Windows 大小写行为与现有 `FileIgnore` 一致，使用不区分大小写匹配。

### 5.3 与内置规则的关系

`.onecodeignore` 是用户补充规则，不覆盖内置安全/噪音规则：

- 内置规则始终生效，例如 `.git`、`bin`、`obj`、`node_modules`；
- 用户规则可以增加忽略项；
- 用户规则的 `!` 只能恢复用户规则忽略的路径，不能恢复内置规则忽略的路径；
- 第一阶段不提供配置项来关闭内置规则。

推荐将匹配结果表达为两个层次：

```text
BuiltInIgnored     // 永远忽略
UserIgnored        // .onecodeignore 命中
ExplicitlyIncluded // 用户规则取消用户忽略
```

这样可以避免 whitelist 误恢复 `.git` 或其他内置排除目录。

### 5.4 统一消费边界

`.onecodeignore` 应由一个共享的 `IWorkspaceIgnoreProvider` 或等价服务加载一次，并被以下入口消费：

1. `GlobTool`：匹配文件前排除；
2. `LS`/tree/文件列表命令：不展示被忽略路径；
3. `GrepTool`/`ITextSearchService`：传递 exclude glob 给 ripgrep，fallback 扫描也使用同一判定器；
4. 上下文收集器：向模型附加文件内容前过滤；
5. 设计上下文、项目初始化和相关索引器：不主动读取被忽略文件；
6. 需要时，LSP 上下文提供器在批量收集文件时过滤输入，但不改变 LSP 本身对单个用户明确请求的语义。

`.onecodeignore` 不应被应用到：

- 用户明确指定的单文件读取请求；
- 用户明确指定的编辑、删除或执行目标；
- 权限检查和路径安全检查。

对于显式请求，工具仍必须先执行现有路径校验和权限校验；ignore 只是“默认发现过滤”，不是拒绝访问机制。工具返回结果中可提示“该路径被 `.onecodeignore` 排除，已按显式请求继续”，避免产生隐式行为。

#### 5.4.1 ripgrep 集成语义（防止第三套语义）

ripgrep 默认会自行读取 `.gitignore`、`.ignore`、`.rgignore` 等文件。若只把用户规则转成 `--glob !<pattern>` 传入，同一个文件可能被三套规则同时判定：`.onecodeignore` 解析器、rg 原生 ignore、C# fallback 判定器，且 `/foo` 根锚定与 `**/` 在 `--glob` 与 ignore 文件中的含义不同。实现约束：

- rg 路径必须显式关闭原生 ignore 读取：`--no-ignore`；用户规则经 `--ignore-file <workspace>/.onecodeignore` 作为显式文件传入；
- rg 进程必须基于 `IWorkingDirectoryAccessor` 以工作区根为运行目录，使 `--ignore-file` 的 `/foo` 根锚定与 `**/` 语义对齐 fallback 判定器；
- 内置排除规则（§5.3）由 OneCode 生成 glob 参数或预编译 Matcher 后传给 rg，不依赖 rg 的 ignore 文件解析；
- rg 参数需同时设置 `--ignore-file-case-insensitive` 与 `--glob-case-insensitive`，使 ignore/glob 的大小写行为与 C# fallback 的 `OrdinalIgnoreCase` 全局匹配一致；
- `--glob !<pattern>` 仅承载 GrepTool 参数级 `exclude_glob`（用户临时过滤），与 `.onecodeignore` 的 `--ignore-file` 通道分离，不混用；
- 两条搜索路径对同一规则必须给出相同结果，由 §7.2 测试矩阵锁定。

### 5.5 加载和错误处理

建议服务提供以下能力：

```text
Load(workspaceRoot) -> IgnoreSnapshot
IsIgnored(relativePath, kind) -> bool
ApplyExcludes(matcher)
GetDiagnostics() -> IgnoreDiagnostics
```

`IgnoreSnapshot` 应包含：

- ignore 文件路径；
- 文件最后写入时间或内容版本；
- 已解析规则；
- 解析错误列表；
- 是否存在用户 ignore 文件。

处理策略：

- 文件不存在：只使用内置规则；
- 文件为空：等同于没有用户规则；
- 单行语法错误：跳过该行并记录诊断；
- 文件无法读取：保留内置规则，并记录 warning；
- 文件变更：通过现有 watcher 或按工作区快照失效机制重新加载；
- 规则数量过大：设置上限，超过上限时截断并记录 warning，防止恶意或误配置导致搜索成本失控。

### 5.6 性能和缓存

- 一个工作目录只保留一个当前 ignore 快照；
- Glob matcher、ripgrep 参数和 fallback 判定器共享解析后的规则；
- 不在每个文件上重新读取 `.onecodeignore`；
- 工作目录切换时必须创建新快照，不能复用旧工作区规则；
- watcher 事件应做 debounce，避免编辑器保存一次文件触发多次全量刷新；
- 初始实现不需要持久化 ignore 缓存，进程重启后重新加载即可。

## 6. 推荐实现分阶段

### 阶段 1：目录兼容和共享解析入口

1. 把技能目录候选和优先级集中到一个解析入口。
2. 增加 `.agents/skills`、`.cursor/skills` 的用户级和项目级只读发现。
3. 让 `SkillCatalog`、`SkillsCommand`、`SkillChangeWatcher` 共用候选目录结果。
4. 补齐冲突和标准路径测试。

验收：标准 `SKILL.md` 可被发现、渲染、注册和热刷新；现有 `.onecode` 技能的覆盖结果不变；技能快照缓存与 watcher 失效联动，同层同名裁决与排序在缓存/非缓存路径下结果一致。

### 阶段 2：ignore 核心服务和 Glob/LS

1. 实现 `.onecodeignore` 读取、解析和快照。
2. 将用户规则接入 `FileIgnore` 或其新的共享判定器。
3. 先接入 Glob、LS/tree 等文件枚举入口。
4. 增加诊断信息和 watcher 刷新。

验收：新增、删除、否定规则和 Windows 路径分隔符均有可验证行为。

### 阶段 3：Grep、上下文和索引入口

1. 将 ignore 规则传入 `ITextSearchService` 的 ripgrep 实现。
2. 在 C# fallback 中复用同一判定器。
3. 接入项目上下文、设计上下文、初始化和其他批量文件收集器。
4. 验证被忽略文件不会通过另一条上下文路径重新进入模型。

验收：Glob、Grep 和上下文收集对同一规则给出一致结果；rg 以 `--no-ignore` 关闭原生 ignore 读取，仅经 `--ignore-file` 应用 `.onecodeignore`，路径基准为工作区根。

### 阶段 4：可观测性和文档完善

1. `/doctor` 或 `/files` 诊断中显示 `.onecodeignore` 状态、规则数和解析错误。
2. `/skills` 显示技能来源路径，便于识别标准目录加载结果。
3. 更新 `docs/skills.md`、`docs/settings.md` 和相关命令帮助。
4. 在 README 中补充最小示例和安全说明。

## 7. 测试设计

测试应验证真实业务产出，不测试常量或简单属性。

### 7.1 标准技能目录

建议新增或扩展 `SkillCatalogTests`：

- 项目 `.agents/skills/name/SKILL.md` 可发现并生成动态技能；
- 用户 `.agents/skills` 可发现，项目技能可覆盖用户技能；
- `.onecode/skills` 对兼容目录中的同名技能保持现有本地优先级；
- 同时存在单文件和目录布局时，名称与正文来源正确；
- frontmatter 无效的一个文件不会阻止其他技能加载；
- 标准目录中的技能修改后，watcher 刷新结果能反映新正文；
- `/skills` 或对应诊断能区分技能来源，且不泄露正文；
- 同一层内 `foo.md` 与 `foo/SKILL.md` 同名时裁决结果固定且诊断正确；
- 技能快照缓存命中时结果与全量解析一致；watcher 刷新后能反映新增/删除/修改技能；
- 编辑单个技能文件后立即调用 `/<skill-name>` 能反映新正文（轻量校验路径）。

测试目录使用临时工作区，测试结束删除临时文件；不依赖用户真实 home 目录。

### 7.2 `.onecodeignore`

建议新增 `WorkspaceIgnoreTests` 或等价测试类：

- 不存在 ignore 文件时保留内置排除行为；
- 普通文件、目录、`**`、根路径和 `!` 规则产生预期过滤结果；
- Windows `\\` 路径和 `/` 路径给出一致结果；
- 用户否定规则不能恢复内置 `.git`、`bin`、`obj` 等目录；
- ignore 文件解析错误只影响错误行，其他规则仍然生效；
- Glob 不返回被忽略文件；
- Grep 的 ripgrep 路径和 C# fallback 对同一规则给出一致结果；
- 上下文收集不会注入被忽略文件；
- ignore 文件变更后新规则立即或在约定刷新周期内生效；
- 显式请求单个被忽略文件仍遵守路径安全和权限检查，而不是被 ignore 静默拒绝；
- rg 在 `--no-ignore` 下不受 `.gitignore`/`.ignore` 影响，与 fallback 判定器对同一 `.onecodeignore` 给出相同结果（覆盖 `/foo` 根锚定、`**/`、`!` 用例）；
- 工作区同时存在 `.gitignore` 与 `.onecodeignore` 时，仅 `.onecodeignore` 的规则参与过滤，不引入第三套语义。

### 7.3 集成测试

至少保留一个真实工作区集成测试，验证以下链路：

```text
.onecodeignore
  -> 文件发现
  -> 内容搜索
  -> 上下文收集
  -> Agent 可见输入
```

该测试的重点是防止后续新增文件入口绕过共享 ignore 服务。

## 8. 文档和兼容性变更

实现完成时同步更新：

- `docs/skills.md`：补充标准目录、来源和覆盖顺序；
- `docs/settings.md`：说明 `.onecodeignore` 不是 settings 配置，也不是安全 ACL；
- `README_CN.md`：补充最小使用示例；
- 相关命令帮助：说明技能来源和 ignore 诊断方式；
- 如新增 Core/App 公共 API，补充 XML 文档和变更说明。

不需要增加新的 settings 键。`.onecodeignore` 是约定文件，避免把任意 glob 文本塞入 JSON 配置并扩大配置合并复杂度。

## 9. 风险与决策记录

### 风险：技能覆盖结果改变

**处理**：新增生态目录默认低于现有 OneCode 本地目录；增加冲突诊断和回归测试。

### 风险：忽略规则被误解为安全边界

**处理**：文档和工具描述明确说明 ignore 只影响主动发现；显式操作仍走路径安全和权限检查。

### 风险：ripgrep 与 fallback 语义不一致

**处理**：规则先解析成共享中间表示；两条搜索路径使用同一测试矩阵，并保留一个集成回归测试。

### 风险：新文件入口绕过 ignore

**处理**：禁止各业务组件自行读取 `.onecodeignore`；统一依赖工作区 ignore 服务，并在代码评审清单中检查新的文件枚举器。

### 风险：标准目录过多导致扫描成本增加

**处理**：只扫描固定候选目录的固定深度；目录不存在时不创建；对每个路径做去重；刷新时使用 debounce；技能解析结果以 watcher 驱动的快照缓存避免每次调用全量重扫描（见 §4.6）。

## 10. 建议的完成定义

功能只有在以下条件全部满足时才视为完成：

- 标准技能目录能被发现、覆盖、热刷新并显示来源；
- 现有技能目录和覆盖规则的回归测试通过；
- `.onecodeignore` 能过滤 Glob、LS/tree、Grep 和至少一个上下文入口；
- ripgrep 与 fallback 的规则结果一致；
- ignore 解析错误可诊断，不会导致整个文件工具不可用；
- 显式文件操作仍受现有路径安全和权限系统控制；
- `docs/skills.md`、`README_CN.md` 和命令帮助已同步；
- 技能目录扩展后解析行为确定：同层同名裁决与跨平台排序一致，watcher 驱动的快照缓存保留“编辑后立即生效”语义；
- ripgrep 路径采用 `--no-ignore` + `--ignore-file`，Glob / Grep（rg 与 fallback）/ 上下文收集对同一规则语义一致；
- `dotnet build src/OneCode.slnx` 与 `dotnet test src/OneCode.slnx` 通过，且没有新增警告。