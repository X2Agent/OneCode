# P4 · ssh/mosh 远程工作流

> 状态：待实施（纯文档交付）。
> 依赖：[P1 daemon](p1-daemon-acceptance.md)（会话与定时任务常驻）、[P2b sessions/attach](p2b-sessions-attach.md)
> （跨终端接入；ssh 只是传输层，接入能力本身由 P2b 提供）。
> 关联：[docs/adr/0013-ui-surface-strategy.md](../adr/0013-ui-surface-strategy.md)（ssh 即协议、不为此建
> 自有协议的裁决）。

## 1. 背景与动机

TUI + daemon + attach 的组合天然可远程化：ssh 分配伪终端，mosh 管漫游，tmux 管断线。缺的只是一份把
三件套与 OneCode 的 daemon/attach 串起来的操作文档。定位：远程体验「可用而非精致」，不建自有协议、
不做 Web 网关。

## 2. 目标

交付 `docs/remote-workflow.md`，覆盖：

1. **基础接入**：`ssh -t devbox onecode` 直接进 TUI；或登录后 `onecode attach <id>` 接入运行中会话。
2. **漫游与断线**：mosh 替代 ssh 抗网络切换；tmux 包一层管断线续连（会话本体在 daemon，断线零损失）。
3. **Windows 开发机作服务端**：OpenSSH Server 启用步骤与防火墙注意点。
4. **手机端**：Termius / Blink 走同一套（ssh → attach → 应答审批 → 看 cron 历史），全程 TUI。
5. **常见问题**：终端尺寸适配、键位映射（手机键盘）、审批 toast 在远程不可达时的替代路径
   （`onecode sessions` 查看未决审批）。

## 3. 非目标

- 自有远程协议、Web 网关、端口转发图形界面（[ADR-0013](../adr/0013-ui-surface-strategy.md)、
  [ADR-0017](../adr/0017-ui-contract-single-layer.md) 边界）。
- 性能调优（字体渲染、滚动优化等终端模拟器自身话题）。

## 4. 验收

1. 手机（Termius 或 Blink）ssh 到开发机，`onecode attach` 接入一个运行中会话，看到实时进度并完成
   一次审批应答。
2. 手机上 `onecode sessions` 看到 cron 执行历史会话。
3. mosh 会话网络切换后界面恢复继续工作；tmux detach/attach 演练一遍。
4. 文档随 P2b 实测修订一次（attach 行为与文档一致）。
