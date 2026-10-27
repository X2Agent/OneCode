# P1 · daemon 端到端验收

> 状态：待执行（代码与脚本已落地，验收未手动执行过）。
> 依赖：无（P2a 的后台会话宿主是 daemon，本验收是 P2a 的前置）。
> 关联：[docs/background-services.md](../background-services.md)（cron/daemon 语义总览）、
> [docs/adr/0013-ui-surface-strategy.md](../adr/0013-ui-surface-strategy.md)（后台能力进守护进程、不进 UI 的原则）。

## 1. 背景与动机

`onecode daemon` 无头宿主、`/daemon` 命令、cron 多进程正确性、通知触发补齐与三平台开机自启脚本均已提交
（`src/OneCode.App/Daemon/DaemonHost.cs` / `DaemonLifecycle.cs` / `DaemonStopWatcher.cs`、
`src/OneCode.App/Commands/DaemonCommand.cs`、`scripts/install-daemon.ps1` / `onecode-daemon.service` /
`com.onecode.daemon.plist`），但**端到端验收从未手动执行**。后续多会话计划（P2a/P2b）把后台会话的宿主
压在 daemon 上，地基未验收就往上盖有返工风险，故本验收独立成项、先行补齐。

## 2. 目标

按 §4 验收清单逐项手动执行并记录结果，全部通过后本计划关闭；执行中发现的缺陷在本计划范围内修复。

## 3. 非目标

- daemon 新功能（后台会话执行属 P2a，attach 属 [P2b](p2b-sessions-attach.md)）。
- 托盘、OSC 9 并发终端标记（已裁决不做/后置，见 [ADR-0013](../adr/0013-ui-surface-strategy.md)）。

## 4. 验收清单

### 4.1 常驻与调度

1. **cron 照跑**：TUI 完全关闭，`/daemon start`（或脚本）启动 daemon；创建一个 durable cron 作业
   （如 `*/1 * * * *`），到点触发、会话落盘、`~/.onecode/cron/locks/{id}.lock` 独占锁出现。
2. **停机补跑**：daemon 停止跨过至少一个触发点后重启，离线期间到期作业在启动后首个轮询周期（≤30 秒）补跑一次。
3. **多进程不双发**：同项目 TUI + daemon 并存，同一时刻作业只被一方触发（跨进程逐作业锁，拿不到即跳过）。
4. **项目归属过滤**：别项目的 durable 作业（`CronJobEntry.ProjectDir` 盖章不匹配）不在本项目进程触发。

### 4.2 通知

5. **审批宽限 toast**：交互运行中审批挂起超 30 秒，桌面 toast 到达（`InteractionApprovalPromptSink`）。
6. **长运行完成 toast**：交互运行超 2 分钟后完成，toast 到达（`QueryStreamEngine`）；无人值守路径由
   `CronJobExecutor` 通知且不双发。

### 4.3 管理面与会话可见性

7. **`/daemon` 命令**：状态视图（PID/启动时间/durable cron 概况）、`start` 回报 PID 与日志目录、
   `stop` 优雅停止（15 秒上限，超时只报告不强杀）；daemon 未运行时状态视图给出安装指引。
8. **历史可见**：重开 TUI 经 `/session` 可见 cron 执行产生的会话及其转录。

### 4.4 开机自启脚本

9. **Windows 实测**：`scripts/install-daemon.ps1` 安装→重启登录→daemon 自起；卸载脚本清理干净。
10. **macOS/Linux 走查**：`com.onecode.daemon.plist` / `onecode-daemon.service` 模板与当前
    `DaemonHost` 启动参数一致（无实机时走查 + 与 Windows 路径对照）。

### 4.5 生命周期边界

11. **单实例互斥**：第二个 daemon 进程启动即退出；互斥体兼作 `/daemon` 存活探针。
12. **停止哨兵**：`DaemonStopWatcher` 感知停止文件后进程在优雅期限内退出，后台服务逐个收尾无悬挂。

## 5. 缺陷处置

验收发现的缺陷按严重度处理：阻断级（数据错写、双发、进程悬挂）当场修复并回归本清单；体验级记录后随
最近批次修复。修复不改变 §3 非目标边界。

## 6. 文档生命周期

验收全部通过后**删除本文档**（执行记录交 git），daemon 语义以
[docs/background-services.md](../background-services.md) 为唯一总览。
