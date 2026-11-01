#!/usr/bin/env pwsh
<#
.SYNOPSIS
    OneCode 真实 GitHub Release 的下载与安装验证。
.DESCRIPTION
    对已经发布的 tag 做端到端验证，覆盖「用户在真实域名上下载并安装」这条路径：

      1. 预检       tag 形状、依赖脚本存在性
      2. 资产清点   Release 上该 tag 的资产与 .sha256 是否齐全
      3. 下载       从 releases/download/<tag>/ 取 win-x64 归档与校验和
      4. 完整性     独立重算 SHA256 并与 .sha256 比对
      5. 解包       解压到本次运行目录，断言可执行文件与 prompts 资源齐全
      6. 发布契约   对解压出来的 OneCode.Tui.exe 跑启动契约断言
      7. 安装       用注入副本（只改版本解析与落位目录）执行生产安装逻辑
      8. 安装契约   对安装出来的 onecode.exe 跑同一套启动契约断言
      9. 环境完整性 全程结束后用户 PATH 哈希必须与开始时一致

    注入副本的锚点唯一性与还原比对由 scripts/lib/installer-injection.ps1 提供，
    与 scripts/local-release-e2e.ps1 共用同一份实现；生产逻辑逐字符保持原样。

    预发布 tag 不占 /releases/latest（这是有意设计，避免一键安装用户被推到 beta），
    因此本脚本用「固定 tag」注入验证；正式版还应额外验证未改动的一键安装路径。

    失败即停：任一步失败以退出码 1 终止，并打印该步期望、实际与日志尾部。
    中间产物留在 artifacts/verify/<tag>/，不生成其他报告文件。
.PARAMETER Tag
    要验证的 Release tag，含 v 前缀（如 v1.1.0-rc.1）。
.PARAMETER Runtime
    目标 RID，默认当前平台的 win-x64。当前只支持 Windows 资产（.zip）。
.PARAMETER Repository
    owner/repo，默认 X2Agent/OneCode。
.PARAMETER SkipDownload
    复用 artifacts/verify/<tag>/ 下已下载的归档与校验和，跳过网络下载（离线回归）。
.EXAMPLE
    ./verify-github-release.ps1 -Tag v1.1.0-rc.1
    ./verify-github-release.ps1 -Tag v1.1.0-rc.1 -SkipDownload
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Runtime = "win-x64",

    [string]$Repository = "X2Agent/OneCode",

    [switch]$SkipDownload
)

$ErrorActionPreference = "Stop"

# ── 常量与路径 ────────────────────────────────────────────────────
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ContractScript = Join-Path $PSScriptRoot "test-binary-contract.ps1"
$InstallerScript = Join-Path $PSScriptRoot "install.ps1"
$InjectionLibrary = Join-Path $PSScriptRoot "lib/installer-injection.ps1"

# 注入实现与本地发布 e2e 共用：锚点唯一性与还原比对规则只有一份。
. $InjectionLibrary

$PwshPath = Join-Path $PSHOME "pwsh.exe"
if (-not (Test-Path -LiteralPath $PwshPath -PathType Leaf)) {
    $PwshPath = (Get-Process -Id $PID).Path
}

$Version = $Tag.TrimStart("v")
$RunDir = Join-Path $RepoRoot "artifacts/verify/$Tag"
$DownloadDir = Join-Path $RunDir "download"
$PayloadDir = Join-Path $RunDir "payload"
$InstallDir = Join-Path $RunDir "install"
$InjectedPath = Join-Path $RunDir "install-github-tag.ps1"
$InstallLog = Join-Path $RunDir "install.log"
$AssetName = "onecode-$Version-$Runtime.zip"
$DownloadBase = "https://github.com/$Repository/releases/download/$Tag"

$globalStep = 0

# ── 输出辅助 ──────────────────────────────────────────────────────
function Write-Phase {
    param([int]$Index, [string]$Name)
    Write-Host ""
    Write-Host "[$Index] $Name" -ForegroundColor Cyan
}

function Write-Ok { param($Message) Write-Host "  ✓ $Message" -ForegroundColor Green }
function Write-Fail { param($Message) Write-Host "  ✗ $Message" -ForegroundColor Red }

# ── 子进程执行（与 local-release-e2e.ps1 同一约定） ───────────────
function ConvertTo-SingleQuoted {
    param([string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function New-PwshScriptCommand {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [string[]]$ScriptArguments = @()
    )

    # 开关名必须是裸 token，否则会被当成位置参数；值一律加单引号防止空格拆词。
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; & ")
    [void]$builder.Append((ConvertTo-SingleQuoted $ScriptPath))

    for ($index = 0; $index -lt $ScriptArguments.Count; $index++) {
        $argument = $ScriptArguments[$index]
        $hasValue = ($index + 1) -lt $ScriptArguments.Count -and
            $ScriptArguments[$index + 1] -notmatch '^-[A-Za-z][A-Za-z0-9]*$'

        if ($argument -match '^-[A-Za-z][A-Za-z0-9]*$') {
            [void]$builder.Append(" ").Append($argument)
            if ($hasValue) {
                [void]$builder.Append(" ").Append((ConvertTo-SingleQuoted $ScriptArguments[$index + 1]))
                $index++
            }
        }
        else {
            [void]$builder.Append(" ").Append((ConvertTo-SingleQuoted $argument))
        }
    }

    return $builder.ToString()
}

function Invoke-PwshScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [string[]]$ScriptArguments = @(),
        [string]$LogPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $PwshPath
    $startInfo.ArgumentList.Add("-NoProfile")
    $startInfo.ArgumentList.Add("-Command")
    $startInfo.ArgumentList.Add((New-PwshScriptCommand -ScriptPath $ScriptPath -ScriptArguments $ScriptArguments))
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    # 子进程内部已把输出编码固定为 UTF-8，这里按 UTF-8 解码即可。
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $startInfo.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($startInfo)
    # 必须异步读两个流：顺序 ReadToEnd 会在子进程写满缓冲区时死锁。
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()

    $stdOut = $outTask.Result
    $stdErr = $errTask.Result
    $combined = (@($stdOut, $stdErr) | Where-Object { $_ }) -join "`n"

    if ($LogPath) {
        Set-Content -LiteralPath $LogPath -Value $combined -Encoding utf8
    }

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut   = $stdOut
        StdErr   = $stdErr
        Combined = $combined
    }
}

function Get-LogTail {
    param([string]$Path, [int]$Lines = 15)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return "" }
    return ((Get-Content -LiteralPath $Path -Tail $Lines) -join "`n")
}

# ── 资产查询 ──────────────────────────────────────────────────────
function Get-ReleaseAssets {
    $apiUrl = "https://api.github.com/repos/$Repository/releases/tags/$Tag"
    $headers = @{
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "onecode-release-verifier"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    $token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } elseif ($env:GH_TOKEN) { $env:GH_TOKEN } else { $null }
    if ($token) {
        $headers["Authorization"] = "Bearer $token"
    }

    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -ErrorAction Stop
    return [pscustomobject]@{
        IsPrerelease = [bool]$release.prerelease
        Draft        = [bool]$release.draft
        Names        = @($release.assets | ForEach-Object { [string]$_.name })
    }
}

# ── 主流程 ────────────────────────────────────────────────────────
$pathHashBefore = Get-UserPathHash

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  OneCode GitHub Release 下载与安装验证            ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  Tag:        $Tag" -ForegroundColor White
Write-Host "  Repository: $Repository" -ForegroundColor White
Write-Host "  RID:        $Runtime" -ForegroundColor White
Write-Host "  产物目录:   $RunDir" -ForegroundColor White
Write-Host ""

try {
    # ── 1. 预检 ───────────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "预检"

    if ($Tag -notmatch '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "tag 不符合发布约定（须带 v 前缀的语义化版本）: $Tag"
    }
    if ($Runtime -notlike 'win-*') {
        throw "当前只验证 Windows 资产（.zip）: $Runtime"
    }
    foreach ($required in @($ContractScript, $InstallerScript, $InjectionLibrary)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "找不到依赖脚本: $required"
        }
    }

    # 每次运行重建解包与安装目录：它们承载的是本次下载的产物，混入上一轮文件会让断言失真。
    foreach ($stale in @($PayloadDir, $InstallDir)) {
        if (Test-Path -LiteralPath $stale) {
            Remove-Item -LiteralPath $stale -Recurse -Force
        }
    }
    New-Item -ItemType Directory -Path $DownloadDir, $PayloadDir -Force | Out-Null
    Write-Ok "版本 $Version → 资产 $AssetName"

    # ── 2. 资产清点 ───────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "Release 资产清点"

    $release = Get-ReleaseAssets
    if ($release.Draft) {
        throw "Release $Tag 仍是草稿（draft），尚未对外可见"
    }
    foreach ($expected in @($AssetName, "$AssetName.sha256")) {
        if ($release.Names -notcontains $expected) {
            throw "Release $Tag 缺少资产: $expected（实际: $($release.Names -join ', ')）"
        }
    }
    Write-Ok "$($release.Names.Count) 个资产，含 $AssetName 与校验和"
    Write-Ok $(if ($release.IsPrerelease) { "标记为预发布（不占 /releases/latest）" } else { "标记为正式发布" })

    # ── 3. 下载 ───────────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "下载归档与校验和"

    $assetPath = Join-Path $DownloadDir $AssetName
    $checksumPath = "$assetPath.sha256"

    if ($SkipDownload) {
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
            throw "-SkipDownload 要求已有归档与校验和，但 $DownloadDir 下缺失 $AssetName"
        }
        Write-Ok "复用已有下载: $assetPath"
    }
    else {
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest -Uri "$DownloadBase/$AssetName" -OutFile $assetPath -UseBasicParsing
        Invoke-WebRequest -Uri "$DownloadBase/$AssetName.sha256" -OutFile $checksumPath -UseBasicParsing
        $sizeMb = [math]::Round((Get-Item -LiteralPath $assetPath).Length / 1MB, 1)
        Write-Ok "$AssetName（$sizeMb MB）"
    }

    # ── 4. 完整性 ─────────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "完整性校验"

    # 独立重算而不是复用安装脚本的判定：这里验证的是「发布出去的字节」本身。
    $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    $expectedHash = ($checksumText -split "\s+")[0]
    if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$') {
        throw ".sha256 内容形状不符（应为 64 位十六进制）: $checksumText"
    }
    $actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA256 校验失败。期望 $expectedHash，实际 $actualHash"
    }
    # 文件名必须与资产名一致：换名重放会让校验和失去意义。
    $checksumFileName = ($checksumText -split "\s+")[-1]
    if ($checksumFileName -ne $AssetName) {
        throw ".sha256 记录的文件名不符：期望 $AssetName，实际 $checksumFileName"
    }
    Write-Ok "SHA256 一致: $($actualHash.ToLowerInvariant())"

    # ── 5. 解包 ───────────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "解包与资源检查"

    Expand-Archive -Path $assetPath -DestinationPath $PayloadDir -Force

    $payloadExe = Join-Path $PayloadDir "OneCode.Tui.exe"
    if (-not (Test-Path -LiteralPath $payloadExe -PathType Leaf)) {
        throw "归档根目录缺少 OneCode.Tui.exe"
    }
    foreach ($relative in @("prompts/system/harness.prompt", "prompts/system/default.prompt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $PayloadDir $relative) -PathType Leaf)) {
            throw "归档缺少运行时资源: $relative"
        }
    }
    Write-Ok "OneCode.Tui.exe + prompts 齐全"

    # ── 6. 发布契约 ───────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "发布产物启动契约"

    $payloadContract = Invoke-PwshScript -ScriptPath $ContractScript -ScriptArguments @(
        "-ExePath", $payloadExe, "-ExpectedVersion", $Version
    ) -LogPath (Join-Path $RunDir "contract-payload.log")
    Write-Host $payloadContract.Combined
    if ($payloadContract.ExitCode -ne 0) {
        throw "发布产物启动契约失败（退出码 $($payloadContract.ExitCode)）"
    }

    # ── 7. 安装 ───────────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "注入副本安装（版本解析固定为 $Tag）"

    $sourceText = Get-Content -LiteralPath $InstallerScript -Raw
    $replacementCount = New-GitHubTagInstaller -SourceText $sourceText -TargetPath $InjectedPath `
        -Tag $Tag -TargetInstallDir $InstallDir
    Write-Ok "已替换 $replacementCount 处配置，还原比对通过"

    $install = Invoke-PwshScript -ScriptPath $InjectedPath -LogPath $InstallLog
    Write-Host $install.Combined
    if ($install.ExitCode -ne 0) {
        throw "安装失败（退出码 $($install.ExitCode)）"
    }

    $installedExe = Join-Path $InstallDir "onecode.exe"
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw "安装目录缺少 onecode.exe: $InstallDir"
    }
    foreach ($relative in @("prompts/system/harness.prompt", "prompts/system/default.prompt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $InstallDir $relative) -PathType Leaf)) {
            throw "安装目录缺少运行时资源: $relative"
        }
    }
    # 改名必须生效：残留 OneCode.Tui.exe 说明落位逻辑没走完。
    if (Test-Path -LiteralPath (Join-Path $InstallDir "OneCode.Tui.exe")) {
        throw "安装目录残留 OneCode.Tui.exe（改名未生效）"
    }
    Write-Ok "onecode.exe + prompts 已落位，无改名残留"

    # ── 8. 安装契约 ───────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "安装后启动契约"

    $installedContract = Invoke-PwshScript -ScriptPath $ContractScript -ScriptArguments @(
        "-ExePath", $installedExe, "-ExpectedVersion", $Version
    ) -LogPath (Join-Path $RunDir "contract-installed.log")
    Write-Host $installedContract.Combined
    if ($installedContract.ExitCode -ne 0) {
        throw "安装后启动契约失败（退出码 $($installedContract.ExitCode)）"
    }

    # ── 9. 环境完整性 ─────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "环境完整性"

    $pathHashAfter = Get-UserPathHash
    if (-not [string]::Equals($pathHashBefore, $pathHashAfter, [System.StringComparison]::Ordinal)) {
        throw "用户 PATH 被修改（前后哈希不一致）"
    }
    Write-Ok "用户 PATH 未被修改"
}
catch {
    Write-Host ""
    Write-Fail $_.Exception.Message
    Write-Host ""
    Write-Host "  证据目录: $RunDir" -ForegroundColor Yellow
    $installTail = Get-LogTail $InstallLog
    if ($installTail) {
        Write-Host "  install.log 尾部:" -ForegroundColor Yellow
        Write-Host $installTail -ForegroundColor DarkGray
    }
    exit 1
}

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  ✅ GitHub Release 下载与安装验证通过             ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  产物与日志: $RunDir" -ForegroundColor Gray
Write-Host "  离线回归:   ./scripts/verify-github-release.ps1 -Tag $Tag -SkipDownload" -ForegroundColor Gray
Write-Host ""
