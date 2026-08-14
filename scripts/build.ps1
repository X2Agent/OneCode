#!/usr/bin/env pwsh
<#
.SYNOPSIS
    OneCode 跨平台构建脚本
.DESCRIPTION
    支持两种构建模式：
      - Build      : 开发构建（默认）
      - Publish    : 自包含单文件发布
.PARAMETER Mode
    构建模式: Build | Publish (默认: Build)
.PARAMETER Runtime
    目标 RID，如 win-x64, linux-x64, osx-arm64 (默认: 当前平台)
.PARAMETER Configuration
    构建配置: Debug | Release (默认: Release)
.PARAMETER OutputDir
    输出目录 (默认: artifacts/)
.EXAMPLE
    ./build.ps1 -Mode Publish -Runtime win-x64
#>

[CmdletBinding()]
param(
    [ValidateSet("Build", "Publish")]
    [string]$Mode = "Build",

    [string]$Runtime = "",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepositoryRoot = Split-Path -Parent $ScriptDir
$ProjectFile = Join-Path $RepositoryRoot "src/OneCode.Cli/OneCode.Cli.csproj"

if (-not (Test-Path -LiteralPath $ProjectFile -PathType Leaf)) {
    throw "找不到 OneCode CLI 项目: $ProjectFile"
}

# ── 自动检测当前平台 RID ──────────────────────────────────────────
function Get-CurrentRid {
    $os = if ($IsWindows) { "win" } elseif ($IsMacOS) { "osx" } else { "linux" }
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLower()
    return "$os-$arch"
}

if ([string]::IsNullOrEmpty($Runtime)) {
    $Runtime = Get-CurrentRid
}

if ([string]::IsNullOrEmpty($OutputDir)) {
    $OutputDir = Join-Path $RepositoryRoot "artifacts/$Mode/$Runtime"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $RepositoryRoot $OutputDir
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║       OneCode Build System                        ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Mode:          $Mode" -ForegroundColor White
Write-Host "  Runtime:       $Runtime" -ForegroundColor White
Write-Host "  Configuration: $Configuration" -ForegroundColor White
Write-Host "  Output:        $OutputDir" -ForegroundColor White
Write-Host ""

# ── 确保 .NET SDK 可用 ───────────────────────────────────────────
try {
    $sdkVersion = dotnet --version 2>&1
    Write-Host "  .NET SDK: $sdkVersion" -ForegroundColor Green
} catch {
    Write-Error "未找到 .NET SDK，请先安装: https://dot.net/download"
    exit 1
}

# ── 构建逻辑 ─────────────────────────────────────────────────────
switch ($Mode) {
    "Build" {
        Write-Host "`n▶ 开发构建...`n" -ForegroundColor Green
        dotnet build $ProjectFile -c $Configuration
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    "Publish" {
        Write-Host "`n▶ 自包含单文件发布...`n" -ForegroundColor Green
        dotnet publish $ProjectFile `
            -c $Configuration `
            -r $Runtime `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:PublishReadyToRun=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -o $OutputDir
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

# ── 输出摘要 ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "✅ 构建完成!" -ForegroundColor Green
Write-Host "   输出目录: $OutputDir" -ForegroundColor White

if ($Mode -eq "Publish") {
    $exeName = if ($Runtime -like "win-*") { "OneCode.Cli.exe" } else { "OneCode.Cli" }
    $exePath = Join-Path $OutputDir $exeName
    if (Test-Path $exePath) {
        $size = (Get-Item $exePath).Length
        $sizeMB = [math]::Round($size / 1MB, 2)
        Write-Host "   可执行文件: $exeName ($sizeMB MB)" -ForegroundColor Yellow
    }
}
Write-Host ""
