# MAF Workflows.Declarative 评估结论

**状态**: Rejected (议题关闭)
**日期**: 2026-07-09
**关联**: 原生产级重构计划 §9.2 M3（该计划文档未随仓库提交——工作区与 git 历史均无此路径）

## 语境

`Microsoft.Agents.AI.Workflows.Declarative` 的 `ResponseAgentProvider` 抽象定义在 Declarative 主包内，Azure Foundry 侧实现位于独立子包 `Microsoft.Agents.AI.Workflows.Declarative.Foundry`；但 Declarative 主包依赖 `Microsoft.Agents.ObjectModel` / `Microsoft.PowerFx.Interpreter` 等 Foundry 生态重依赖，项目因放弃 Azure Foundry 而未引入。M3 任务评估是否实现本地 `ResponseAgentProvider` 适配器来引入声明式 YAML 工作流。

## 评估结论

### 能力已被覆盖

项目已具备等价的声明式工作流能力，无需引入 MAF Declarative：

| 能力 | 现有实现 | MAF Declarative 对应 |
|------|----------|---------------------|
| YAML 团队配置 | `AgentTemplateConfig.FromYaml()` + `prompts/teams/*.yaml` | Declarative YAML spec |
| GroupChat 编排 | `TeamWorkflowRunner` 使用 `AgentWorkflowBuilder.CreateGroupChatBuilderWith` | Declarative schema 可表达（如 next-speaker 公式类多 Agent 协作） |
| Magentic 编排 | `TeamWorkflowRunner` 使用 `MagenticWorkflowBuilder` | Declarative schema 可表达（如 next-speaker 公式类多 Agent 协作） |
| 用户自定义模板 | `~/.onecode/teams/{name}/team.yaml` | Declarative spec 路径 |

### 适配器成本与风险

1. **API 阻抗失配**：`ResponseAgentProvider` 是 Response API 抽象，与本地 `IChatClient` 的 messages API 模型不一致，适配器需转换消息格式并可能模拟状态语义
2. **API 形状未知**：包未还原到本地（截至 2026-07-09 评估时），无法预先检视 `ResponseAgentProvider` 抽象成员，POC 需先做信息收集阶段
3. **实验性 API 漂移**：项目大量 MAF API 带 `[Experimental]`，编译期由 `src/Directory.Build.props` 的全局 `NoWarn` 抑制 `MAAI001`（见 `src/OneCode.Infrastructure/AGENTS.md`），Declarative 子包稳定性更不可控
4. **双轨风险**：引入 MAF Declarative YAML spec 后与现有 `AgentTemplateConfig` 体系可能并存，需明确替换策略

## 决策

**关闭 M3 议题，不做本地 Declarative 适配器 POC。**

理由：
- 声明式工作流能力已被现有 YAML 体系覆盖，边际收益弱
- 适配器实现成本高且不确定（API 未知 + 阻抗失配 + 实验性 API 风险）
- 与原计划"不成则关闭议题"的退出路径一致（原生产级重构计划.md:338，该文档未随仓库提交）

## 影响

- 维持现有代码式工作流路径（`TeamWorkflowRunner` + `AgentTemplateConfig`）
- 不引入 `Microsoft.Agents.AI.Workflows.Declarative` 包依赖
- 未来如需重新评估，需先确认 MAF 是否提供不依赖 Azure Foundry 的 Declarative 实现
