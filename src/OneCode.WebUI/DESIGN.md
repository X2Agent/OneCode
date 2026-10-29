# OneCode WebUI Design System

Blazor Server 浏览器客户端的设计规范。本文是 WebUI 的可移植设计系统：token、排版、栅格、组件清单（props / 状态样式）、关键帧与过渡参数、动效性格。实现唯一来源：`wwwroot/app.css`（样式 token）+ `wwwroot/app.js`（主题互操作）+ `Components/`（组件）。

## 设计原则

- **不画边框**：分层靠微对比色阶（相邻层级 1%–3% 亮度差）与阴影，`border` 仅用于 iframe 归零与滚动条透明描边。
- **纯黑仅用于文字**：背景用中性灰阶（浅色画布 `#F5F7FA` / 暗色画布 `#0D0D0D`），纯黑只作文字色。
- **主色 `#0066FF`**：中性灰 `#F5F7FA` / `#E4E7ED` / `#C9CDD2`，适配本地企业与政务系统观感。
- **暗色减重**：暗色模式 `--fw-*` 整体减一档，避免白字发粗刺眼。
- **数字等宽**：计数 / 时间戳 / 统计一律 `tabular-nums`，防刷新跳动。
- **hover 不放大**：交互悬停只加深色阶与阴影，禁用 transform 缩放。

## 主题机制

双主题：浅色（`:root` 基准）+ 暗色（`[data-theme="dark"]` 覆盖）。

- 首帧引导：`App.razor` `<head>` 内联脚本在样式表之前执行，读 `localStorage["onecode-theme"]`，无手动值则查 `prefers-color-scheme`，写入 `html[data-theme]`，杜绝主题闪变（FOUC）。
- 运行时切换：`app.js` 暴露 `window.app.getTheme / setTheme / toggleTheme`；手动切换持久化到 localStorage，系统主题热变化仅在没有手动选择时跟随。
- `color-scheme` 随作用域切换，原生滚动条 / 表单控件自动适配。

## Token（双主题对照）

### 色板

| Token | 浅色 | 暗色 | 用途 |
|---|---|---|---|
| `--accent` | `#0066FF` | `#0066FF` | 主色：用户气泡、主按钮、激活标记 |
| `--accent-strong` | `#0052CC` | `#0052CC` | 主按钮 hover |
| `--accent-text` | `#0052CC` | `#4D94FF` | 强调文字 / 图标（`#0066FF` 直接触底对比不足） |
| `--accent-soft` | `rgba(0,102,255,.10)` | `rgba(0,102,255,.12)` | 审批徽章底 |
| `--bg-base` | `#F5F7FA` | `#0D0D0D` | 画布：转录区背景、内嵌输入底 |
| `--bg-panel` | `#FFFFFF` | `#131313` | 面板：侧栏、顶栏、输入条、代码块、卡片 |
| `--bg-raised` | `#F5F7FA` | `#191919` | 白面板上的芯片：默认按钮、列表 hover、表头徽章 |
| `--bg-overlay` | `#FFFFFF` | `#1F1F1F` | 浮层：中断卡、抽屉、复制按钮 |
| `--bg-hover` | `#E4E7ED` | `#202020` | hover 加深阶（浅色即规范第二灰） |
| `--bg-canvas` | `#FFFFFF` | `#FFFFFF` | 产物 iframe 画布底 |
| `--text-primary` | `#0D0D0D` | `#F5F7FA` | 正文 |
| `--text-secondary` | `#4E5961` | `#C9CDD2` | 次级说明 |
| `--text-muted` | `#86909C` | `#8A9099` | 占位 / 时间戳 |
| `--success` / `--warning` / `--danger` | `#15803D` / `#B45309` / `#DC2626` | `#30C85E` / `#F5A623` / `#F0564E` | 语义色（暗底取亮阶） |
| `--danger-soft` / `--danger-soft-hover` | `rgba(220,38,38,.10)` / `.18` | `rgba(240,86,78,.12)` / `.20` | 停止 / 拒绝按钮底 |
| `--selection-bg` | `rgba(0,102,255,.22)` | `rgba(0,102,255,.35)` | 选区 |

### 排版

| Token | 值 | 用途 |
|---|---|---|
| `--font-sans` | `Inter, "PingFang SC", "Microsoft YaHei", "Segoe UI", system-ui, -apple-system, sans-serif` | Inter 仅本地优先（不下载字体文件） |
| `--font-mono` | `ui-monospace, SFMono-Regular, …` | 代码、数字、路径 |
| `--fs-hero-lg` / `--fs-hero` / `--fs-title` | `32px` / `28px` / `24px` | hero 标题阶（当前用于空态 h1） |
| `--fs-body-lg` | `16px` | 消息正文、顶栏标题 |
| `--fs-body` | `14px` | 界面正文基准，行高 1.5 |
| `--fs-caption` / `--fs-micro` | `12px` / `11px` | 辅助信息 |
| `--fw-bold` / `--fw-semibold` / `--fw-medium` | `700/600/500`（暗色 `600/500/400`） | 字重，暗色自动减一档 |

### 间距 / 圆角 / 阴影

| Token | 值 | 说明 |
|---|---|---|
| `--space-1..6` | `4/8/12/16/20/24px` | 栅格间距落在 16–24px |
| `--radius-sm` / `--radius-lg` / `--radius-full` | `8px` / `12px` / `999px` | 圆角 8–12px |
| `--shadow-1/2/3` | 浅色低 alpha 黑；暗色高 alpha 黑 | 浮起分层，替代描边 |
| `--shadow-accent` | 主色投影 | 主按钮 hover |
| `--focus-ring` | `0 0 0 2px var(--bg-base), 0 0 0 4px var(--ring-accent)` | 焦点双环，不用亮线 |
| `--scrollbar-thumb(-hover)` | 浅 `#C9CDD2`/`#AEB4BD`；暗 `#262626`/`#303030` | 透明描边 + padding-box 裁切 |

## 布局与响应式

三栏 shell：`clamp(280px,20vw,330px) | minmax(0,1fr) | clamp(288px,21vw,340px)`；滚动只发生在 `.session-list` / `.transcript` / `.activity` 内部（各容器自备 `min-height: 0`，`html/body` 锁 `overflow: hidden`）。

| 断点 | 布局 |
|---|---|
| `≤767px`（设计宽度 375） | 单栏；会话栏左抽屉、活动右抽屉，遮罩 `--backdrop` |
| `768–1023px`（平板 768） | 会话栏收 56px 图标栏，活动变浮层抽屉 |
| `1024–1439px` | 三栏紧凑（默认） |
| `≥1440px`（桌面 1440） | 三栏舒适档：消息列 820→860px，侧栏上限 350/360px，留白加大 |

消息区 12 列栅格（`.messages`，列间距 16px）：用户消息块占 `5 / -1`（8/12 列右对齐），助手 / 系统消息整行；移动端用户块 `1 / -1` + `justify-self: end`，气泡最大 88%。

## 组件清单

| 组件 | 文件 | Props / 注入 | 状态样式 |
|---|---|---|---|
| ChatShell | `Components/Pages/Chat.razor` | `ChatSessionClient Client` / `IJSRuntime Js` | `.shell.nav-open` / `.activity-open` 控制抽屉；`transcript.busy` 切换半透明 |
| ThemeToggle | Chat 顶栏内 | — | `.context-toggle.theme-toggle` 常驻；hover `--bg-raised`；暗色显示日、亮色显示月 |
| EmptyHero | Chat 转录区 | — | `.empty-hero`：`h1` 用 `--fs-hero/--fw-bold`，兼作 FocusOnNavigate 落点 |
| SessionList | `Components/Chat/SessionList.razor` | `WorkingDirectory` / `Running` / `Busy` / `Loading` / `Sessions` / `OnSwitch` / `OnNew` / `OnClose` | `.session:hover/active`（`--bg-raised` + 内嵌主色条）；`.dot.live` 呼吸；`.busy` cursor: progress；触屏常显关闭按钮 |
| MessageList | `Components/Chat/MessageList.razor` | `Messages` / `SessionKey` | `.message-block.user` 8 列右对齐；`.streaming` 隐藏操作条；`.msg-copy.copied` 回弹 |
| 消息气泡 | MessageList 内 | — | 用户：`--accent` 底白字 12px 圆角；助手：无气泡平铺 16px；系统：`--fs-caption` + 左侧色条 |
| ToolCallCard | `Components/Chat/ToolCallCard.razor` | `Call` | `.running`（主色圆点）/`.done`（绿）/`.failed`（红）；summary hover `--bg-raised` |
| HtmlArtifactCard | `Components/Chat/HtmlArtifactCard.razor` | `Source` | `.artifact-frame.expanded` 320px→70vh；sandbox 不给 `allow-same-origin` |
| InteractionCard | `Components/Chat/InteractionCard.razor` | `Pending` / `OnRespond` | `.interaction:focus-visible` 双环；徽章 `approval`（主色软底）/`budget`（警示软底） |
| ActivityPanel | `Components/Chat/ActivityPanel.razor` | `Activities` / `ContextItems` | 条目左侧 `--bg-hover` 色条；`+n/-n` 绿/红 |
| Composer | Chat 输入区 | — | textarea `--bg-base` 内嵌底，focus 双环；发送 `button.primary`；停止 `--danger-soft` |
| 按钮 | 全局 | — | hover 只加深 `--bg-hover` + `--shadow-1`；`:focus-visible` 双环；`:disabled` 40% 透明 |

## 关键帧与过渡参数

| 名称 | 参数 | 用途 |
|---|---|---|
| `delta-in` | `blur(5px) opacity .35 → 清晰`，`--dur-stream 120ms` `--ease-out` | 流式尾段块级入场（`@key` 容器重建触发） |
| `ch-in` | `blur(5px) opacity .3 → 清晰`，`--dur-char 90ms`，`backwards`；`animation-delay: calc(var(--i) × 30ms)`，`--i` 上限 16（≤480ms） | 字级错峰（`.stream-tail .ch`） |
| `squash` | `scale(.82) → scale(1.1)@55% → scale(1)`，`--dur-base 180ms` | 复制回弹（≈18% 压缩 + 10% 过冲） |
| `fade-in` | `opacity 0 → 1` + 2px 位移，180ms | 消息 / 遮罩入场 |
| `rise` | `opacity 0 → 1` + 6px 位移，180ms | 中断卡入场 |
| `blink` | step-end 1s 循环 | 流式光标 |
| `pulse` | `opacity 1 → .35 → 1`，1.4s 循环 | 运行中会话圆点 |
| 过渡基准 | `--dur-fast 120ms` / `--dur-base 180ms` / `--ease-out cubic-bezier(.2,.8,.2,1)` | hover、抽屉位移、透明度变化 |

`prefers-reduced-motion: reduce`：`delta-in` / `ch-in` 回落纯淡入（`*-fade`），错峰延迟归零；其余循环动效由用户代理策略接管。

## 动效性格

| 场景 | 性格 | 参数 |
|---|---|---|
| 流式打字 | 极速高斯模糊→清晰，相邻字错开 30ms | 块 120ms；字 90ms + 30ms 错峰（上限 480ms） |
| 复制 / 点赞 | Q 弹回弹 | squash 18% 压缩 + 10% 过冲，180ms；对勾 2500ms 自动复原 |
| 拖拽（未来组件） | 瞬间到位，无弹性拖尾 | 落点直接 snap，禁用 overshoot |
| hover | 色阶 / 阴影加深 | 120ms，禁用缩放 |
| 焦点 | 双环扩散 | 内环贴局部底色 + 外环主色，无亮线描边 |
| 抽屉 / 浮层 | 滑入 | transform 180ms `--ease-out` |

## 流式渲染机制

尾段由 `MessageList.SplitStreamingTail` 拆出（拆分点避开未闭合代码围栏）：

1. **纯文本快速路径**（`StreamingTextTokenizer.IsPlainText`）：无 Markdown 触发符时按字（CJK）/词（拉丁）拆 span，`StreamingTailState` 以绝对字符偏移为 `@key` 分配错峰索引——Blazor 跨渲染复用 DOM，动画不被打断；600 字符窗口外的旧 span 收敛为纯文本。
2. **整段 Markdown 路径**：含语法尾段走 `MarkdownRenderer`（Markdig + 白名单消毒），容器随 `@key` 重建触发块级入场。
3. 消息完结后整条重渲染，span 结构自然消失。**禁止 JS 向流式节点注入 DOM**（Blazor diff 会与注入结构冲突）；`app.js` 的复制按钮注入只作用于非流式消息。
