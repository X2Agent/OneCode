# OneCode.WebUI

Blazor Server 浏览器客户端：`OneCode.App` 的进程内 QueryEvent 消费端，与 OneCode.Tui 对等（ADR-0017）。

## 硬约束

- **仅绑定 loopback**（默认 `http://127.0.0.1:5080`），不得对外暴露任何端点。
- **不经 CDN、不依赖外网**：静态资源只允许 wwwroot 本地自托管；禁止引入第三方 js/css/字体。
- **Markdown 禁用原始 HTML**：`MarkdownRenderer` 的 `DisableHtml()` 管道不可放开——模型输出按不可信文本处理。
- **依赖方向**：只依赖 `OneCode.App`（经组合根 `AddOneCodeCore`），**禁止引用 OneCode.Tui**；两前端唯一共享物是 App 层契约类型，样式与交互独立演进。
- **契约新增**：QueryEvent 新增子类型时必须同时补 TUI/WebUI 两处渲染并更新 `QueryEventContractTests` 基线，不得静默丢弃。

## 结构约定

- 样式唯一入口 `wwwroot/app.css`：设计 token 全部在 `:root`（单暗色主题，无描框，微对比色阶分层）；新增颜色/间距/圆角/动效一律加 token，禁止散落硬编码。
- `wwwroot/app.js` 是唯一 JS 互操作文件，零第三方依赖；Blazor 组件经 `window.app.*` 调用（copyText / fitComposer）；代码块复制按钮由 JS 内部 MutationObserver 注入，不经互操作入口。
- 对话流渲染：流式消息按「已完结块 + 尾段」拆分（`MessageList.SplitStreamingTail`），尾段承载入场动效；拆分点不得落在未闭合代码围栏内。
