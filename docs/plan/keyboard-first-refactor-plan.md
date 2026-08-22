# OneCode TUI 无鼠标化重构计划

> 目标：所有交互动作均可通过键盘完成，鼠标仅作为辅助（滚轮、点击快捷方式）。
> 状态：计划已确认，待实施。创建日期：2026-08-19。2026-08-19 复审：行号引用已按当前代码核准；ChatTranscriptView 确认为 MessageListView 的组合包装层（非双轨），方案落点不变。
> 关联文档：`docs/web-host-plan.md`（§3.6 双宿主共享设计——本计划的 Transcript 游标导航将作用于 App 层共享 `TranscriptViewModel`；keybindings.json 双宿主共用，Phase 4 时 Web 端接入同一 `KeybindingResolver`）。

## 1. 背景与现状评估

### 1.1 已有的良好基础

- **快捷键体系完备**：`KeybindingResolver` 支持上下文（Global/Chat/Autocomplete）、和弦序列、用户覆盖（`~/.onecode/keybindings.json`）、热重载、JSON Schema 校验。源头定义在 `src/OneCode.Core/Keybindings/KeybindingDefaults.cs`。
- **大部分 UI 已键盘驱动**：
  - Overlay（Review/Settings/Resume/DiffDetail）均支持 ↑↓/Enter/Esc，由 `OverlayHost` 统一管理。
  - `InlineSelector`（权限提示、Plan 审批）支持 ↑↓/Enter/Esc。
  - `DiffView` 支持 ↑↓/PgUp/PgDn/Home/End 及 J/K。
  - 表单类 `FormOverlay`/`SettingsOverlay` 支持 Tab 切换、Ctrl+S 保存。
  - 对话区滚动有 Shift+↑↓/Ctrl+PgUp/PgDn/PgUp/PgDn 键盘路径。

### 1.2 缺口清单（鼠标独占/硬编码）

| # | 缺口 | 位置 | 现状 |
|---|------|------|------|
| G1 | 工具行展开/折叠 | `MessageListView.cs` `OnMouseEvent`（L309-356，点击分发在 L324-353） | 仅鼠标左键点击 |
| G2 | 思考块展开/折叠 | 同上（L342-346） | 仅鼠标点击 |
| G3 | 错误块展开/折叠 | 同上（L347-351） | 仅鼠标点击 |
| G4 | 代码块复制 | `CodeBlockCopyTag`（点击代码块头行复制） | 仅鼠标点击 |
| G5 | 硬编码按键绕过 Resolver | `ChatInputView.Keys.cs`（Tab 补全/模式循环 L140-158、Ctrl+←→ 切换建议 L181-192） | 无法通过 keybindings.json 重映射 |
| G6 | DiffView 按键硬编码 | `DiffView.cs` `OnKeyDown`（L101-118） | 绕过 Resolver |
| G7 | InlineSelector 按键硬编码 | `InlineSelector.cs` `HandleKey`（L39） | 绕过 Resolver |
| G8 | Overlay Esc 硬编码 | `OverlayHost.cs` `OnKeyDown`（L213-219） | 绕过 Resolver（可保留，关闭必须永远可用） |
| G9 | 输入框失去焦点后无法用键盘回到对话区导航 | 焦点模型 | 无 Transcript 导航上下文 |
| G10 | 次级视图 OnKeyDown 硬编码（审查中发现的补充项） | `SettingsOverlay.cs` L198、`ResumeChooserOverlay.cs` L61、`ResultOverlay.cs` L70 | Phase 3 检查其键盘完整性即可，暂不迁移 Resolver |

## 2. 总体设计

### 2.1 设计原则

1. **键盘优先，鼠标保留**：每个鼠标行为必须有键盘等价动作；鼠标行为降级为「快捷方式」而非唯一入口。
2. **一切走 Resolver**：可配置按键一律注册为 `KeybindingDefaults` 默认绑定，硬编码仅保留「必须永远生效」的兜底键（Esc 关闭 Overlay）。
3. **渐进交付**：每个 Phase 独立可发布、可测试，避免大爆炸重构。

### 2.2 新增 Transcript 导航模式（核心方案）

解决 G1-G4、G9：新增 `Transcript` 上下文与「对话区导航模式」：

- **进入**：在 Chat 上下文按 `Ctrl+T`（默认，可配置）→ 焦点从输入框切换到 `MessageListView`，`KeybindingContextManager` push `Transcript` 上下文。
- **导航**：`j`/`↓`、`k`/`↑` 在**可交互行**（ToolLineTag/ThinkingLineTag/ErrorLineTag/CodeBlockCopyTag）之间跳过普通行移动，选中行高亮（Accent 色）并自动滚动到可见区。
- **动作**：`Enter`/`Space` 展开折叠（复用 `ToggleToolExpansion`/`ToggleThinkingExpansion`/`ToggleErrorExpansion`）；`c` 复制代码块；`g`/`G` 跳顶/底；`PgUp`/`PgDn`/`Home`/`End` 滚动。
- **退出**：`Esc` 或 `i`（Vim 风格）→ 焦点回到 `ChatInputView.FocusInput()`，pop `Transcript` 上下文。
- **状态提示**：`AgentStatusBar` 左侧显示 `导航模式` 标记，避免用户迷失；欢迎屏提示语补一句 `Ctrl+T 浏览对话`。

### 2.3 新增上下文与动作常量

`KeybindingDefaults.cs` 新增：

```
上下文：
  ContextTranscript = "Transcript"   // 对话区导航模式激活时

动作：
  chat:enterTranscript               // 进入对话区导航（Chat 上下文）
  transcript:exit                    // 退出导航模式（Transcript 上下文）
  transcript:next                    // 下一个可交互行
  transcript:previous                // 上一个可交互行
  transcript:toggle                  // 展开/折叠当前块
  transcript:copyCode                // 复制当前代码块
  transcript:scrollUp / scrollDown / pageUp / pageDown / top / bottom
  diff:scrollUp / scrollDown / pageUp / pageDown / top / bottom
  selector:previous / next / confirm / dismiss
  autocomplete:accept / dismiss      // 迁移 G5 的 Tab/Esc
```

默认绑定（均可被用户覆盖）：

```
Chat:      ctrl+t → chat:enterTranscript
Transcript: esc → transcript:exit, i → transcript:exit,
           j/down → transcript:next, k/up → transcript:previous,
           enter/space → transcript:toggle, c → transcript:copyCode,
           pageup/pagedown/home/end → 滚动
Diff:      j/k/↑↓/PgUp/PgDn/Home/End → diff:*
Selector:  ↑↓/Enter/Esc → selector:*
Autocomplete: tab → autocomplete:accept, escape → autocomplete:dismiss
```

注意：Esc 关闭 Overlay/补全保留硬编码兜底（在 Resolver 之前处理），文档中标注为「保留行为」。

宿主感知默认绑定：Ctrl+T 在浏览器是保留键（新标签页），因此 `KeybindingDefaults` 提供**按宿主区分的默认集**（TUI：ctrl+t；Web：alt+t），用户 `keybindings.json` 覆盖优先于任何宿主默认；`KeybindingValidator` 校验时结合当前宿主的保留键集给出告警。详见 web-host-plan §3.6。

## 3. 分阶段实施计划

### Phase 0：准备与基线（0.5 天）

- [ ] 全局搜索 `OnMouseEvent`，把 1.2 缺口清单核对一遍，补充遗漏项
- [ ] 为现有按键行为补齐单元测试基线（`OneCode.Tests`），确保重构不破坏现有绑定
- [ ] 确认 `KeybindingValidator` 对新上下文/动作的校验路径

### Phase 1：Transcript 导航模式（2-3 天，核心）

- [ ] `KeybindingDefaults.cs`：新增 `Transcript` 上下文、上述动作常量与默认绑定；更新 `AllContexts`/`AllActions`/`ContextDescriptions`
- [ ] 新建 `OneCode.App/Transcript/TranscriptViewModel.cs`：游标状态**直接落在 App 层共享模型**（见 `docs/web-host-plan.md` §3.6），不在 `MessageListView` 内引入私有字段——避免先实现再上提的二次搬家。职责最小化：可交互行标识列表、展开状态、游标位置与 `MoveCursor(delta)` 跳行逻辑（扫描带 Tag 的行），纯 C# 可单测；Web 端后续直接复用同一模型。
- [ ] `MessageListView`：消费 `TranscriptViewModel` 游标状态，渲染选中行高亮（`MessageListView.Rendering.cs`）
- [ ] 将 `ToggleToolExpansion`/`ToggleThinkingExpansion`/`ToggleErrorExpansion` 从 private 提为 internal/public 或通过新公开方法 `ToggleAt(int lineIdx)` 暴露
- [ ] 新增 `MessageListView.CopyCodeAt(int lineIdx)`：命中 `CodeBlockCopyTag` 时调用 `_clipboard.TryCopyTextAsync`
- [ ] `ChatInputView.Keys.cs` / `ReplShell.Keyboard.cs`：接入 `chat:enterTranscript`（焦点切换 + push 上下文）与 `transcript:*` 动作分发
- [ ] `AgentStatusBar`：导航模式指示
- [ ] `WelcomeRenderer`：更新提示行
- [ ] 单元测试：游标跳行、展开折叠、复制、进出模式焦点管理

### Phase 2：硬编码按键迁移到 Resolver（1-2 天）

- [ ] `ChatInputView.Keys.cs`：Tab（补全接受/循环）、Ctrl+←→（切换建议）改为先查 Resolver 的 `autocomplete:accept` 等，未匹配再走现有逻辑
- [ ] `DiffView.OnKeyDown`：改为解析 `diff:*` 动作（在 DiffDetailOverlay/ReviewOverlay 所在的上下文中 push `Diff` 上下文）
- [ ] `InlineSelector.HandleKey`：改为 `selector:*` 动作分发（保持 `HandleKey` 签名，内部走 Resolver）
- [ ] `KeybindingSchema.cs`：重新生成 JSON Schema（新上下文/动作枚举自动进入）
- [ ] 回归：所有 Overlay 键盘操作、补全、审批流不回归

### Phase 3：审计收尾与文档（1 天）

- [ ] 逐个核对 1.2 清表中所有项已有键盘等价动作
- [ ] 检查 `QuestionWizard`、`ResumeChooserOverlay`、`FormOverlay` 等次级视图按键完整性（Tab 循环、Enter 确认、Esc 取消）
- [ ] 更新 `docs/keybindings.md`：新上下文、新动作、Transcript 模式说明、「保留行为」章节
- [ ] 更新 `src/OneCode.App/Tui/DESIGN.md`：Do's 中加入「每个鼠标行为必须有键盘等价动作」
- [ ] 端到端冒烟：`scripts/smoke-test-release.ps1` + 手工键盘走查清单（见 §5）

## 4. 涉及文件一览

| 文件 | 变更类型 |
|------|----------|
| `src/OneCode.Core/Keybindings/KeybindingDefaults.cs` | 新增上下文/动作/绑定 |
| `src/OneCode.Core/Keybindings/KeybindingSchema.cs` | Schema 枚举更新 |
| `src/OneCode.App/Tui/MessageListView.cs` | 游标状态、键盘导航、公开 Toggle/Copy |
| `src/OneCode.App/Tui/MessageListView.Rendering.cs` | 选中行高亮 |
| `src/OneCode.App/Tui/MessageListView.Expansion.cs` | 可见性调整（private→internal） |
| `src/OneCode.App/Tui/ChatInputView.Keys.cs` | enterTranscript 分发 + G5 迁移 |
| `src/OneCode.App/Tui/ReplShell.Keyboard.cs` | Transcript 上下文动作分发 |
| `src/OneCode.App/Tui/DiffView.cs` | 按键迁移 Resolver |
| `src/OneCode.App/Tui/InlineSelector.cs` | 按键迁移 Resolver |
| `src/OneCode.App/Tui/AgentStatusBar.cs` | 导航模式指示 |
| `src/OneCode.App/Tui/WelcomeRenderer.cs` | 提示语更新 |
| `docs/keybindings.md` | 文档更新 |
| `src/OneCode.App/Tui/DESIGN.md` | 设计原则补充 |
| `src/OneCode.Tests/**` | 单元测试 |
| `src/OneCode.App/Transcript/TranscriptViewModel.cs` | 本 Phase 1 即新建（共享视图模型，游标导航/展开状态落点，Web 端后续直接复用，详见 web-host-plan §3.6） |

## 5. 验收标准（键盘走查清单）

不碰鼠标完成以下全部操作：

1. 输入消息、历史↑↓、换行（Alt+Enter）、粘贴（Ctrl+V）、提交（Enter）
2. Ctrl+T 进入对话区 → j/k 跳转 → 展开/折叠工具行、思考块、错误块 → c 复制代码块 → Esc 返回输入框
3. Shift+↑↓ / PgUp/PgDn 滚动对话区；/find 搜索并跳转
4. /diff 打开审查 → ↑↓ 选文件 → Enter 看详情 → J/K 滚动 → Esc 两层关闭
5. /config 打开设置 → Tab 遍历控件 → 修改 → Ctrl+S 保存 → Esc 取消
6. /session 恢复会话选择
7. 权限提示/Plan 审批 InlineSelector：↑↓ 选择、Enter 确认、Esc 取消
8. Tab 补全循环、Esc 关闭补全
9. `keybindings.json` 覆盖新动作（如把 ctrl+t 改为 alt+t）后热重载生效
10. Ctrl+D 退出

## 6. 风险与对策

| 风险 | 对策 |
|------|------|
| Transcript 模式与流式输出冲突（新行插入导致游标漂移） | 游标按「可交互行的稳定标识」定位而非绝对行号；流式期间暂禁导航进入 |
| Esc 语义过载（关补全/关 Overlay/退导航/中断） | 维持现有优先级链：补全 > Overlay > 导航 > 中断；文档明示 |
| 单字母键 c/i/j/k 与未来文本输入冲突 | 仅在 Transcript 上下文生效，Chat 上下文不受影响 |
| Resolver 迁移改变现有用户按键行为 | 默认绑定与现硬编码行为完全一致，保证零行为变化迁移 |