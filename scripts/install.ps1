# OneCode Installer for Windows
# Usage: irm https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.ps1 | iex
# Or:    Set-ExecutionPolicy Bypass -Scope Process -Force; & ([scriptblock]::Create((irm https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.ps1)))

$ErrorActionPreference = "Stop"

# ── 配置 ──────────────────────────────────────────────────────────
# 生产值即默认值：不提供命令行参数与环境变量入口，一键安装是唯一用户路径。
# 本地发布验证（scripts/local-release-e2e.ps1）按字面量替换下面四行以指向本地源，
# 替换前断言锚点唯一、替换后还原比对全文，因此这四行必须各占一行并保持字面量形式。
$RepoOwner      = "X2Agent"
$RepoName       = "OneCode"
$GitHubBase     = "https://github.com/$RepoOwner/$RepoName"
$ApiBase        = "https://api.github.com"
$InstallDir     = Join-Path $env:USERPROFILE ".onecode\bin"
$BinaryName     = "onecode.exe"
$WritePathToEnv = $true

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
    $token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } elseif ($env:GH_TOKEN) { $env:GH_TOKEN } else { $null }
    if ($token) {
        $headers["Authorization"] = "Bearer $token"
    }

    try {
        $release = Invoke-RestMethod `
            -Uri "$ApiBase/repos/$RepoOwner/$RepoName/releases/latest" `
            -Headers $headers `
            -ErrorAction Stop

        $version = [string]$release.tag_name
        if (-not [string]::IsNullOrWhiteSpace($version)) {
            return $version
        }
    }
    catch {
        Write-Info "GitHub API 请求受限或失败，回退通过 /releases/latest 重定向解析版本..."
    }

    # 回退方案：通过 github.com/<owner>/<repo>/releases/latest 的 302 重定向 URL 提取最新 tag（不消耗 REST API 配额）
    $req = [System.Net.HttpWebRequest]::Create("$GitHubBase/releases/latest")
    $req.Method = "HEAD"
    $req.AllowAutoRedirect = $false
    $req.UserAgent = "onecode-installer"
    try {
        $resp = [System.Net.HttpWebResponse]$req.GetResponse()
    }
    catch [System.Net.WebException] {
        $resp = [System.Net.HttpWebResponse]$_.Exception.Response
    }

    if ($null -ne $resp) {
        $location = $resp.Headers["Location"]
        $resp.Close()
        if ($location -match "/releases/tag/([^/?#]+)") {
            return $Matches[1]
        }
    }

    throw "无法获取 $RepoOwner/$RepoName 的最新 Release 版本（API 与重定向回退均失败）"
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

        # 发布包包含 prompts 等运行时资源，必须整体安装。
        $sourceExecutable = Join-Path $extractDir "OneCode.Tui.exe"
        if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
            throw "在归档根目录中找不到 OneCode.Tui.exe"
        }

        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Get-ChildItem -LiteralPath $extractDir -Force | Copy-Item -Destination $InstallDir -Recurse -Force

        $targetPath = Join-Path $InstallDir $BinaryName
        Move-Item -LiteralPath (Join-Path $InstallDir "OneCode.Tui.exe") -Destination $targetPath -Force

        Write-Ok "已安装到 $targetPath（含运行时资源）"
        return $targetPath
    }
    finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ── 配置 PATH ─────────────────────────────────────────────────────
# 纯函数：只计算目标 PATH 取值，不读写注册表与会话环境。
# 拆开是为了让本地发布验证能在不触碰真实用户环境的前提下验证拼接与去重规则。
# 返回 Path（已存在时原样返回传入值）与 AlreadyPresent。
function Get-UserPathWithInstallDir {
    param(
        [AllowNull()][AllowEmptyString()][string]$CurrentPath,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $pathEntries = @($CurrentPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $normalizedDir = $Directory.TrimEnd("\")
    $alreadyPresent = $pathEntries | Where-Object {
        [string]::Equals($_.TrimEnd("\"), $normalizedDir, [StringComparison]::OrdinalIgnoreCase)
    }

    if ($alreadyPresent) {
        return [pscustomobject]@{ Path = $CurrentPath; AlreadyPresent = $true }
    }

    # 末尾原样保留既有字符串：空项与顺序都不改写，避免安装动作顺手重排用户环境。
    $newPath = if ([string]::IsNullOrEmpty($CurrentPath)) { $Directory } else { "$Directory;$CurrentPath" }
    return [pscustomobject]@{ Path = $newPath; AlreadyPresent = $false }
}

# 读取用户级 PATH 的原文与注册表类型。
# 不能走 [Environment]::GetEnvironmentVariable：它会展开 %VAR% 并把结果固化回去，
# 含变量的 PATH 会被写成死路径。注册表原文才是唯一可靠来源。
# SubKey 参数是为了让验证能在临时键上跑通同一份代码，生产调用一律使用默认值。
function Get-UserPathEntry {
    param(
        [string]$SubKey = 'Environment',
        [string]$Name = 'Path'
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey, $false)
    if ($null -eq $key) {
        return [pscustomobject]@{ Value = $null; Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString }
    }
    try {
        $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
        $value = $null
        # 值为空或不存在时 GetValue 返回 $null，此时沿用 ExpandString（Windows 新建 PATH 的默认类型）。
        foreach ($existing in @($key.GetValueNames())) {
            if ([string]::Equals($existing, $Name, [StringComparison]::OrdinalIgnoreCase)) {
                $kind = $key.GetValueKind($existing)
                $value = [string]$key.GetValue($existing, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                break
            }
        }
        return [pscustomobject]@{ Value = $value; Kind = $kind }
    }
    finally {
        $key.Dispose()
    }
}

# 按原注册表类型写回用户级 PATH。
# 类型必须保留：默认的 ExpandString 被写成 String 后，PATH 里的 %VAR% 不再展开，
# 依赖变量的条目会全部失效（[Environment]::SetEnvironmentVariable 正是这种降级写法）。
function Set-UserPathEntry {
    param(
        [AllowNull()][AllowEmptyString()][string]$Value,
        [Microsoft.Win32.RegistryValueKind]$Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString,
        [string]$SubKey = 'Environment',
        [string]$Name = 'Path',
        [bool]$Notify = $true
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($SubKey)
    if ($null -eq $key) {
        throw "无法打开注册表 HKCU:\$SubKey，PATH 写入失败"
    }
    try {
        $key.SetValue($Name, $Value, $Kind)
    }
    finally {
        $key.Dispose()
    }

    if (-not $Notify) {
        return
    }

    # 通知系统环境变量已变更，否则已运行的资源管理器/终端不会重新读取。
    if (-not ('OneCodeEnvBroadcast' -as [type])) {
        Add-Type -Namespace '' -Name 'OneCodeEnvBroadcast' -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam,
    string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
    }
    $result = [UIntPtr]::Zero
    [void][OneCodeEnvBroadcast]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)
}

function Set-PathEnvironment {
    if (-not $WritePathToEnv) {
        Write-Info "已跳过 PATH 写入，请用绝对路径运行"
        return
    }

    $entry = Get-UserPathEntry
    $result = Get-UserPathWithInstallDir -CurrentPath $entry.Value -Directory $InstallDir

    if ($result.AlreadyPresent) {
        Write-Info "PATH 已包含 $InstallDir"
        return
    }

    Set-UserPathEntry -Value $result.Path -Kind $entry.Kind
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
    if ($WritePathToEnv) {
        Write-Host "  运行 " -NoNewline
        Write-Host "onecode" -ForegroundColor Cyan -NoNewline
        Write-Host " 进入交互界面（onecode --version 查看版本）"
        Write-Host ""
        Write-Host "  提示: 你可以设置别名让输入更短:" -ForegroundColor Yellow
        Write-Host '    function cc { onecode @args }' -ForegroundColor White
        Write-Host "    将上面这行添加到你的 PowerShell profile 中"
    }
    else {
        Write-Host "  运行 " -NoNewline
        Write-Host (Join-Path $InstallDir $BinaryName) -ForegroundColor Cyan -NoNewline
        Write-Host " 进入交互界面（未写入 PATH）"
    }
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

    $version = Get-LatestVersion
    Write-Info "最新版本: $version"

    Install-Binary -Platform $platform -Version $version | Out-Null
    Set-PathEnvironment
    Show-PostInstallInfo
}

Main
