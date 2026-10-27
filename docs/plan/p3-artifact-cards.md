# P3 · 产物卡 + 本机软件调起 + 图片内联

> 状态：待实施。
> 依赖：无（引擎与前端解耦已落地；与 P2 系列无依赖关系，可并行推进）。
> 关联：[docs/adr/0013-ui-surface-strategy.md](../adr/0013-ui-surface-strategy.md)（TUI 是调度器、本机软件
> 是渲染器的原则；HTML/富媒体内嵌渲染不做的裁决）。

## 1. 背景与动机

agent 产出文件（报告 html、图片、xlsx、notebook……）后，TUI 目前只能以文件路径文本呈现，用户要自己
切窗口打开。原则是「TUI 只做摘要与入口，渲染交给系统里最专业的软件」：产物注册为一行卡片，一键交给
系统默认应用 / 编辑器 / diff 工具，全程零鼠标。

## 2. 目标

- 工具产出或对话提到的文件注册为**产物事件**，TUI 渲染一行产物卡（类型徽标 + 文件名 + 大小 + 状态）。
- `o` 打开（系统默认应用）、`d` diff（`code --diff` 或用户配置的 diff 工具）、`e` 跳转
  （`code path:line` 或 `$EDITOR`）。
- 任务结束自动预览：可配置三档（关 / 询问 / 自动打开）。
- 图片能力检测：支持的终端内联缩略图，否则降级为 `o` 打开。

## 3. 非目标

- **HTML/CSS 内嵌渲染**：明确不做（[ADR-0013](../adr/0013-ui-surface-strategy.md) 裁决）。
- **Kestrel 纯静态文件 + livereload 预览小服务**：可选加项，出现真实需求另行立项，不进本计划默认
  范围；且它不是 WebUI 的例外通道，不得在其上长出界面。
- 产物卡的进程外持久化（产物注册随会话转录走，不建独立产物库）。

## 4. 任务分解

### 批 A · 产物事件与契约

1. `QueryEvent` 新增产物子类型（路径 + 类型 + 来源工具 + 状态；命名随实施定）。
2. 注册时机：写文件类工具（Write/Edit/Bash 重定向产物等）完成时与对话中识别的产出文件路径。
3. 契约同步义务：更新 `src/OneCode.Tests/QueryEventContractTests.cs` 基线，TUI 与 WebUI 两处渲染
   （未知事件告警兜底已存在，不静默丢弃）。
- **验证**：基线测试绿；两前端对未知旧版本事件仍走兜底告警。

### 批 B · TUI 产物卡与快捷键

4. 产物卡渲染：`ChatTranscriptView` 一行卡片（徽标 + 文件名 + 大小 + 状态），不渲染内容本身。
5. 焦点态按键：`o` 打开 / `d` diff / `e` 跳转；快捷键默认绑定入
   `src/OneCode.Core/Keybindings/KeybindingDefaults.cs` 并同步 [docs/keybindings.md](../keybindings.md)。
6. 三平台打开 helper：一个静态类分派 `Process.Start(UseShellExecute=true)` / `open` / `xdg-open`
   （先例：`src/OneCode.App/Commands/KeybindingsCommand.cs`）；diff 与跳转工具读用户配置，默认
   `code --diff` / `code path:line`，回落 `$EDITOR`。
- **验证**：html/pdf/图片/xlsx 四类产物 `o` 均交系统默认应用；`d` 在配置的 diff 工具中打开；
  未安装 `code` 时回落生效。

### 批 C · 自动预览

7. 设置项三档：关 / 询问 / 任务结束自动打开（默认关）；agent 产出 `index.html` 类交付物 → 任务完成
   toast（P1 通知设施）+ 按档位自动弹浏览器。
- **验证**：三档行为各一次；询问档在 TUI 内确认而非系统弹窗。

### 批 D · 图片内联（能力检测，不做则降级）

8. 检测 `TERM_PROGRAM` / `WT_SESSION` / kitty 图形协议支持；支持则 Sixel / kitty 协议内联缩略图，
   不支持则产物卡 `o` 打开。
- **验证**：Windows Terminal 内联显示缩略图；VS Code 内嵌终端自动降级为 `o` 打开。

## 5. 验收

1. agent 产出 `report.html` 与图片 → 会话尾部出现产物卡 → `o` 打开浏览器/照片查看器，diff 卡 `d`
   在 VS Code 看对比，`e` 跳到编辑器对应位置，全程零鼠标。
2. 自动预览「任务结束自动打开」档：交付物完成即弹系统应用。
3. `dotnet build` 零新增警告 + `dotnet test` 全绿（含契约基线更新）。
