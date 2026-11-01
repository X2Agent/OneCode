#!/usr/bin/env pwsh
<#
.SYNOPSIS
    OneCode 本地发布端到端验证。
.DESCRIPTION
    在本地复现「发布 → 打包 → 安装 → 运行」全链路，不依赖 GitHub 与本机已安装的 OneCode：

      1. 预检       版本语义化校验 + dotnet SDK 可用性
      2. 发布构建   与 .github/workflows/release.yml 完全相同的 publish 参数
      3. 产物冒烟   scripts/smoke-test-release.ps1（含共享的启动契约断言）
      4. 打包       onecode-{版本}-win-x64.zip + 同名 .sha256（格式同 release.yml）
      5. 本地源     scripts/local-release-server.ps1 模拟 Release API、302 与资产下载
      6. 配置注入   复制 install.ps1，仅替换配置块四行（下载基址、API 基址、安装目录、是否写 PATH）
      7. PATH 规则   从 install.ps1 原文提取拼接纯函数并执行，不触碰真实用户 PATH
      8. PATH 注册表 在一次性注册表子键上验证读写保留原类型、变量不被展开
      9. 安装       以子进程运行注入副本，验证下载、SHA256 校验、解压、改名、资源完整性
     10. 安装后契约 对安装出来的 onecode.exe 执行与发布产物相同的启动契约断言
     11. 重复安装   覆盖同一目录，校验无改名残留
     12. 负向用例   篡改 .sha256 必须失败且不产出可执行文件
     13. 回退用例   -FailApi 驱动的 302 回退必须装成
     14. 环境完整性 全程结束后用户 PATH 哈希必须与开始时一致

    失败即停：任一步失败以退出码 1 终止，并打印该步输入、期望、实际与 install.log 尾部。
    中间产物留在 artifacts/e2e/<时间戳>/（publish 目录、zip + sha256、注入副本、安装目录、
    install.log），不生成其他报告文件。
.PARAMETER Version
    本次模拟发布的版本号（不含 v 前缀），默认 1.0.0-local。
.PARAMETER SkipPublish
    复用 artifacts/e2e/_publish 里上一次的发布产物，跳过 dotnet publish（约 10 秒回归）。
.EXAMPLE
    ./local-release-e2e.ps1
    ./local-release-e2e.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0-local",

    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

# ── 常量与路径 ────────────────────────────────────────────────────
$Runtime = "win-x64"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectFile = Join-Path $RepoRoot "src/OneCode.Tui/OneCode.Tui.csproj"
$SmokeScript = Join-Path $PSScriptRoot "smoke-test-release.ps1"
$ContractScript = Join-Path $PSScriptRoot "test-binary-contract.ps1"
$ServerScript = Join-Path $PSScriptRoot "local-release-server.ps1"
$InstallerScript = Join-Path $PSScriptRoot "install.ps1"
$InjectionLibrary = Join-Path $PSScriptRoot "lib/installer-injection.ps1"

# 注入实现与真实 Release 验证共用：锚点唯一性与还原比对规则只有一份。
. $InjectionLibrary

$PwshPath = Join-Path $PSHOME "pwsh.exe"
if (-not (Test-Path -LiteralPath $PwshPath -PathType Leaf)) {
    $PwshPath = (Get-Process -Id $PID).Path
}

$RunId = Get-Date -Format "yyyyMMdd-HHmmss"
$RunDir = Join-Path $RepoRoot "artifacts/e2e/$RunId"
$PublishDir = Join-Path $RepoRoot "artifacts/e2e/_publish"
$ReleaseDir = Join-Path $RunDir "release"
$PayloadDir = Join-Path $RunDir "payload"
$InstallDir = Join-Path $RunDir "install"
$TamperedInstallDir = Join-Path $RunDir "install-tampered"
$FallbackInstallDir = Join-Path $RunDir "install-fallback"
$InstallLog = Join-Path $RunDir "install.log"

$Servers = [System.Collections.Generic.List[object]]::new()
$Steps = [System.Collections.Generic.List[object]]::new()
$TamperBackup = $null
$TamperPath = $null

# ── 输出辅助 ──────────────────────────────────────────────────────
function Write-Phase {
    param([int]$Index, [string]$Name)
    Write-Host ""
    Write-Host "[$Index] $Name" -ForegroundColor Cyan
}

function Write-Ok { param($Message) Write-Host "  ✓ $Message" -ForegroundColor Green }
function Write-Fail { param($Message) Write-Host "  ✗ $Message" -ForegroundColor Red }

function Add-Step {
    param([string]$Name, [double]$Seconds)
    $Steps.Add([pscustomobject]@{ Name = $Name; Seconds = $Seconds })
}

# ── 子进程执行 ────────────────────────────────────────────────────
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

# ── 本地发布源 ────────────────────────────────────────────────────
function Start-ReleaseServer {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Tag,
        [switch]$FailApi
    )

    $serverArguments = @("-Root", $Root, "-Tag", $Tag)
    if ($FailApi) { $serverArguments += "-FailApi" }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $PwshPath
    $startInfo.ArgumentList.Add("-NoProfile")
    $startInfo.ArgumentList.Add("-Command")
    $startInfo.ArgumentList.Add((New-PwshScriptCommand -ScriptPath $ServerScript -ScriptArguments $serverArguments))
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $startInfo.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($startInfo)
    # stderr 持续 drain，避免缓冲区写满后阻塞服务进程。
    $null = $process.StandardError.ReadToEndAsync()

    $handshake = $process.StandardOutput.ReadLineAsync()
    if (-not $handshake.Wait([TimeSpan]::FromSeconds(15))) {
        $process.Kill()
        throw "本地发布源 15 秒内未输出就绪握手"
    }

    $readyLine = $handshake.Result
    if ($readyLine -notmatch '^READY\s+(\d+)$') {
        $process.Kill()
        throw "本地发布源握手格式不符: $readyLine"
    }

    $server = [pscustomobject]@{ Process = $process; Port = [int]$Matches[1]; FailApi = [bool]$FailApi }
    $Servers.Add($server)
    return $server
}

function Stop-ReleaseServers {
    foreach ($server in $Servers) {
        if ($null -ne $server.Process -and -not $server.Process.HasExited) {
            try {
                $server.Process.Kill()
                $server.Process.WaitForExit(5000) | Out-Null
            }
            catch {
                # 进程可能已自行退出；清理失败不应掩盖真正的失败原因。
            }
        }
    }
}

# ── 主流程 ────────────────────────────────────────────────────────
$pathHashBefore = Get-UserPathHash
$globalStep = 0

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  OneCode 本地发布端到端验证                       ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  版本:     $Version" -ForegroundColor White
Write-Host "  RID:      $Runtime" -ForegroundColor White
Write-Host "  产物目录: $RunDir" -ForegroundColor White
Write-Host ""

try {
    # ── 1. 预检 ───────────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "预检"
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "版本号不符合语义化版本（不带 v 前缀）: $Version"
    }
    $binaryVersion = ($Version -split '[-+]')[0] + ".0"

    $sdkVersion = (& dotnet --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
        throw "未找到 .NET SDK（dotnet --version 失败）"
    }
    if (-not (Test-Path -LiteralPath $ProjectFile -PathType Leaf)) {
        throw "找不到 TUI 项目: $ProjectFile"
    }
    foreach ($required in @($SmokeScript, $ContractScript, $ServerScript, $InstallerScript, $InjectionLibrary)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "找不到依赖脚本: $required"
        }
    }

    New-Item -ItemType Directory -Path $RunDir, $ReleaseDir, $PayloadDir -Force | Out-Null
    Write-Ok ".NET SDK $sdkVersion"
    Write-Ok "版本 $Version → 程序集版本 $binaryVersion"
    Add-Step "预检" $stopwatch.Elapsed.TotalSeconds

    # ── 2. 发布构建 ───────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "发布构建（$Runtime）"
    $stopwatch.Restart()

    if ($SkipPublish) {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishDir "OneCode.Tui.exe") -PathType Leaf)) {
            throw "-SkipPublish 要求已有发布产物，但 $PublishDir 下没有 OneCode.Tui.exe"
        }
        Write-Ok "复用已有产物: $PublishDir"
    }
    else {
        if (Test-Path -LiteralPath $PublishDir) {
            Remove-Item -LiteralPath $PublishDir -Recurse -Force
        }

        # 参数真相源：.github/workflows/release.yml 的 Publish 步骤。改动此处必须同步该文件。
        & dotnet publish $ProjectFile `
            -c Release `
            -r $Runtime `
            --self-contained true `
            -p:Version=$Version `
            -p:AssemblyVersion=$binaryVersion `
            -p:FileVersion=$binaryVersion `
            -p:PublishSingleFile=true `
            -p:PublishReadyToRun=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -o $PublishDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
        }
        Write-Ok "已发布到 $PublishDir"
    }
    Add-Step "发布构建" $stopwatch.Elapsed.TotalSeconds

    # ── 3. 产物冒烟 ───────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "产物冒烟 + 启动契约"
    $stopwatch.Restart()

    $smoke = Invoke-PwshScript -ScriptPath $SmokeScript -ScriptArguments @(
        "-PublishDir", $PublishDir, "-ExpectedVersion", $Version, "-Runtime", $Runtime
    ) -LogPath (Join-Path $RunDir "smoke.log")
    Write-Host $smoke.Combined
    if ($smoke.ExitCode -ne 0) {
        throw "产物冒烟失败（退出码 $($smoke.ExitCode)）"
    }
    Add-Step "产物冒烟" $stopwatch.Elapsed.TotalSeconds

    # ── 4. 打包与校验和 ───────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "打包 + SHA256"
    $stopwatch.Restart()

    $assetName = "onecode-$Version-$Runtime.zip"
    $assetPath = Join-Path $ReleaseDir $assetName
    Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $assetPath -CompressionLevel Optimal

    $assetHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$assetHash  $assetName" | Set-Content -LiteralPath "$assetPath.sha256" -Encoding ascii -NoNewline
    $assetSizeMb = [math]::Round((Get-Item -LiteralPath $assetPath).Length / 1MB, 1)
    Write-Ok "$assetName（$assetSizeMb MB）"
    Write-Ok "sha256 $assetHash"
    Add-Step "打包" $stopwatch.Elapsed.TotalSeconds

    # ── 5. 本地发布源 ─────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "启动本地发布源"
    $stopwatch.Restart()

    $tag = "v$Version"
    $server = Start-ReleaseServer -Root $ReleaseDir -Tag $tag
    Write-Ok "http://127.0.0.1:$($server.Port)（latest API / 302 / download）"
    Add-Step "本地源" $stopwatch.Elapsed.TotalSeconds

    # ── 6. 配置注入 ───────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "生成注入副本（配置块四行）"
    $stopwatch.Restart()

    $sourceText = Get-Content -LiteralPath $InstallerScript -Raw
    $injectedPath = Join-Path $PayloadDir "install-injected.ps1"
    $replacementCount = New-LocalInstaller -SourceText $sourceText -TargetPath $injectedPath `
        -Port $server.Port -TargetInstallDir $InstallDir
    Write-Ok "已替换 $replacementCount 处配置，还原比对通过"
    Add-Step "配置注入" $stopwatch.Elapsed.TotalSeconds

    # ── 7. PATH 拼接规则（纯函数，不写注册表） ────────────────────
    $globalStep++
    Write-Phase $globalStep "PATH 拼接规则（纯函数，不触碰用户环境）"
    $stopwatch.Restart()

    # 从 install.ps1 原文提取函数定义并就地执行：测的是生产代码本身，不是抄来的副本。
    $installerAst = [System.Management.Automation.Language.Parser]::ParseInput(
        $sourceText, $InstallerScript, [ref]$null, [ref]$null)
    $pathFunctionAst = $installerAst.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-UserPathWithInstallDir'
        }, $true)
    if ($null -eq $pathFunctionAst) {
        throw "install.ps1 中找不到 Get-UserPathWithInstallDir（PATH 拼接逻辑必须在纯函数中）"
    }
    Invoke-Expression $pathFunctionAst.Extent.Text

    # 纯函数不得读写环境：一旦混入注册表或会话变量，这层验证就失去意义。
    foreach ($forbidden in @('SetEnvironmentVariable', 'GetEnvironmentVariable', '$env:Path')) {
        if ($pathFunctionAst.Extent.Text.Contains($forbidden)) {
            throw "Get-UserPathWithInstallDir 含有副作用调用 [$forbidden]，PATH 拼接必须保持纯函数"
        }
    }

    $pathProbeDir = 'C:\Probe\onecode\bin'
    $pathCases = @(
        [pscustomobject]@{
            Name = '追加到非空 PATH 前置'; Current = 'C:\a;C:\b'; Expect = "C:\Probe\onecode\bin;C:\a;C:\b"; Present = $false
        }
        [pscustomobject]@{ Name = 'PATH 为空字符串'; Current = ''; Expect = 'C:\Probe\onecode\bin'; Present = $false }
        [pscustomobject]@{ Name = 'PATH 为 null'; Current = $null; Expect = 'C:\Probe\onecode\bin'; Present = $false }
        [pscustomobject]@{ Name = '已存在（精确匹配）'; Current = 'C:\Probe\onecode\bin;C:\a'; Expect = 'C:\Probe\onecode\bin;C:\a'; Present = $true }
        [pscustomobject]@{ Name = '已存在（大小写 + 尾斜杠）'; Current = 'C:\PROBE\OneCode\BIN\;C:\a'; Expect = 'C:\PROBE\OneCode\BIN\;C:\a'; Present = $true }
        [pscustomobject]@{ Name = '保留既有空项不做重排'; Current = 'C:\a;;C:\b'; Expect = "C:\Probe\onecode\bin;C:\a;;C:\b"; Present = $false }
        [pscustomobject]@{ Name = '仅前缀相同不算命中'; Current = 'C:\Probe\onecode\bin\sub'; Expect = "C:\Probe\onecode\bin;C:\Probe\onecode\bin\sub"; Present = $false }
    )

    foreach ($case in $pathCases) {
        $result = Get-UserPathWithInstallDir -CurrentPath $case.Current -Directory $pathProbeDir
        if (-not [string]::Equals($result.Path, $case.Expect, [StringComparison]::Ordinal)) {
            throw "PATH 拼接用例「$($case.Name)」不符：期望 [$($case.Expect)]，实际 [$($result.Path)]"
        }
        if ($result.AlreadyPresent -ne $case.Present) {
            throw "PATH 去重用例「$($case.Name)」判定不符：期望 AlreadyPresent=$($case.Present)，实际 $($result.AlreadyPresent)"
        }
    }
    Write-Ok "$($pathCases.Count) 个拼接/去重用例通过（未读写注册表与会话 PATH）"
    Add-Step "PATH 拼接" $stopwatch.Elapsed.TotalSeconds

    # ── 8. PATH 注册表读写（临时键，不触碰真实用户环境） ──────────
    $globalStep++
    Write-Phase $globalStep "PATH 注册表读写（临时键，保留原类型）"
    $stopwatch.Restart()

    foreach ($functionName in @('Get-UserPathEntry', 'Set-UserPathEntry')) {
        $functionAst = $installerAst.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
            }, $true)
        if ($null -eq $functionAst) {
            throw "install.ps1 中找不到 $functionName（PATH 注册表读写必须可独立验证）"
        }
        Invoke-Expression $functionAst.Extent.Text

        # 读取侧一旦改回 [Environment]::GetEnvironmentVariable，%VAR% 会在读时就展开，
        # 后面的类型保留便无从谈起。这条守卫防止该实现回退。
        if ($functionName -eq 'Get-UserPathEntry' -and
            $functionAst.Extent.Text.Contains('[Environment]::GetEnvironmentVariable')) {
            throw "Get-UserPathEntry 使用了 [Environment]::GetEnvironmentVariable：它会展开 %VAR% 并导致固化，必须直读注册表"
        }
    }

    # 用一次性子键承载读写：真实 HKCU:\Environment 全程不参与。
    $probeKey = "OneCodeE2EProbe-$(Get-Random)"
    $probeValue = 'Path'
    try {
        $registryCases = @(
            [pscustomobject]@{
                Name = 'ExpandString 保留 %VAR%'
                Raw = '%USERPROFILE%\bin;C:\Windows'
                Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
                MustContain = '%USERPROFILE%'
            }
            [pscustomobject]@{
                Name = 'String 不被升级回 ExpandString'
                Raw = 'C:\a;C:\b'
                Kind = [Microsoft.Win32.RegistryValueKind]::String
                MustContain = $null
            }
            [pscustomobject]@{
                Name = 'PathVariable 类型同样保留'
                Raw = 'C:\c'
                Kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
                MustContain = $null
            }
        )

        foreach ($case in $registryCases) {
            Set-UserPathEntry -SubKey $probeKey -Name $probeValue -Value $case.Raw -Kind $case.Kind -Notify $false

            $readBack = Get-UserPathEntry -SubKey $probeKey -Name $probeValue
            if ($readBack.Kind -ne $case.Kind) {
                throw "PATH 注册表用例「$($case.Name)」类型读取不符：期望 $($case.Kind)，实际 $($readBack.Kind)"
            }
            if (-not [string]::Equals($readBack.Value, $case.Raw, [StringComparison]::Ordinal)) {
                throw "PATH 注册表用例「$($case.Name)」原文读取不符：期望 [$($case.Raw)]，实际 [$($readBack.Value)]"
            }

            $computed = Get-UserPathWithInstallDir -CurrentPath $readBack.Value -Directory $pathProbeDir
            Set-UserPathEntry -SubKey $probeKey -Name $probeValue -Value $computed.Path -Kind $readBack.Kind -Notify $false

            $afterWrite = Get-UserPathEntry -SubKey $probeKey -Name $probeValue
            if ($afterWrite.Kind -ne $case.Kind) {
                throw "PATH 注册表用例「$($case.Name)」写入后类型降级：期望 $($case.Kind)，实际 $($afterWrite.Kind)。" +
                "类型降级会让 PATH 中的 %VAR% 永久固化为字面路径。"
            }
            if (-not [string]::Equals($afterWrite.Value, $computed.Path, [StringComparison]::Ordinal)) {
                throw "PATH 注册表用例「$($case.Name)」写入后原文不符：期望 [$($computed.Path)]，实际 [$($afterWrite.Value)]"
            }
            if ($null -ne $case.MustContain -and -not $afterWrite.Value.Contains($case.MustContain)) {
                throw "PATH 注册表用例「$($case.Name)」变量被展开固化：写入后丢失 [$($case.MustContain)]，实际 [$($afterWrite.Value)]"
            }
        }

        # 值不存在时须回退到 ExpandString，否则首次安装会写出 String 类型。
        $missing = Get-UserPathEntry -SubKey $probeKey -Name 'NoSuchPathValue'
        if ($missing.Kind -ne [Microsoft.Win32.RegistryValueKind]::ExpandString) {
            throw "PATH 缺失值默认类型不符：期望 ExpandString，实际 $($missing.Kind)"
        }
        if ($null -ne $missing.Value) {
            throw "PATH 缺失值应返回空值，实际 [$($missing.Value)]"
        }
    }
    finally {
        Remove-Item -Path "HKCU:\$probeKey" -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Ok "$($registryCases.Count) 个注册表类型用例 + 缺失值默认通过（临时键已删除）"
    Add-Step "PATH 注册表" $stopwatch.Elapsed.TotalSeconds

    # ── 9. 安装 ───────────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "安装（下载 → 校验 → 解压 → 目录落位）"
    $stopwatch.Restart()

    $install = Invoke-PwshScript -ScriptPath $injectedPath -LogPath $InstallLog
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
    Write-Ok "onecode.exe + prompts 已落位"
    Add-Step "安装" $stopwatch.Elapsed.TotalSeconds

    # ── 10. 安装后契约 ─────────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "安装后启动契约"
    $stopwatch.Restart()

    $contract = Invoke-PwshScript -ScriptPath $ContractScript -ScriptArguments @(
        "-ExePath", $installedExe, "-ExpectedVersion", $Version
    ) -LogPath (Join-Path $RunDir "contract.log")
    Write-Host $contract.Combined
    if ($contract.ExitCode -ne 0) {
        throw "安装后启动契约失败（退出码 $($contract.ExitCode)）"
    }
    Add-Step "安装后契约" $stopwatch.Elapsed.TotalSeconds

    # ── 11. 重复安装（覆盖） ──────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "重复安装（覆盖同一目录）"
    $stopwatch.Restart()

    $reinstall = Invoke-PwshScript -ScriptPath $injectedPath -LogPath (Join-Path $RunDir "install-reinstall.log")
    if ($reinstall.ExitCode -ne 0) {
        Write-Host $reinstall.Combined
        throw "重复安装失败（退出码 $($reinstall.ExitCode)）"
    }
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw "重复安装后 onecode.exe 缺失"
    }
    if (Test-Path -LiteralPath (Join-Path $InstallDir "OneCode.Tui.exe")) {
        throw "重复安装后残留 OneCode.Tui.exe（改名未生效）"
    }
    Write-Ok "覆盖安装成功，无改名残留"
    Add-Step "重复安装" $stopwatch.Elapsed.TotalSeconds

    # ── 12. 篡改校验和（负向） ────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "负向用例：篡改 SHA256 必须失败"
    $stopwatch.Restart()

    $tamperedInjectedPath = Join-Path $PayloadDir "install-tampered.ps1"
    $null = New-LocalInstaller -SourceText $sourceText -TargetPath $tamperedInjectedPath `
        -Port $server.Port -TargetInstallDir $TamperedInstallDir

    $TamperPath = "$assetPath.sha256"
    $TamperBackup = Get-Content -LiteralPath $TamperPath -Raw
    $bogusHash = -join (1..64 | ForEach-Object { '0' })
    "$bogusHash  $assetName" | Set-Content -LiteralPath $TamperPath -Encoding ascii -NoNewline

    $tampered = Invoke-PwshScript -ScriptPath $tamperedInjectedPath -LogPath (Join-Path $RunDir "install-tampered.log")
    if ($tampered.ExitCode -eq 0) {
        throw "篡改校验和后安装仍然成功（校验链路未生效）"
    }
    if ($tampered.Combined -notmatch 'SHA256 校验失败') {
        throw "篡改校验和后未输出预期的失败原因，实际输出尾部: $(($tampered.Combined -split "`n" | Select-Object -Last 5) -join ' | ')"
    }
    if (Test-Path -LiteralPath (Join-Path $TamperedInstallDir "onecode.exe")) {
        throw "校验失败后仍写出了 onecode.exe"
    }

    Set-Content -LiteralPath $TamperPath -Value $TamperBackup -Encoding ascii -NoNewline
    $TamperBackup = $null
    Write-Ok "校验失败按预期终止，未产出可执行文件"
    Add-Step "篡改校验和" $stopwatch.Elapsed.TotalSeconds

    # ── 13. 302 回退（API 不可用） ───────────────────────────────
    $globalStep++
    Write-Phase $globalStep "回退用例：API 503 时走 /releases/latest 重定向"
    $stopwatch.Restart()

    $fallbackServer = Start-ReleaseServer -Root $ReleaseDir -Tag $tag -FailApi
    $fallbackInjectedPath = Join-Path $PayloadDir "install-fallback.ps1"
    $null = New-LocalInstaller -SourceText $sourceText -TargetPath $fallbackInjectedPath `
        -Port $fallbackServer.Port -TargetInstallDir $FallbackInstallDir

    $fallback = Invoke-PwshScript -ScriptPath $fallbackInjectedPath -LogPath (Join-Path $RunDir "install-fallback.log")
    Write-Host $fallback.Combined
    if ($fallback.ExitCode -ne 0) {
        throw "302 回退安装失败（退出码 $($fallback.ExitCode)）"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $FallbackInstallDir "onecode.exe") -PathType Leaf)) {
        throw "302 回退安装未产出 onecode.exe"
    }
    Write-Ok "API 503 后经 302 回退成功安装"
    Add-Step "302 回退" $stopwatch.Elapsed.TotalSeconds

    # ── 14. 环境完整性 ───────────────────────────────────────────
    $globalStep++
    Write-Phase $globalStep "环境完整性"
    $stopwatch.Restart()

    $pathHashAfter = Get-UserPathHash
    if (-not [string]::Equals($pathHashBefore, $pathHashAfter, [System.StringComparison]::Ordinal)) {
        throw "用户 PATH 被修改（前后哈希不一致）"
    }
    Write-Ok "用户 PATH 未被修改"
    Add-Step "环境完整性" $stopwatch.Elapsed.TotalSeconds
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
finally {
    Stop-ReleaseServers
    if ($null -ne $TamperBackup -and $null -ne $TamperPath) {
        try {
            Set-Content -LiteralPath $TamperPath -Value $TamperBackup -Encoding ascii -NoNewline
        }
        catch {
            Write-Host "  ! 未能恢复被篡改的校验和文件: $TamperPath" -ForegroundColor Yellow
        }
    }
}

# ── 汇总 ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  ✅ 本地发布端到端验证通过                        ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
$totalSeconds = 0.0
foreach ($step in $Steps) {
    Write-Host ("  {0,-12} {1,7:F1}s" -f $step.Name, $step.Seconds) -ForegroundColor White
    $totalSeconds += $step.Seconds
}
Write-Host ("  {0,-12} {1,7:F1}s" -f "合计", $totalSeconds) -ForegroundColor Yellow
Write-Host ""
Write-Host "  产物与日志: $RunDir" -ForegroundColor Gray
Write-Host "  下次回归:   ./scripts/local-release-e2e.ps1 -SkipPublish" -ForegroundColor Gray
Write-Host ""
