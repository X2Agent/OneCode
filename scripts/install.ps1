# OneCode Installer for Windows
# Usage: irm https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.ps1 | iex
# Or:    Set-ExecutionPolicy Bypass -Scope Process -Force; & ([scriptblock]::Create((irm https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.ps1)))

$ErrorActionPreference = "Stop"

# ── 配置 ──────────────────────────────────────────────────────────
$RepoOwner = if ($env:ONECODE_REPO_OWNER) { $env:ONECODE_REPO_OWNER } else { "X2Agent" }
$RepoName = if ($env:ONECODE_REPO_NAME) { $env:ONECODE_REPO_NAME } else { "OneCode" }
$InstallDir = if ($env:ONECODE_INSTALL_DIR) { $env:ONECODE_INSTALL_DIR } else { Join-Path $env:USERPROFILE ".onecode\bin" }
$BinaryName = "onecode.exe"
$GitHubBase = "https://github.com/$RepoOwner/$RepoName"

# ── 辅助函数 ──────────────────────────────────────────────────────
function Write-Info { param($Message) Write-Host "▸ $Message" -ForegroundColor Cyan }
function Write-Ok { param($Message) Write-Host "✓ $Message" -ForegroundColor Green }
function Write-Err { param($Message) Write-Host "✗ $Message" -ForegroundColor Red }

# ── 检测平台 ──────────────────────────────────────────────────────
function Get-Platform {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    switch ($architecture) {
        "x64" { return "win-x64" }
        "arm64" { return "win-arm64" }
        default { throw "不支持的架构: $architecture" }
    }
}

# ── 获取最新版本 ──────────────────────────────────────────────────
function Get-LatestVersion {
    $headers = @{
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "onecode-installer"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest" `
        -Headers $headers `
        -ErrorAction Stop

    $version = [string]$release.tag_name
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "GitHub latest release 响应不包含 tag_name: $RepoOwner/$RepoName"
    }

    return $version
}

# ── 下载并安装 ────────────────────────────────────────────────────
function Install-Binary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Platform,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $assetVersion = $Version.TrimStart("v")
    $downloadUrl = "$GitHubBase/releases/download/$Version/onecode-$assetVersion-$Platform.zip"
    $tempDir = Join-Path $env:TEMP "onecode-install-$(Get-Random)"
    $extractDir = Join-Path $tempDir "payload"
    $zipPath = Join-Path $tempDir "onecode.zip"
    $checksumPath = "$zipPath.sha256"

    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

    try {
        Write-Info "下载 OneCode $Version ($Platform)..."
        Write-Info "URL: $downloadUrl"

        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
        Invoke-WebRequest -Uri "$downloadUrl.sha256" -OutFile $checksumPath -UseBasicParsing

        Write-Info "校验 SHA256..."
        $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
        $expectedHash = ($checksumText -split "\s+")[0]
        $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "SHA256 校验失败。期望 $expectedHash，实际 $actualHash"
        }

        Write-Info "解压中..."
        Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

        # 发布包包含 prompts、Team YAML 等运行时资源，必须整体安装。
        $sourceExecutable = Join-Path $extractDir "OneCode.Cli.exe"
        if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
            throw "在归档根目录中找不到 OneCode.Cli.exe"
        }

        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Get-ChildItem -LiteralPath $extractDir -Force | Copy-Item -Destination $InstallDir -Recurse -Force

        $targetPath = Join-Path $InstallDir $BinaryName
        Move-Item -LiteralPath (Join-Path $InstallDir "OneCode.Cli.exe") -Destination $targetPath -Force

        Write-Ok "已安装到 $targetPath（含运行时资源）"
        return $targetPath
    }
    finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ── 配置 PATH ─────────────────────────────────────────────────────
function Set-PathEnvironment {
    $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $pathEntries = @($currentPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $containsInstallDir = $pathEntries | Where-Object {
        [string]::Equals($_.TrimEnd("\"), $InstallDir.TrimEnd("\"), [StringComparison]::OrdinalIgnoreCase)
    }

    if ($containsInstallDir) {
        Write-Info "PATH 已包含 $InstallDir"
        return
    }

    $newPath = if ($currentPath) { "$InstallDir;$currentPath" } else { $InstallDir }
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
    Write-Ok "已将 $InstallDir 添加到用户 PATH"

    $env:Path = "$InstallDir;$env:Path"
    Write-Info "已更新当前终端的 PATH"
}

# ── 安装完成提示 ──────────────────────────────────────────────────
function Show-PostInstallInfo {
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║  安装完成!                                        ║" -ForegroundColor Green
    Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
    Write-Host ""
    Write-Host "  运行 " -NoNewline
    Write-Host "onecode --help" -ForegroundColor Cyan -NoNewline
    Write-Host " 开始使用"
    Write-Host ""
    Write-Host "  提示: 你可以设置别名让输入更短:" -ForegroundColor Yellow
    Write-Host '    function cc { onecode @args }' -ForegroundColor White
    Write-Host "    将上面这行添加到你的 PowerShell profile 中"
    Write-Host ""
}

# ── 主流程 ────────────────────────────────────────────────────────
function Main {
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "║       OneCode Installer (Windows)                 ║" -ForegroundColor Cyan
    Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host ""

    $platform = Get-Platform
    Write-Info "检测平台: $platform"

    $version = $env:ONECODE_VERSION
    if (-not $version) {
        $version = Get-LatestVersion
        Write-Info "最新版本: $version"
    }
    else {
        Write-Info "指定版本: $version"
    }

    Install-Binary -Platform $platform -Version $version | Out-Null
    Set-PathEnvironment
    Show-PostInstallInfo
}

Main
