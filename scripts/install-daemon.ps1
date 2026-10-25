# OneCode daemon 开机自启注册脚本（Windows 任务计划程序）
# 用法：
#   .\install-daemon.ps1                        # 注册当前目录为 daemon 项目
#   .\install-daemon.ps1 -Project E:\MyProject  # 注册指定项目
#   .\install-daemon.ps1 -Uninstall             # 注销当前目录的 daemon 任务
#   .\install-daemon.ps1 -Project E:\MyProject -Uninstall
#
# 一个 daemon 绑定一个项目（durable cron 只在归属项目的进程中触发）；
# 注册后登录时自动隐藏启动，TUI 内 /daemon 可查看状态并显式启停。

param(
    [string]$Project = (Get-Location).Path,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

function Write-Info { param($Message) Write-Host "▸ $Message" -ForegroundColor Cyan }
function Write-Ok { param($Message) Write-Host "✓ $Message" -ForegroundColor Green }
function Write-Err { param($Message) Write-Host "✗ $Message" -ForegroundColor Red }

$Project = [System.IO.Path]::GetFullPath($Project)
if (-not (Test-Path -LiteralPath $Project -PathType Container)) {
    Write-Err "项目目录不存在：$Project"
    exit 1
}

# 项目 slug：目录名（仅安全字符）+ 路径哈希前 8 位——与 App 内 DaemonLifecycle.GetSlug 同规则，
# 保证任务名跨项目唯一且人可读。
$hashBytes = [System.Security.Cryptography.SHA256]::HashData(
    [System.Text.Encoding]::UTF8.GetBytes($Project))
$hash = ([BitConverter]::ToString($hashBytes)).Replace("-", "").Substring(0, 8).ToLowerInvariant()
$safeName = (Split-Path -Leaf ($Project.TrimEnd('\', '/'))) -replace '[^a-zA-Z0-9]', ''
$slug = if ($safeName) { "$safeName-$hash" } else { "daemon-$hash" }
$TaskName = "OneCodeDaemon-$slug"

if ($Uninstall) {
    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($existing) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Ok "已注销任务 $TaskName（项目 $Project）。正在运行的 daemon 不受影响，可用 TUI 内 /daemon stop 停止。"
    }
    else {
        Write-Info "任务 $TaskName 不存在，无需注销。"
    }
    exit 0
}

$onecode = Get-Command onecode -ErrorAction SilentlyContinue
if (-not $onecode) {
    $fallback = Join-Path $env:USERPROFILE ".onecode\bin\onecode.exe"
    if (Test-Path $fallback) { $onecode = [PSCustomObject]@{ Source = $fallback } }
}
if (-not $onecode) {
    Write-Err "找不到 onecode 可执行文件（PATH 与 ~/.onecode/bin 均无）。请先运行 install.ps1。"
    exit 1
}
$ExePath = $onecode.Source

# 隐藏窗口启动：直接以 exe 为任务动作会在登录时弹出控制台窗口，经
# powershell -WindowStyle Hidden + Start-Process -WindowStyle Hidden 创建无窗进程。
# 内层命令只用单引号，避免与 -Command "..." 的外层双引号嵌套冲突。
$innerCommand = "Start-Process -WindowStyle Hidden -FilePath '$ExePath' -ArgumentList '--cwd','$Project','daemon'"
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -WindowStyle Hidden -Command `"$innerCommand`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $TaskName

Write-Ok "已注册并启动任务 $TaskName"
Write-Info "项目：$Project"
Write-Info "可执行：$ExePath"
Write-Info "TUI 内 /daemon 可查看运行状态；注销：.\install-daemon.ps1 -Project `"$Project`" -Uninstall"
