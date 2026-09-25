#!/usr/bin/env pwsh
<#
.SYNOPSIS
    OneCode 发布产物冒烟测试 —— 在 CI 打包前验证刚 publish 出来的可执行文件可用。
.DESCRIPTION
    由 .github/workflows/release.yml 在每个 RID 矩阵上调用,断言:
      1. 可执行文件存在于发布目录
      2. --version 输出包含期望版本号
      3. 关键运行时资源(prompts / Team YAML)已随包发布
      4. 启动层契约:--version/-v/-V 快路径、--cwd 与快路径的组合、
         --cwd 用法错误必须以退出码 2 + stderr 提示终止
    任一断言失败立即以非零码退出,阻断后续打包/上传步骤。

    覆盖边界:交互式 TUI 需要真实 TTY,无法在 CI 里启动,因此不在本脚本范围内;
    它的驱动级验证由 OneCode.Tests 的 TuiHeadless* 用例(真实 ANSI 驱动 + 按键/鼠标注入)承担。
.PARAMETER PublishDir
    dotnet publish 的输出目录,包含 OneCode.Cli(.exe) 及运行时资源。
.PARAMETER ExpectedVersion
    期望的语义版本号(不带 v 前缀,如 1.0.0),来自 git tag。
.PARAMETER Runtime
    目标 RID,如 win-x64、linux-arm64、osx-arm64。
.EXAMPLE
    ./smoke-test-release.ps1 -PublishDir ./artifacts/publish/win-x64 -ExpectedVersion 1.0.0 -Runtime win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string]$Runtime
)

$ErrorActionPreference = "Stop"

# ── 辅助函数 ──────────────────────────────────────────────────────
function Write-Step { param($Message) Write-Host "  ▸ $Message" -ForegroundColor Cyan }
function Write-Pass  { param($Message) Write-Host "  ✓ $Message" -ForegroundColor Green }
function Write-Fail  { param($Message) Write-Host "  ✗ $Message" -ForegroundColor Red }

# ── 入口校验 ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  OneCode Smoke Test                               ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  PublishDir:      $PublishDir" -ForegroundColor White
Write-Host "  ExpectedVersion: $ExpectedVersion" -ForegroundColor White
Write-Host "  Runtime:         $Runtime" -ForegroundColor White
Write-Host ""

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    Write-Fail "发布目录不存在: $PublishDir"
    exit 1
}

# ── 1. 定位可执行文件 ─────────────────────────────────────────────
$isWindowsRid = $Runtime -like "win-*"
$exeName = if ($isWindowsRid) { "OneCode.Cli.exe" } else { "OneCode.Cli" }
$exePath = Join-Path $PublishDir $exeName

Write-Step "定位可执行文件: $exeName"
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Write-Fail "找不到可执行文件: $exePath"
    Write-Host ""
    Write-Host "  发布目录内容:" -ForegroundColor Yellow
    Get-ChildItem -LiteralPath $PublishDir -Force | Select-Object -First 20 |
        ForEach-Object { Write-Host "    $($_.Name)" -ForegroundColor Gray }
    exit 1
}
Write-Pass "可执行文件存在: $exePath"

# Unix 平台需要可执行权限。无条件 chmod +x（幂等）——不要用反射检查权限位，
# [System.IO.FileSystemInfo]::new() 是抽象类实例化，在 pwsh 上直接抛异常。
if (-not $isWindowsRid -and -not $IsWindows) {
    Write-Step "补充可执行权限"
    chmod +x $exePath
}

# ── 2. --version 断言 ─────────────────────────────────────────────
Write-Step "执行 --version"
$versionOutput = & $exePath --version
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    Write-Fail "--version 退出码非零: $exitCode"
    Write-Host "  输出: $versionOutput" -ForegroundColor Yellow
    exit 1
}

# FastPathDispatcher 输出格式: "{version} ({ProductName})",如 "1.0.0 (OneCode)"
if (-not ($versionOutput -match [regex]::Escape($ExpectedVersion))) {
    Write-Fail "版本号不匹配"
    Write-Host "  期望包含: $ExpectedVersion" -ForegroundColor Yellow
    Write-Host "  实际输出: $versionOutput" -ForegroundColor Yellow
    exit 1
}
Write-Pass "版本号匹配: $versionOutput"

# ── 3. 关键运行时资源检查 ────────────────────────────────────────
# 发布包包含 prompts、Team YAML 等运行时资源(见 install.ps1 注释)。
# 这里只验证最关键的几个:缺了它们 TUI 起不来 / 命令报错。
$requiredResources = @(
    "prompts/system/harness.prompt",
    "prompts/system/default.prompt"
)

foreach ($rel in $requiredResources) {
    $full = Join-Path $PublishDir $rel
    Write-Step "检查资源: $rel"
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        Write-Fail "缺失关键资源: $full"
        exit 1
    }
    Write-Pass "存在"
}

# ── 4. 启动层契约（快路径 / --cwd / 退出码）─────────────────────────
# 这一节无需 TTY:--version 走 DI 容器之前的快路径,--cwd 用法错误走两层启动之前的分支,
# 两者在无终端环境下都给出确定的 stdout/stderr 与退出码,因此可被 CI 断言。
# 用真实子进程而不是 `&` 调用,才能独立取到 stdout 与 stderr,并区分二者。

function Get-CommandLine {
    # Start 只接受一整条命令行字符串,含空白或引号的参数必须自行加引号,
    # 否则 PublishDir 带空格时会被拆成多个参数。
    param([string[]]$Arguments)

    ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_ + '"' } else { $_ } }) -join ' '
}

function Invoke-Cli {
    param([string[]]$Arguments)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $exePath
    $startInfo.Arguments = Get-CommandLine $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    # 提示信息含中文,固定按 UTF-8 解码,避免因宿主默认代码页不同而断言失败。
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $proc = [System.Diagnostics.Process]::Start($startInfo)
    # 必须异步读两个流:顺序 ReadToEnd 会在子进程写满缓冲区时死锁。
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()

    [pscustomobject]@{
        ExitCode = $proc.ExitCode
        StdOut   = $outTask.Result
        StdErr   = $errTask.Result
    }
}

$missingDir = Join-Path $PublishDir ("__missing__" + [guid]::NewGuid().ToString("N"))
$versionShape = '^\d+\.\d+\.\d+ \(OneCode\)$'

$startupCases = @(
    @{ Name = "--version 快路径";            Args = @("--version"); Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "-v 短别名";                   Args = @("-v");        Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "-V 短别名";                   Args = @("-V");        Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "--cwd <有效目录> --version";  Args = @("--cwd", $PublishDir, "--version"); Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "-C <有效目录> --version";     Args = @("-C", $PublishDir, "--version");    Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "--cwd 缺少目录参数";          Args = @("--cwd");               Exit = 2; OutLike = $null; Err = "用法错误" }
    @{ Name = "--cwd= 空路径";               Args = @("--cwd=");              Exit = 2; OutLike = $null; Err = "用法错误" }
    @{ Name = "--cwd <不存在的目录>";        Args = @("--cwd", $missingDir);  Exit = 2; OutLike = $null; Err = "目录不存在" }
)

foreach ($case in $startupCases) {
    Write-Step $case.Name
    $result = Invoke-Cli $case.Args

    if ($result.ExitCode -ne $case.Exit) {
        Write-Fail "退出码不符: 期望 $($case.Exit),实际 $($result.ExitCode)"
        Write-Host "  参数: $($case.Args -join ' ')" -ForegroundColor Yellow
        Write-Host "  stdout: $($result.StdOut)" -ForegroundColor Yellow
        Write-Host "  stderr: $($result.StdErr)" -ForegroundColor Yellow
        exit 1
    }

    if ($null -ne $case.OutLike -and $result.StdOut.Trim() -notmatch $case.OutLike) {
        Write-Fail "stdout 形状不符: 期望匹配 $($case.OutLike)"
        Write-Host "  实际: $($result.StdOut)" -ForegroundColor Yellow
        exit 1
    }

    # 正常路径不得往 stderr 写内容,否则会污染下游脚本对错误的判定。
    if ($null -eq $case.Err -and $result.StdErr.Trim().Length -ne 0) {
        Write-Fail "正常路径不应输出 stderr: $($result.StdErr)"
        exit 1
    }

    # 错误路径必须只写 stderr:stdout 保持干净,且不能悄悄退化成启动 TUI。
    if ($null -ne $case.Err) {
        if ($result.StdErr -notmatch [regex]::Escape($case.Err)) {
            Write-Fail "stderr 缺少提示: 期望包含 $($case.Err)"
            Write-Host "  实际: $($result.StdErr)" -ForegroundColor Yellow
            exit 1
        }
        if ($result.StdOut.Trim().Length -ne 0) {
            Write-Fail "用法错误不应写 stdout: $($result.StdOut)"
            exit 1
        }
    }

    Write-Pass "退出码 $($case.Exit) / 契约成立"
}

# ── 摘要 ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  ✅ Smoke Test PASSED                             ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
