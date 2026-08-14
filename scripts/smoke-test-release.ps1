#!/usr/bin/env pwsh
<#
.SYNOPSIS
    OneCode 发布产物冒烟测试 —— 在 CI 打包前验证刚 publish 出来的可执行文件可用。
.DESCRIPTION
    由 .github/workflows/release.yml 在每个 RID 矩阵上调用,断言:
      1. 可执行文件存在于发布目录
      2. --version 输出包含期望版本号
      3. 关键运行时资源(prompts / Team YAML)已随包发布
    任一断言失败立即以非零码退出,阻断后续打包/上传步骤。
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

# Unix 平台需要可执行权限(Linux/macOS runner 上 publish 应自动设置,此处只做提示性检查)
if (-not $isWindowsRid -and -not $IsWindows) {
    $mode = [System.IO.FileSystemInfo]::new($exePath).UnixMode
    if ($mode -and $mode -notmatch "x") {
        Write-Step "补充可执行权限"
        chmod +x $exePath
    }
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

# ── 4. --help 冒烟(可选,验证 DI 容器能初始化) ───────────────────
# --help 走 FullCli 路径,会触发最小 DI 组装;失败说明入口程序集加载有问题
Write-Step "执行 --help(验证入口完整性)"
& $exePath --help 2>&1 | Out-Null
$helpExit = $LASTEXITCODE
if ($helpExit -ne 0) {
    Write-Fail "--help 退出码非零: $helpExit"
    exit 1
}
Write-Pass "入口完整"

# ── 摘要 ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  ✅ Smoke Test PASSED                             ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
