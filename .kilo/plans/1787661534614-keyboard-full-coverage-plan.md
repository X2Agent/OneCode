# 键盘全覆盖增强计划（合并版）

> 目标：所有操作均可通过键盘完成；模式支持 Tab+1..4 直达；新增可选的上下文快捷键提示条；快捷键面板沿用 `/keybindings` 既有入口，不新增键位。
> 本计划合并并延续 `docs/plan/keyboard-first-refactor-plan.md`（该计划尚未实施，G1-G9 缺口全部有效）；Phase C/D 复用其方案细节，不重复展开。
> 创建日期：2026-08-25。范围经用户确认：四部分合为一份计划，分阶段交付。
> 2026-08-25 复审：Tab+N 误吞边界维持 1000ms 现状；提示条默认开启确认；取消原稿提议的 F1/空输入 `?` 面板入口（`/keybindings` 已覆盖，用户裁定）。

## 已核实的关键事实（实施前提）

1. **和弦机制无侵入**：Tab 按下 → `KeybindingResolver.ResolveInternal`（Core/Keybindings/KeybindingResolver.cs:94-101）因存在更长和弦前缀返回 `ChordStarted`；`TuiKeyAdapter.ResolveAction` 仅在 `Match` 时返回 action（TuiKeyAdapter.cs:61），故 `ChatInputView.OnInputKeyPress` 中 earlyAction 为 null、硬编码 Tab 分支照常执行「空输入切模式」（Keys.cs:140-159）；数字键 1s 内到达则精确匹配 `tab N` 返回 action。**不需要改 Core 解析语义。**
2. **帮助面板已有入口（用户裁定不新增键位）**：`KeybindingsOverlay` 由 `OneCodeToplevel.Commands.cs:114-136 TryHandleKeybindingsListOverlay` 驱动，裸 `/keybindings` 与 `/keybindings list` 均弹 overlay（数据源 `_ctx.KeyResolver.Bindings` + `GetKeybindingWarnings`）。F1 与空输入 `?` 均为原稿提议、代码中从未存在，经复审取消。
3. **事件接线先例**：模式直达参照 `CycleModeRequested += () => _modeController.CycleMode()`（ChatInputView.Layout.cs:42）在视图内部接线即可，无需跨层订阅。
4. **设置管道现成**：读值 `settings.Get(key, default)`（参照 `showThinking`）；保存链路 `SettingsResult` → `BuildSettingsPatch`（TuiHostConfigurator.cs:468）→ `configManager.ApplyAsync` → 保存成功后 `app.Invoke(toplevel.UpdateRuntimeState(...))`（L454-456）即时生效。注意现状：`thinkingEnabled`/`showThinking` 键名在 BuildSettingsPatch 中是内联字符串先例。
5. **布局零增量方案**：输入框与 SessionContextBar 之间已有 1 行空白 `TuiSpacing.ChatInputContextGap`（TuiSpacing.cs:86）。提示条启用时占用该行（`Y = Pos.AnchorEnd(2), Height = 1`），关闭时还原空行——`ContentZoneReservedBottom` 与 `_chatInput.BottomOffset` 数学完全不动，布局永不位移。

## 设计决策（全部已定）

| 决策 | 结论 | 理由 |
|------|------|------|
| Tab+N 形态 | 和弦序列 `tab 1..4`（按 Tab 松开、1s 内按数字） | 终端无法检测按住 Tab+数字，物理限制 |
| 模式动作 | `app:modeBuild/Plan/Team/Goal`，注册于 **Chat 上下文** | 与 `app:exit` 命名一致；Tab 仅在输入框聚焦时可达 |
| busy 态直达 | 允许（与现有 Tab 循环行为一致） | 一致性优先 |
| 提示条开关 | `showKeyHints` 默认 **true**（用户已确认），`/config` CheckBox 可关 | 解决发现性痛点；关闭后零占位 |
| 提示条位置 | 复用 `ChatInputContextGap` 空白行 | 布局零数学变更 |
| 提示条数据源 | 策展动作 id 列表 + 经 Resolver 反查按键标签；查不到绑定则整段隐去 | 用户重映射/解绑后提示自动跟随 |
| Tab+N 误吞边界 | 维持全局和弦窗口 1000ms，文档明示（2026-08-25 复审用户确认） | 与 `ctrl+x ctrl+k` 同语义；场景罕见 |
| 面板入口 | **不新增 F1/?**；沿用 `/keybindings`（已验证） | 用户裁定：已有入口足够 |
| 设置键常量 | 新增 `Core.Constants.ConfigKeys.ShowKeyHints`（使用点 ≥3：SettingsOverlay/BuildSettingsPatch/启动初值） | AGENTS.md §10.2 多处共享必须常量 |

### 已知边界（文档明示，非缺陷）

- Tab 循环后 1s 内输入 `1-4` 被解释为直达模式（和弦窗口内，预期行为）；
- 补全激活时不直达（分发侧守卫），数字正常入文；
- **交互会话挂起/提问模式不直达**（权限选择器期间数字交给选择器——挂起分支在 Keys.cs:59-68 先于动作分支返回，守卫作纵深防御；提问模式下数字须落入答案文本，守卫必需）；
- 其他按键 1s 内正常取消和弦（现有语义）。

---

## Phase A：模式直达 + 欢迎页

1. `KeybindingDefaults.cs`：
   - 动作常量 `ActionAppModeBuild/Plan/Team/Goal`，加入 `AllActions`；
   - Chat 默认绑定追加 `"tab 1".."tab 4"`；
   - 验证 `KeybindingSchema` 枚举随 AllActions 再生成。
2. `ChatInputView.Keys.cs` 分发（置于现有 cycleTeam/cyclePlanPanel 分支附近）：
   - `app:mode*` → 内部接 `_modeController.Mode = ...`（参照 Layout.cs:42 的 CycleModeRequested 接线，复用 ModeChanged 徽章闪烁）；守卫 `!_completion.IsCompletionActive && !_interactionSuspended && !_isQuestionMode`，被拦下时**不置 Handled**（数字入文）。
3. `WelcomeRenderer.Render`：tips 扩两行，补 `Tab+1..4 直达模式`、`Ctrl+G 计划栏`、`/keybindings 快捷键面板`。
4. `KeybindingsOverlay.FormatRows`：「硬编码」清单与新模式动作分组随实际绑定自动呈现，核对无需手工同步项。
5. 测试：Resolver 和弦三态（匹配/超时回退/非数字取消）、分发守卫四场景（正常/补全/挂起/提问）。
6. 文档：`docs/keybindings.md` 新动作表、和弦 `tab N` 说明。

## Phase B：上下文提示条（showKeyHints）

1. 设置全链路：`ConfigKeys.ShowKeyHints` 常量 → `SettingsOverlay` CheckBox（放「任务完成通知」同排）→ `SettingsResult` 新字段 → `BuildSettingsPatch` 映射（`AddIfChanged(changes, ConfigKeys.ShowKeyHints, effective.Get(...), result.ShowKeyHints)`）。
2. 新建 `OneCode.App/Tui/KeyHintsBar.cs`（View，≤1 行）：
   - `SetEnabled(bool)`：关闭时不渲染（该行还原空白间隙）；
   - 内容段 `{actionId, 描述}` 有序列表，标签经 Resolver 反查当前绑定键名；
   - Chat 空闲：`Tab 切模式 · Tab+1..4 直达 · Shift+Tab 换团队 · Ctrl+G 计划栏 · /keybindings 全部快捷键`；busy 时前置 `Esc 中断`；宽度不足自尾部截段；
   - overlay 可见时隐藏（跟随 `_overlayHost.IsOverlayVisible`），避免居中浮层下残留主视图提示误导；
   - 颜色走 `TuiTheme`/`TuiPalette`。
3. 接线：
   - 初值：ReplShell 构造参数 `bool showKeyHints = true`（构造点 OneCodeToplevel.cs:78 附近传入 configManager 快照值）；
   - 热生效：`applySettingsAsync` 保存成功后追加 `_shell.SetKeyHintsEnabled(effective.Get(ConfigKeys.ShowKeyHints, true))`；
   - 位置：`Y = Pos.AnchorEnd(2)`，占用 gap 行。
4. 测试：格式化（标签反查/截断/禁用/overlay 隐藏）、设置往返（SettingsResult→patch→快照回读）。

## Phase C：Transcript 对话区导航（= keyboard-first Phase 0+1）

按原计划 §2.2/§3 Phase 1 执行：`Transcript` 上下文 + `transcript:*` 动作、`App/Transcript/TranscriptViewModel.cs`（游标跳行纯 C#）、`MessageListView` 高亮与 `ToggleAt/CopyCodeAt`、Ctrl+T 进入/Esc/i 退出、流式禁入、AgentStatusBar 导航指示。本计划对接差异：

- `KeyHintsBar` 增加 Transcript 态内容：`J/K 跳转 · Enter 展开 · C 复制 · G 顶底 · Esc 返回`；
- Ctrl+T 成为 Transcript 默认键后，修 `docs/keybindings.md` L255 示例（`ctrl+t → command:compact` 改用其他示例键）。

## Phase D：硬编码迁移与审计收尾（= keyboard-first Phase 2+3）

按原计划 §3 Phase 2/3 执行：`autocomplete:accept/dismiss`、`diff:*`、`selector:*` 迁移 Resolver；次级视图按键审计；文档「保留行为」章节；DESIGN.md 增补「每个鼠标行为必须有键盘等价动作」。

**Tab 冲突裁决（已定结论）**：补全激活时 Enter 接受建议依赖 Chat 上下文的 `chat:submit`（Keys.cs:90 分支），故「补全期间 pop Chat 上下文」不可行；迁移 `tab → autocomplete:accept` 必须引入 **「精确匹配立即触发且和弦继续监听」** 的解析策略（仅当单键自身有绑定时生效，`ctrl+x ctrl+k` 类无绑定的前缀不受影响），Core 层小改 + 单测覆盖。

---

## 涉及文件一览

| 文件 | 变更 |
|------|------|
| `src/OneCode.Core/Keybindings/KeybindingDefaults.cs` | 新动作/默认绑定（A） |
| `src/OneCode.Core/Constants.cs` | `ConfigKeys.ShowKeyHints`（B） |
| `src/OneCode.App/Tui/ChatInputView.Keys.cs` | mode* 分发、守卫（A） |
| `src/OneCode.App/Tui/ChatInputView.Layout.cs` | mode* 内部接线（A） |
| `src/OneCode.App/Tui/ReplShell.cs` / `ReplShell.Keyboard.cs` | 提示条挂载、SetKeyHintsEnabled（B/C） |
| `src/OneCode.App/Tui/WelcomeRenderer.cs` | 两行 tips（A） |
| `src/OneCode.App/Tui/KeyHintsBar.cs` | 新建（B） |
| `src/OneCode.App/Tui/SettingsOverlay.cs`、`Services/TuiHostConfigurator.cs` | `showKeyHints` 全链路（B） |
| `src/OneCode.Core/Keybindings/KeybindingResolver.cs` | eager-fire 策略（仅 D） |
| `MessageListView*.cs`、`App/Transcript/TranscriptViewModel.cs`、`AgentStatusBar.cs`、`DiffView.cs`、`InlineSelector.cs` | C/D（见原计划文件清单） |
| `docs/keybindings.md`、`docs/plan/keyboard-first-refactor-plan.md`（标记进度）、`src/OneCode.App/Tui/DESIGN.md` | 文档同步 |

## 验收清单（原计划 §5 十项之外追加）

11. 空输入 `Tab` 循环不变；`Tab` 后 1s 内 `1/2/3/4` 直达对应模式，状态栏徽章闪亮；busy 态同样可直达
12. 补全激活时 `Tab` 后按数字：数字入文、模式不变
13. 权限选择器挂起时按 `1-4`：交给选择器；提问向导短文本题输入数字正常入文
14. 裸 `/keybindings` 打开快捷键面板、Esc 关闭（回归确认既有行为，无新键位）
15. `/config` 关 `showKeyHints` 保存后提示行立即消失且还原为空行（总高度不变）；重开恢复
16. `keybindings.json` 把 `ctrl+g` 改绑他键后提示条该段自动更新或隐去
17. `dotnet build src/OneCode.slnx` 无新增警告；`dotnet test src/OneCode.slnx` 全部通过

## 风险与对策

| 风险 | 对策 |
|------|------|
| 数字误吞（Tab 后 1s 内打数字开头的文本） | 窗口即全局既有和弦超时 1000ms；文档明示；切模式后紧接数字开头输入的场景罕见 |
| Phase D tab 单键绑定与 `tab N` 和弦前缀冲突 | 已定：引入 eager-fire 策略（见 Phase D），Core 小改 + 回归测试保证 `ctrl+x ctrl+k` 类和弦不回归 |
| 提示条挤压窄终端 | 自尾部截段保关键项；整条可关 |
