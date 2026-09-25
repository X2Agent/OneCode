# Team 编排模式选型指南

与 `TeamOrchestrationModeExtensions`（单一事实源）同步维护。三种模式的映射、标签、适用性均以代码为准。

## 选型矩阵

| 特征 | ParallelDag | Magentic | GroupChat |
|---|---|---|---|
| 拓扑 | 静态扇出/扇入（编译期确定） | Orchestrator 动态分解委派 | 对等轮询发言 |
| 上下文 | 完全隔离 | Orchestrator 调度，Worker 各自执行 | 全员共享 |
| 成本 | 低 | 中 | 高（全上下文 × 每轮全员） |
| 终止 | DAG 聚合即终止 | max_rounds / 计划完成 | max_rounds + 活动计数收敛 |

## 决策规则

1. 子任务相互独立、编译期可知 → **ParallelDag**
2. 任务需动态分解、成员有上下游分工（计划→实现→验证）→ **Magentic**
3. 结论依赖成员互相看到并回应对方观点（选型辩论、方案对比）→ **GroupChat**

误用提示：视角独立型团队（instructions 含"只从 X 视角/不要评价…"）配置为 groupchat 时，
注册日志会输出 advisory 建议改用 parallel-dag（见 `TeamConfigLoader.BuildAdvisories`）。

> **实现说明**：ParallelDag 的"静态扇出/扇入"是**产品语义描述**，实现为按 `AssigneeRole` 选**单成员** +
> MAF `WorkflowBuilder` 手工链式绑定（`AIAgent.BindAsExecutor`）——**不是** MAF Concurrent 真并行扇出。
> 未用 `SequentialWorkflowBuilder` 是因为它硬编码 `AIAgentHostOptions`，无法开启事件处理所需的
> `EmitAgentResponseEvents`。请勿将两者视为等价替换。
> 决策见 [ADR 0007 §2](./adr/0007-maf-integration-boundaries.md)。

**模式不可运行期覆盖**：编排模式是团队定义的固定属性（`TeamRun.EffectiveMode` 持久化
与 TUI 策略切换键已随运行期覆盖一起移除）。改变执行方式的唯一途径是切换团队
（`Shift+Tab` / `/team <name>`）或修改 team.yaml。

## 内置团队

| 团队 | 模式 | 成员 | 场景 |
|---|---|---|---|
| code-review | parallel-dag | security + performance + maintainability | 三视角并行代码审查，聚合去重 |
| research | groupchat | planner + researcher×2 + architect | 项目调研/需求分析/技术选型辩论 |
| impl | magentic-orchestrator | planner + executor×2 + tester | 多文件功能实现：拆解→实现→测试验证 |
