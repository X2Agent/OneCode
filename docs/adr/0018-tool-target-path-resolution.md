# 工具目标路径解析：schema 单一事实源与 fail-closed

**状态**: Accepted
**日期**: 2026-10-29
**关联**: [MAF 集成边界](./0007-maf-integration-boundaries.md)（工具管道中间件顺序）、
[UI 契约单层化](./0017-ui-contract-single-layer.md)（工具调用事件与审批提示共用同一份参数视图）

## 语境

Layer 0 安全不变量与 Layer 1 权限边界都需要回答同一个问题：**这次工具调用要动哪个文件？**
如果各消费者按固定参数名（`filePath` / `file_path` / `path` / `file`）各自扫描调用参数，猜不到就静默放行，
边界保护会出现三处盲区：

- 敏感路径黑名单只对硬编码的读工具集合生效，MCP 工具完全在保护范围外；
- 权限边界先按「工具名是否在写 / 只读集合」筛一遍，非文件类工具直接 `Allow`；
  解析不到路径时路径集合为空，循环不执行，同样 `Allow`；
- 编辑事务与编辑守卫各自再扫一遍参数，拿不到就不建快照、不做存在性检查。

三个消费者各自猜 + 猜不到的语义是放行 = 新增工具与 MCP 工具默认没有边界保护。
`ToolArgumentBindingMiddleware` 只归一化「未命中 schema 的别名」，MCP 工具的 `file_path` 会原样保留，
所以扩参数名白名单无法收敛问题——白名单永远落后于工具生态。

## 决策

1. **路径来源改为工具 JSON Schema**：`ToolTargetPathResolver` 读 `AIFunction.JsonSchema` 推断目标参数，
   分两级——规范键 `filePath`/`file_path`/`path`/`file`（schema 里出现的全部纳入：`Lsp` 同时声明 `file` 与
   `path`，只传其中一个的调用也必须受检），没有任何规范键时取第一个「以 `path` 结尾」的参数名。
   任一路径参数不可用（必填缺失、非字符串、空串）即拒绝整次调用，不让另一个可用的键掩盖它。
   `target`/`directory` 这类通用名词刻意不列为规范键：它们会把「无路径概念」误判成「路径不可用」而误拒。
   工具注册不新增声明参数（`AddToolInstance` 签名不变）。

2. **三态结果而不是 nullable**：`ToolTargetPathResult` 区分 `None`（无路径概念 / 可选路径未提供）、
   `Resolved`（含路径列表）、`Unresolvable`（声明了路径参数但拿不到可用值）。
   用 `null` 无法表达「没有路径」与「解析失败」的相反处理，正是按参数名猜路径这类做法 fail-open 的成因。
   可选路径未提供时返回 `None`，**不**替工具假设「默认就是 cwd」——那是 `Grep`/`Glob`/`LS` 的私有语义。

3. **单一 fail-closed 收口点**：`SafetyInvariantMiddleware`（Layer 0，先于 Layer 1 执行）是唯一把
   `Unresolvable` 转为拒绝的位置，消息含工具名与参数名并给出「改用字面路径」指引。
   各不变量与权限层拿到的是已解析结果，不重复报错。`ISafetyInvariant.CheckAsync` 因此接收
   `InvariantCheckRequest(ToolName, Parameters, TargetPath)`。

4. **保护范围按元数据分层**：`ToolPathScope` 是唯一分类来源（`Write` = `FileEdit|FileWrite|FileDelete`、
   `ReadOnly` = `Risk.ReadOnly`、`Dynamic` = `Risk.Dynamic`、其余 `None`）：
   - `Write` → 写保护段 + 工作区边界；
   - `ReadOnly` 与 `Dynamic` → 敏感读清单 + 工作区边界；
   - `None` → 全部跳过。
   `Dynamic` 刻意不套写保护：MCP 工具已经过审批边界，叠加 `.git`/`.env` 写段会误伤
   「MCP 只读工具读取 `.git`」这类合法调用。写保护的范围只由文件写分类决定。

5. **审批层回落**：`AutoApprovalRulesFactory` 在 MAF 审批评估时拿不到 `AIFunction`，
   `ToolPermissionContext.TargetPath` 为 null，`ValidatePath` 回落到既有的参数名提取；
   执行期的 Layer 0/1 仍是最终防线。

## 后果

- 新增工具只要参数名带 `path` 语义，就自动进入敏感路径与边界保护；MCP 工具不再有保护盲区。
- 参数畸形（必填路径缺失、类型非字符串、空串）从「静默执行」变为「可自愈拒绝」，
  模型能根据错误消息改写调用。
- 已知豁免（均有测试锁定）：
  - **多文件协议工具**：`ApplyWorkspaceEdit` 顶层只有 `workspaceEditJson`，路径嵌在
    `documentChanges[].textDocument.uri`，由工具内部 `PathsHelper.SafeResolve` 保证边界；
    按 `None` 放行并按工具名打一次性告警。
  - **provider 注入工具**：`todos_*` 与 `FileMemoryProvider` 的工具注册为 `Risk.Safe` +
    `Category.PlanAllowed`，落在 `None`，行为与决策前完全一致（它们只读写项目内的 `.onecode` 会话状态，
    其中技能资源目录可能在项目外）。
- 工具若声明了路径语义参数却不可用，会被拒绝——这是有意的严格化；新工具接入时按错误消息修正参数名即可。
- 仍按参数名启发式判断：若某工具用完全不相关的名字传路径（如 `subject`），解析器看不到它，
  该工具会落在 `None`。为此保留「写类工具无路径参数」的一次性告警，让这类声明缺陷在日志里可见。
