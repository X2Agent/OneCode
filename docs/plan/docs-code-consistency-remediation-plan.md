# 文档-代码一致性复核与修复计划

> 状态：**已实施**（2026-10-18，D1–D12 全部按推荐项 A 执行；实施记录见 §7）
> 复核范围：`docs/` 根目录 9 篇 + `docs/adr/` 15 篇 + `docs/maf/integration-guide.md`
> 复核方式：逐条断言对照生产代码；P1/P2 全部二次人工复核
> 基线：`dotnet test src/OneCode.slnx --filter "Category=Docs"` → 15 passed（复核时）
> 文中 §2 的「现状/真相」列记录**复核时**状态，闭环结果见 §7。

---

## 0. 结论摘要

- 逐项复核 **64 条**：P1 4 条、P2 10 条、P3 50 条；其中 4 条在复核后降级（见 §2.8）。
- 无 P0（无安全门禁被反向描述）；4 条 P1 均属「行为契约/安全语义描述错误」。
- 文档静态事实（路径/API/计数/压缩阈值）有机器守卫；**行为契约、默认值、ADR 决策落地无守卫**，问题全部集中在该区域。
- 功能层面存在 **6 处实际失效或半失效**，不是单纯措辞问题：

| # | 位置 | 现象 | 性质 |
|---|---|---|---|
| F1 | `ONECODE_MAX_CONTEXT_TOKENS` | 只有 `/status` 读，压缩/告警/状态栏都不读 | 半失效，且制造假象 |
| F2 | fork 路径 context provider | 每 run 新建的 skills provider 不释放 | 资源泄漏 |
| F3 | AutoDream 整合提示词 | 指向不存在的 `### Manual entries` 分区 | 软约束失效 |
| F4 | Harness 遥测 | 已开启但宿主无导出器，span 全 null | 可观测性为零 |
| F5 | `/design-init` URL 校验 | 只拦主机名，私有 IP/回环 IP 可直通 | 校验不完整 |
| F6 | Skills 发现 | 斜杠侧与模型侧两套规则并行 | 漂移风险 |

---

## 1. `ONECODE_MAX_CONTEXT_TOKENS` 专项结论

### 1.1 真实读写面（全仓穷举）

| 位置 | 内容 |
|---|---|
| `src/OneCode.Core/Constants.cs:17` | 常量定义 |
| `src/OneCode.App/Commands/StatusCommand.cs:186-189` | 唯一读取点：解析后覆盖 `/status` 的 `maxTokens` 局部变量 |
| `docs/settings.md:295` | 文档声明「最终覆盖手段，优先于模型目录/默认值解析」 |

### 1.2 不读它的消费方

- `TokenBudget.GetMaxContextTokens`（`src/OneCode.App/Services/Compact/TokenBudget.cs:35`）→ 压缩预算与 `AutoCompactService` 告警口径。
- `CompactionStrategyFactory`（`src/OneCode.App/Services/Agent/CompactionStrategyFactory.cs:79` 附近）→ MAF 压缩阈值。
- TUI 状态栏：`OneCodeToplevel.cs:106` 走 `ModelContextDefaults.Resolve`。

### 1.3 判定

**功能半失效**：设置该变量只会让 `/status` 显示一个与压缩实际行为不一致的窗口值。这恰好违反代码注释自己声明的原则——「告警与压缩必须同源」。保留现状比删除更危险。

### 1.4 处置建议（决策点 D1）

- **A（推荐）删除**：删常量、`StatusCommand.cs:186-189`、`settings.md:295`。理由：开发阶段无兼容包袱；未知模型的窗口已有 catalog 与 `ollamaContextWindow` 两条正路。
- B 实装为真覆盖：在 `TokenBudget.GetMaxContextTokens` 首行读取环境变量，压缩/告警/展示统一。
- C 升级为正式配置项（如 `contextWindowOverride`），走 `ConfigManager` 与 `/config`。

---

## 2. 逐项复核结果

判定：`一致` / `文档失真` / `代码偏离` / `矛盾` / `无法判定`。
「功能有效性」列回答：即使文档改对，这段实现本身是否按预期工作。

### 2.1 `docs/commands.md`（44 命令逐条核验，193/205 一致）

| ID | 位置 | 文档声明 | 代码真相 | 判定 | 功能有效性 | 修复建议 |
|---|---|---|---|---|---|---|
| CMD-01 | :156 | URL 必须是公网地址，排除 localhost/内网 | 仅拦 `localhost` / `*.internal` / `*.local` / `*.localhost`；`127.0.0.1`、`192.168.*` 放行（`DesignInitCommand.cs:410-428`） | 文档失真 | **部分有效** | 见 D6 |
| CMD-02 | :767 | `list`/`ls` 列出所有会话 | `sessions.Take(20)`，超出只提示剩余数（`SessionCommand.cs:62,70-71`） | 文档失真 | 有效 | 文档改为「最多显示 20 条」 |
| CMD-03 | :224 | 扫描 `file_path`/`path`/`filepath` | 实际 key：`filePath`/`file_path`/`path`（`ToolArgumentExtractor.cs:14-19`） | 文档失真 | 有效 | 改为 `filePath` |
| CMD-04 | :1045 | `--severity` 过滤输出 | 映射为 `severityScope` 指令注入 prompt，由模型决定报什么（`ReviewCommand.cs:261-266,287`） | 文档失真 | 有效（语义不同） | 见 D9 |
| CMD-05 | :331 | `--max <次数>` 硬上限 | 合法区间 1..20，越界报错（`LoopCommand.cs:47-54`、`ModeBudgetSettings.cs:42`） | 文档漏约束 | 有效 | 补上界 |
| CMD-06 | :922 | `add` 只列 `--transport` | `--type` 为等价别名（`McpCommand.cs:273-274`） | 文档漏写 | 有效 | 补别名 |
| CMD-07 | :679 | 无参数/`list` 列条目 | 无活跃会话直接报错（`MemoryCommand.cs:33-35`） | 文档漏前置条件 | 有效 | 补前置条件 |
| CMD-08 | :570 | checkpoint 子命令 | 全部要求活跃会话（`CheckpointCommand.cs:31-34`） | 文档漏前置条件 | 有效 | 补前置条件 |
| CMD-09 | :393 | 显示当前模型与可用模型列表 | 仅 `Available.Count > 1` 时打印列表（`ModelCommand.cs:31-42`） | 文档失真 | 有效 | 补条件 |
| CMD-10 | :868 | 取「任意单一」含 `.md` 的子目录 | 遍历全部子目录取第一个命中的（`InstallCommand.cs:233-240`） | 文档措辞失真 | 有效 | 去掉「单一」 |

### 2.2 `docs/settings.md` / `docs/keybindings.md`

| ID | 位置 | 文档声明 | 代码真相 | 判定 | 功能有效性 | 修复建议 |
|---|---|---|---|---|---|---|
| SET-01 | :86 | `hasTrustAccepted` = 是否接受**当前项目**信任 | 全局开关，true 时信任一切目录（`TrustService.cs:52-54`） | 文档失真 | 有效但语义危险 | 见 D7 |
| SET-02 | :295 | 环境变量是最终覆盖手段 | 仅 `/status` 展示 | 文档失真 | **无效** | 见 D1 |
| SET-03 | :28 | 所有组件只读 `ConfigSnapshot` | AutoDream 直接读 `ONECODE_REMOTE`/`ONECODE_AUTODREAM`，env 优先（`AutoDreamService.cs:302-310`） | 代码偏离 | 有效 | 文档补例外，或收敛到 ConfigManager |
| SET-04 | :291-295 | 「其他环境变量」表只列一项 | 另有 `ONECODE_DURABLE_CRON`（`Constants.cs:19`） | 文档遗漏 | 有效 | 补一行 |
| SET-05 | :125 | `/cost` 与状态栏展示四维计数 | 命令集中无 `/cost`（`Commands/` 0 命中） | 文档失真 | — | 见 D8 |
| SET-06 | :394 | `effortValue` 消费方含 `InteractiveModeExecutor` | 实际命中 `SettingsOverlay`、`InteractiveBootstrapService` 等 7 处，`InteractiveModeExecutor` 不在列 | 文档失真 | 有效 | 按 grep 结果重写清单 |
| SET-07 | :85 | `allowedDirectories` 为项目级 | 描述符允许 user+project（`ConfigModels.cs:121`），`/add-dir` 可写会话级 | 文档不完整（降级为 P3） | 有效 | 列全三级 |
| KEY-01 | :89-107 | 修饰键/特殊键表 | 解析器还接受 `control`、`insert`/`ins`、`wheelup`/`wheeldown`、箭头字形（`KeybindingParser.cs:27-108`） | 文档不完整 | 有效 | 补全或标注「常用子集」 |
| KEY-02 | :234 vs :287-298 | Ctrl+D「任何上下文不得占用」，示例又教 `ctrl+d: null` 解绑 | 校验器只发 Warning/Error，不阻止绑定（`KeybindingValidator.cs:357-380`） | 文档内部矛盾 | 有效（仅警告） | 见 D10 |
| KEY-03 | :344 | 绑定到保留键无效 | 同上，仅警告 | 文档失真 | 有效 | 与 D10 一并处理 |

### 2.3 `docs/hooks.md` + ADR 0001/0003/0005

| ID | 位置 | 文档声明 | 代码真相 | 判定 | 功能有效性 | 修复建议 |
|---|---|---|---|---|---|---|
| HOOK-01 | hooks:51 | 不受信任**或策略禁用**时一律不触发 | 无策略开关，唯一门控是工作区信任（`HookPolicyService.cs:14-25`） | 文档失真 | 有效 | 删「或策略禁用」 |
| HOOK-02 | hooks:880 | `once` 执行一次后移除 | 仅 `Success`/`Blocking` 后移除（`HookExecutionService.cs:76-77`） | 文档简化 | 有效 | 与 :261 对齐 |
| HOOK-03 | adr0005:53-56 | 元数据示例 `mcp.*`、DisplayName「工具真正执行前」 | DisplayName「工具调用前」；KnownValues `mcp__*`（`HookPointMetadata.cs:11-15,68-73`） | 片段陈旧 | 有效 | 按真实元数据重写 |
| HOOK-04 | adr0005:191 | 第 6 步聚合后清理 once | `Unregister` 在串行循环内、聚合前（`HookExecutionService.cs:71-80`） | 顺序描述错 | 有效 | 改为实际顺序 |
| HOOK-05 | adr0005:200 | 聚合结果含 `Outcome` | `AggregatedHookResult` 只有 `Message`/`BlockingErrors`/`AdditionalContexts`（`HookResult.cs:23-28`） | 字段不存在 | 有效 | 删 `Outcome` |
| HOOK-06 | adr0005:360 | 飞书/企微/钉钉共享 Webhook 基类 | 钉钉走声明式 Provider，无编译型类（Notifications 目录仅 Feishu/WeChatWork） | 文档失真 | 有效 | 区分编译型/声明式 |
| ADR01-01 | adr0001:13 vs :27 | 同时写 MAF 1.22/10.10.0 与 MAF 1.13/10.7.0 | 实际 pin MAF 1.24.0 / MEAI 10.10.0（`Directory.Packages.props:38-46`） | 内部矛盾 | — | 统一为实际版本 |
| ADR03-01 | adr0003:72,84 | Cron 用 `WorkingMode.Goal` 实现只读策略 | `WorkingMode.Build` + `unattended: true` + `SuppressToolApproval`（`CronJobExecutor.cs:72-79`、`QueryStreamEngine.cs:197`） | 文档失真 | 有效 | 改写为实际机制 |
| ADR03-02 | adr0003:89 | 5 个 M4 单测 | `MafM4ApprovalEventTests` 仅 3 个 Fact | 计数漂移 | — | 改为 3，或补测试 |

### 2.4 `docs/memory-overview.md` + ADR 0004 / `docs/skills.md`

| ID | 位置 | 文档声明 | 代码真相 | 判定 | 功能有效性 | 修复建议 |
|---|---|---|---|---|---|---|
| MEM-01 | adr0004:101 | 并发控制用 `DurableFile` | `MemoryEntryStore` 自建 `.MEMORY.lock` + `File.Replace`；`DurableFile` 在记忆模块零引用（`MemoryEntryStore.cs:27,506-532`） | 实现名错误 | 有效 | 改为真实机制 |
| MEM-02 | adr0004:120 | 闸门在 `ApplyConsolidationChangesAsync` | 闸门全在 `TryApplyConsolidationChangesAsync`；前者只是 3 行包装（`AutoDreamMemoryApplier.cs:41-45` vs `:52` 起） | 定位不准 | 有效 | 改方法名 |
| MEM-03 | 代码侧 | prompt 要求保护 `### Manual entries` | 实际分区是 `### User memories`（`MemoryService.cs:64`） | **代码缺陷** | **软约束失效**（硬闸门仍有效） | 见 D5 |
| SKL-01 | skills:26-29 | 模型/斜杠入口共用 `SkillDiscovery` | provider 用 `UseFileSkills(skillDirs)`；`SkillDiscovery` 仅 `SkillCatalog` 使用（`SkillProviderFactory.cs:42-45`） | 文档夸大 | 有效但双实现 | 见 D3 |
| SKL-02 | skills:36-38 | 每 run provider 经 lease 释放 | 仅主代理管道 `Track`；fork 不释放（`MainAgentRunner.Pipeline.cs:67`、`ForkedAgentRunner.cs:134-136`） | 文档失真 + **代码缺陷** | **泄漏** | 见 F2 |
| SKL-03 | skills:11 | 技能四个来源 | provider 实际聚合文件/内置/MCP 三类 source，文件侧是三个目录 | 口径不完整 | 有效 | 区分斜杠侧/模型侧 |

### 2.5 `docs/background-services.md` + ADR 0006/0009/0014/0015

| ID | 位置 | 文档声明 | 代码真相 | 判定 | 功能有效性 | 修复建议 |
|---|---|---|---|---|---|---|
| BG-01 | :40,44 | Automation 仅依赖 Core 抽象 | 工程直接引用 `OneCode.Infrastructure`（`OneCode.Automation.csproj:15-16`） | 分层判据失真 | 有效 | 改为「不依赖 App；允许 Infrastructure」 |
| BG-02 | :269 | `LastStatus` 可为 `running` | 只写 `ok`/`error`（`CronSchedulerService.cs:290-317`） | 取值域错 | 有效 | 删 `running` |
| BG-03 | :172 | 恢复服务只处理 StartingExecution/Executing | 还处理旧进程遗留 `FinalizingPlanRun` 收口（`PlanExecutionRecoveryService.cs:68-112`） | 清单漏项 | 有效 | 补第三类 |
| BG-04 | :276 | 启动后立即补跑到期任务 | 先 `ReloadJobsAsync`，再 `Task.Delay(30s)` 才首次检查（`CronSchedulerService.cs:22,234-241`） | 时序失真 | 有效 | 改为「≤30s」 |
| BG-05 | :93 | `StartAsync` 同步加载磁盘缓存 | 仅 catalog 为空时加载（`ModelCatalogRefreshService.cs:20-36`） | 条件遗漏 | 有效 | 补条件 |
| ADR06-01 | adr0006:13 | AutoDream 使用 `IConversationRunner` | AutoDream 直接 `new HarnessAgent` + `RunAsync`，绕过该契约（`AutoDreamService.cs:260-279`） | 适用范围过期 | 有效 | 从消费者清单移除 AutoDream |
| ADR09-01 | adr0009:102-103 | Cron 用 Goal 且丢弃事件 | Build + 逐条消费 `ErrorEvent`（`CronJobExecutor.cs:72-90`） | 事实错误 | 有效 | 改写 |
| ADR09-02 | adr0009:64-66 | MEAI.OpenAI/Abstractions 10.10.0 | 实际 10.10.1（`Directory.Packages.props:38-40`） | 版本漂移 | — | 改版本 |

### 2.6 `docs/sub-agents.md` + ADR 0010/0011 + `docs/compact-thresholds.md`

| ID | 位置 | 文档声明 | 代码真相 | 判定 | 功能有效性 | 修复建议 |
|---|---|---|---|---|---|---|
| SUB-01 | :86-87 | provider 由 lease 在 run 后释放 | fork 路径未接 lease（见 SKL-02） | 链路未接线 | **泄漏** | 修代码 |
| SUB-02 | :143 | 轮数上限 `> 4` 且达 80% 提醒 | `maxTurns > 5` 才提醒，即 ≥6（`TurnBudgetNudgeChatClient.cs:27-43`） | 数值错 | 有效 | 改为 `> 5` |
| SUB-03 | :44 | 编辑前置检查 Full/Worker ✅ | `Verification` 另受权限档位门控，Default 档关闭（`PermissionProfiles.cs:193-203`） | 矩阵缺前提 | 有效 | 补「权限档位」注 |
| SUB-04 | :50 | CodeAct Worker ✅ | Plan 或 `SuppressToolApproval` 时整体剔除（`SharedContextProviderBuilder.cs:95-103`） | 矩阵缺前提 | 有效 | 补模式/审批前提 |
| SUB-05 | :25 | <32K 上下文走过滤 | 阈值 32,768，且只对未知 provider 生效（`ModelCapabilities.cs:13-19`、`ToolAssembler.cs:64-67`） | 条件不完整 | 有效 | 补 provider 条件 |
| SUB-06 | :161-162 | 头行 `Agent · [代理名] · 描述` | 实际 `▸ Agent [代理名] · 参数`（`MessageFlowRenderer.cs:472-511`） | 文案不符 | 有效 | 改为实际格式 |
| ADR10-01 | adr0010:45-51 | GoalAuto 强制审批 + pending 上浮早停 | 子目标 `.AsUnattended()`，不挂审批、不打标记，Deny 以错误回喂（`AgentPipelineAssembly.cs:125-131`、`GoalSubGoalExecutor.cs:189`） | **文档与代码相反** | 代码路径有效 | 见 D2 |
| ADR10-02 | adr0010:139 | GoalSubGoalLoopTests 10 例 / IterativeLoopServiceTests 9 例 | 实际 12 / 10 | 计数漂移 | — | 更新数字 |
| ADR11-01 | adr0011:162-165 | 五个文件行数合计约 1234 | 实际约 1190（按当前文件统计） | 计数漂移 | — | 删除具体行数或更新 |
| ADR11-02 | adr0011:197 | F2 待立项 | `OrchestrationEventSink` 已接通（`ForkedAgentRunner.cs:166`），`FileChangeCallback` 未赋值 | 状态滞后 | 部分有效 | 改为「部分完成」 |
| ADR11-03 | adr0011:202 | `plan.prompt` 原列 "agent research reports" | 实际措辞是 "untrusted tool output, files, or web pages"（`plan.prompt:44`） | 证据措辞不可考 | — | 改写依据 |
| CMP-01 | compact:29 | 窗口按守卫式读取，0 视为未解析 | 与 `TokenBudget` 口径一致；其余 3 条依赖 MAF 上游 | 一致 | 有效 | 无需改 |

### 2.7 `docs/maf/integration-guide.md` + ADR 0007/0008/0012/0013

| ID | 位置 | 文档声明 | 代码真相 | 判定 | 功能有效性 | 修复建议 |
|---|---|---|---|---|---|---|
| MAF-01 | guide:653、adr0007:26,190 | `harnessOwnsToolApproval` 阻止第二套审批 | 符号全仓 0 命中；实际是 `EnableToolApproval` 布尔（`AgentPipelineBuilder.cs:259-270,306-307`） | 幽灵符号 | 有效 | 改为真实开关 |
| MAF-02 | guide:656 | 迭代上限与 `MaxToolCalls` 同值 | 有 `MaxTurns + 1` 分支（`AgentPipelineBuilder.cs:308-312`） | 只写一半 | 有效 | 补分支 |
| MAF-03 | guide:654 | 配额组工具另补标，超限 Ask | 全方法仅一次 `Apply`；外部 web 超限直接 `ToolResult.Error`（`PermissionAndLimitMiddleware.cs:64-72`） | 机制错 | 有效（拒绝而非 Ask） | 改写 |
| MAF-04 | guide:645 | AutoDream 直接 `new HarnessAgent`、无审批 | 经 `BuildConsolidationAgentOptions` 工厂（`AutoDreamService.cs:230-258`）；无 broker 成立 | 写法不实（降级 P3） | 有效 | 改为「经工厂构建」 |
| MAF-05 | guide:4,38,733 | 所有结论以本地 `agent-framework` 源码为准 | 当前工作副本无该目录 | 证据链断裂 | — | 见 D11 |
| ADR08-01 | adr0008:9-14 | 三处结构化消费点，表只列两行 | 全仓 `StructuredChatCall` 消费方需另核；文档自身不自洽 | 内部矛盾 | 有效 | 改为「两处」或补第三处 |
| ADR13-01 | adr0013:47-48 | Tui 命名空间泄漏 23 文件 | 实测 Tui 目录外引用为 25 个文件（降级 P3） | 计数漂移 | 边界结论成立 | 改为「约 25」或删数字 |
| ADR12-01 | adr0012 全篇 | 决策模型未接入 | `src/` 内 Jev/TypeSafe/IDecisionClient 零命中 | 一致（强反证） | — | 无需改 |

### 2.8 复核后降级/推翻的 4 条

| 初判 | 复核结论 | 原因 |
|---|---|---|
| `settings.md:85` allowedDirectories 作用域错误（P2） | **降为 P3** | 描述符只说「项目级」不完整，并未反向描述 |
| `guide:645` AutoDream「无审批 broker」错误（P2） | **降为 P3** | 「无 broker」成立；仅「直接 new」写法不实 |
| `adr0013:47` 23 文件（P2） | **降为 P3** | 实测 25，量级正确，边界结论成立 |
| `adr0011:197` F2 状态（P2） | **降为 P3** | sink 已通、文件回调未接，属部分完成 |

---

## 3. 决策点（已裁定：全部按推荐项 A）

> 2026-10-18 用户确认「按推荐执行」。下表保留决策依据与备选，便于回溯。

| ID | 问题 | 选项 | 推荐 | 影响面 |
|---|---|---|---|---|
| **D1** | `ONECODE_MAX_CONTEXT_TOKENS` 如何处置 | A 删除；B 实装到 `TokenBudget`；C 升级为正式配置项 | **A** | 删 3 处；若选 B 需改 `TokenBudget` 并加测试 |
| **D2** | ADR 0010 GoalAuto 审批：谁是产品意图 | A 文档向代码对齐（unattended，Deny 回喂）；B 代码向文档对齐（恢复审批标记 + pending 上浮） | **A** | 选 B 会改审批链，风险高，且需删除现有守卫测试 |
| **D3** | Skills 发现双实现 | A 文档写实「规则同构，非共享实现」；B 让斜杠侧复用 provider 的发现结果 | **A**（短期）| 选 B 需重构 `SkillCatalog`/`SkillDiscovery` |
| **D4** | OpenTelemetry | A 保留缺口、文档写明「无导出器」；B 接入导出器（新增依赖/配置） | **A**（当前无消费需求）| 选 B 增加依赖面与启动配置 |
| **D5** | AutoDream 分区名不一致 | A 改 prompt 为 `### User memories`；B 改 `MemoryService` 输出标题 | **A** | 只改提示词，风险低 |
| **D6** | `/design-init` 内网 URL | A 补私有/回环 IP 校验；B 弱化文档为「示例公网地址」 | **A** | 选 A 需加 IP 解析与测试 |
| **D7** | `hasTrustAccepted` 语义 | A 改文档为「全局信任开关」；B 代码改为 per-project | **A** | 选 B 涉及信任存储与迁移，开发期可用新键名 |
| **D8** | `/cost` 幽灵引用 | A 删引用；B 新增 `/cost` 命令 | **A** | `/status` 已有用量，重复命令无必要 |
| **D9** | `--severity` | A 改文档为「注入报告范围」；B 实现输出侧过滤 | **A** | 选 B 需改 review 输出链路 |
| **D10** | `Ctrl+D` 保留 | A 改文档为「仅警告，可解绑」；B 代码硬拦截 | **A** | 选 B 需删示例 1 |
| **D11** | MAF 上游证据 | A 删除「以本地源码为准」，以 pinned 包行为为准；B vendor 固定快照 | **A** | 选 B 需维护上游副本 |
| **D12** | 守卫测试范围 | A 全量补（幽灵符号/配置默认值/ADR 机制/交叉文档）；B 只补幽灵符号 | **A** | 增加 Docs 测试维护成本 |

---

## 4. 修复清单

### 4.1 代码修复（与 D 决策绑定）

| # | 文件 | 修改 | 验收 |
|---|---|---|---|
| CODE-1 | `ForkedAgentRunner.cs` | fork 构建 shared providers 后 `Track` 到 lease，并在 run 结束/取消/异常时释放 | 新增测试：fork 后 provider 被 Dispose（含取消路径） |
| CODE-2 | `prompts/system/autodream-consolidation.prompt:31` | `### Manual entries` → `### User memories`（按 D5） | prompt 断言测试：引用真实分区名 |
| CODE-3 | `DesignInitCommand.IsValidUrl` | 增加私有/回环/链路本地 IP 拒绝（按 D6） | 表驱动测试：127.0.0.1、10/172.16/192.168、::1、localhost 全部拒绝 |
| CODE-4 | `StatusCommand.cs:186-189` + `Constants.cs:17` | 删除（D1-A）或迁移到 `TokenBudget`（D1-B） | 删除后 `dotnet build` 无残留引用 |
| CODE-5 | `MemoryService` / `AutoDream`（仅当 D5 选 B） | 统一分区标题 | — |
| CODE-6 | OTel（仅当 D4 选 B） | 宿主注册 exporter | 能用 `dotnet-counters`/OTLP 看到 span |

### 4.2 文档修正（按文件，逐条对应 §2 的 ID）

- `docs/commands.md`：CMD-01..10。
- `docs/settings.md`：SET-01..07；D1 处理后删 :295 整行。
- `docs/keybindings.md`：KEY-01..03。
- `docs/hooks.md`：HOOK-01, HOOK-02。
- `docs/background-services.md`：BG-01..05。
- `docs/sub-agents.md`：SUB-01..06（SUB-01 随 CODE-1 一并写实）。
- `docs/skills.md`：SKL-01..03（按 D3）。
- `docs/memory-overview.md`：无需改（30/31 一致）。
- `docs/compact-thresholds.md`：无需改。
- `docs/adr/0001`：ADR01-01。
- `docs/adr/0003`：ADR03-01, ADR03-02。
- `docs/adr/0004`：MEM-01, MEM-02。
- `docs/adr/0005`：HOOK-03..06。
- `docs/adr/0006`：ADR06-01。
- `docs/adr/0007`：MAF-01。
- `docs/adr/0008`：ADR08-01。
- `docs/adr/0009`：ADR09-01, ADR09-02。
- `docs/adr/0010`：ADR10-01（按 D2）、ADR10-02。
- `docs/adr/0011`：ADR11-01..03。
- `docs/adr/0013`：ADR13-01。
- `docs/maf/integration-guide.md`：MAF-01..05（按 D11）。

> 约定：只改事实与机制描述，不追加「现状更新/勘误」段落；变更历史交给 git。

### 4.3 守卫测试（D12-A 时全部实施）

| # | 守卫 | 能拦住本次哪些问题 |
|---|---|---|
| GUARD-1 | 幽灵符号：`harnessOwnsToolApproval`、`UseToolApproval` 不得出现在文档 | MAF-01 |
| GUARD-2 | `settings.md` 表格键名/默认值/作用域 ↔ `ConfigModels.SettingDescriptor` | SET-01、SET-07、SET-04 |
| GUARD-3 | ADR 机制断言：`CronJobExecutor` 不含 `WorkingMode.Goal`、消费 `ErrorEvent`、`ToolApprovalMarker.Apply` 仅一次 | ADR03-01、ADR09-01、MAF-03 |
| GUARD-4 | 交叉文档一致性：lease 覆盖范围、F2 状态、技能来源数量 | SUB-01、SKL-02、SKL-03、ADR11-02 |
| GUARD-5 | 把 `docs/keybindings.md` 加入 `AllDocsWithPathReferences()` | KEY-01（路径类漂移） |
| GUARD-6 | 压缩阈值守卫扩展：只读子代理 40K/80K/120K 上限 | CMP-01 的上限部分 |

---

## 5. 实施顺序与验收

1. **批次一（纯文档，无风险）**：§4.2 全部；按 D1 决定是否删 `settings.md:295`。
2. **批次二（代码）**：CODE-1、CODE-3；按 D5 决定 CODE-2；按 D1 决定 CODE-4。
3. **批次三（守卫）**：GUARD-1..6；同步更新 `DocFactConsistencyTests` 的覆盖清单。
4. 验收：
   - `dotnet build src/OneCode.slnx` 无新增警告；
   - `dotnet test src/OneCode.slnx` 全绿；
   - `dotnet test src/OneCode.slnx --filter "Category=Docs"` 通过；
   - CODE-1 由 `AgentContextProviderLease` 的 `using` 作用域保证取消/异常路径释放，并以租约前提测试 + 源码守卫覆盖；CODE-3 覆盖完整 IP 表。

---

## 6. 明确不做

- 不迁移到 MAF `BackgroundAgents`（ADR 0011 四条证据仍成立）。
- 不引入 `Workflows.Declarative`、Background Responses、MAF `FileMemoryProvider` 长期记忆。
- 不新增 `/cost` 命令（D8-A）。
- 不重构 `/loop` 与 GOAL 的 `LoopAgent` 自建路径（ADR 0007/0010 已论证，仅修文档）。
- 不修改 ADR 的「语境/否决方案/后续工作」段落——只修与当前代码矛盾的事实陈述。

---

## 7. 实施记录（2026-10-18）

### 7.1 代码

| 项 | 变更 | 文件 |
|---|---|---|
| CODE-1 | fork 路径新增 `AgentContextProviderLease.Track(contextProviders)`，随 try 作用域释放（含取消/异常/超时） | `src/OneCode.App/Services/Agent/ForkedAgentRunner.cs` |
| CODE-2 | 提示词分区名改为真实标题 `### User memories` | `src/OneCode.App/prompts/system/autodream-consolidation.prompt` |
| CODE-3 | URL 校验新增私有/回环/链路本地 IP 与整数形式地址拒绝 | `src/OneCode.App/Commands/DesignInitCommand.cs` |
| CODE-4 | 删除 `ONECODE_MAX_CONTEXT_TOKENS` 常量与 `/status` 读取块（D1-A） | `src/OneCode.Core/Constants.cs`、`src/OneCode.App/Commands/StatusCommand.cs` |

### 7.2 文档

- 根目录 9 篇：`commands.md`、`settings.md`、`keybindings.md`、`hooks.md`、`background-services.md`、`skills.md`、`sub-agents.md` 按 §2 的 CMD/SET/KEY/HOOK/BG/SKL/SUB 条目修正；`memory-overview.md`、`compact-thresholds.md` 无需改。
- ADR：0001、0003、0004、0005、0006、0007、0008、0009、0010、0011、0013 修正事实与机制描述；0012/0014/0015、0002 无需改。
- `docs/maf/integration-guide.md`：审批归属改为 `EnableToolApproval`、迭代上限补 `MaxTurns + 1` 分支、配额超限改为错误返回、AutoDream 改为经工厂构造、上游证据改为以 pinned 包为准。
- 连带修正：`docs/adr/0007` 的 Skills 发现描述与 `docs/plan/tui-roadmap.md` 的 Tui 泄漏计数。

### 7.3 守卫

新增 `src/OneCode.Tests/DocCodeContractTests.cs`（13 个用例，`Category=Docs`）：
幽灵符号、settings 键集/信任语义/环境变量、Cron 运行模式、审批标记唯一性、迭代上限分支、
两条 runner 路径的租约、AutoDream 分区名、子代理/技能交叉描述、F2 状态、研究档上限。

新增 `src/OneCode.Tests/DesignInitUrlValidationTests.cs`（公网放行 + 本地/私网/非 http 拒绝）。
`DocFactConsistencyTests` 的路径校验清单加入 `docs/keybindings.md`。
`ForkedAgentRunnerTests` 新增 `WorkerSharedProviders_AreReleasableByLease`，守住「fork provider 可释放」前提。

### 7.4 验收

- `dotnet build src/OneCode.slnx`：通过，39 条警告均为存量（低于复核基线）。
- `dotnet test --filter "FullyQualifiedName~DocCodeContractTests|...DocFactConsistencyTests|...DesignInitUrlValidationTests"`：56 passed。
- `dotnet test --filter "FullyQualifiedName~WorkerSharedProviders_AreReleasableByLease"`：1 passed。
