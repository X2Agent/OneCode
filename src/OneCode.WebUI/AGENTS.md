# OneCode.WebUI

Blazor Server 浏览器客户端：`OneCode.App` 的进程内 QueryEvent 消费端，与 OneCode.Tui 对等（ADR-0017）。

## 硬约束

- **仅绑定 loopback**（默认 `http://127.0.0.1:5080`），不得对外暴露任何端点。
- **不经 CDN、不依赖外网**：静态资源只允许 wwwroot 本地自托管；禁止引入第三方 js/css/字体。
- **原始 HTML 必须消毒**：模型输出按不可信文本处理。Markdig 允许 HTML 后，输出一律过 `MarkdownRenderer` 的 `HtmlSanitizer` 白名单（只留排版标签与排版类 CSS；脚本、事件属性、`javascript:`、表单控件、iframe 全部剥离；`class`/`id` 不放行以免命中宿主样式）。**整页 HTML 不得进主文档**：`HtmlArtifactSplitter` 抽出的产物只能由 `HtmlArtifactCard` 的 sandbox iframe 承载（`allow-scripts`，绝不给 `allow-same-origin`）。外链图片放行（https/http 直连，离线裂图由 alt 兜底）。
- **依赖方向**：只依赖 `OneCode.App`（经组合根 `AddOneCodeCore`），**禁止引用 OneCode.Tui**；两前端唯一共享物是 App 层契约类型，样式与交互独立演进。
- **契约新增**：QueryEvent 新增子类型时必须同时补 TUI/WebUI 两处渲染并更新 `QueryEventContractTests` 基线，不得静默丢弃。

## 结构约定

- 样式唯一入口 `wwwroot/app.css`：设计 token 全部在 `:root`（单暗色主题，无描框，微对比色阶分层）；新增颜色/间距/圆角/动效一律加 token，禁止散落硬编码。
- 静态资源引用一律走 `@Assets["..."]` + `app.MapStaticAssets()`（构建期内容指纹）：裸路径没有指纹，改完文件浏览器会继续跑旧缓存。`app.js` 必须排在 `blazor.web.js` 之前加载——电路建立后立即会调用 `window.app.*` 互操作。
- `wwwroot/app.js` 是唯一 JS 互操作文件，零第三方依赖；Blazor 组件经 `window.app.*` 调用（copyText / fitComposer / bindSessionScroll）；代码块复制按钮由 JS 内部 MutationObserver 注入，不经互操作入口。
- 对话流渲染：流式消息按「已完结块 + 尾段」拆分（`MessageList.SplitStreamingTail`），尾段承载入场动效；拆分点不得落在未闭合代码围栏内。
- 滚动归属：`.shell` 是单行 `minmax(0, 1fr)` 网格且 `html/body` 锁 `overflow: hidden`，滚动只发生在三栏内部容器（`.session-list` / `.transcript` / `.activity`）。新增滚动容器必须自备 `min-height: 0`（flex 项默认 `min-height: auto` 会被内容撑高，退回全页共用一条滚动条）。
- 增量渲染：会话列表首屏只渲染 `SessionList.PageSize` 条，触底由 `app.js` 的滚动委托回调 `LoadMore` 逐屏展开（无翻页控件）；列表未撑出滚动条时（高视口）由 `fillSessionList` 自动补屏，否则触底回调永远等不到。转录只渲染最近 `MessageList.PageSize` 条，更早的由顶部按钮展开。
