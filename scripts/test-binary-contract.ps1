#!/usr/bin/env pwsh
<#
.SYNOPSIS
    OneCode 可执行文件启动契约断言。
.DESCRIPTION
    对任意一份 OneCode 产物执行启动层断言，不依赖 TTY:
      1. --version / -v / -V 命中零 DI 快路径:退出码 0、stdout 形如 "1.0.0 (OneCode)"、stderr 为空
      2. --cwd / -C 与快路径组合可用
      3. --cwd 用法错误(缺参、空路径、目录不存在):退出码 2、只写 stderr、stdout 为空

    发布冒烟(scripts/smoke-test-release.ps1)与本地端到端验证(scripts/local-release-e2e.ps1)
    共用本脚本,使「发布目录里的产物」与「安装后的 onecode.exe」接受完全相同的断言。
.PARAMETER ExePath
    被测可执行文件路径。
.PARAMETER ExpectedVersion
    期望的语义版本号(不带 v 前缀,如 1.0.0)。
.EXAMPLE
    ./test-binary-contract.ps1 -ExePath ./artifacts/publish/win-x64/OneCode.Tui.exe -ExpectedVersion 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"

# ── 辅助函数 ──────────────────────────────────────────────────────
function Write-Step { param($Message) Write-Host "  ▸ $Message" -ForegroundColor Cyan }
function Write-Pass { param($Message) Write-Host "  ✓ $Message" -ForegroundColor Green }
function Write-Fail { param($Message) Write-Host "  ✗ $Message" -ForegroundColor Red }

function Get-CommandLine {
    # Start 只接受一整条命令行字符串,含空白或引号的参数必须自行加引号,
    # 否则路径带空格时会被拆成多个参数。
    param([string[]]$Arguments)

    ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_ + '"' } else { $_ } }) -join ' '
}

function ConvertFrom-ProcessBytes {
    # 被测进程的控制台输出编码跟随宿主代码页:同一个 exe 在 UTF-8 控制台里写 UTF-8,
    # 在中文代码页里写 GBK。按 UTF-8 解码出现替换字符即说明字节不是 UTF-8,回退 ANSI 代码页。
    param([byte[]]$Bytes)

    if ($null -eq $Bytes -or $Bytes.Length -eq 0) { return "" }
    $utf8 = [System.Text.Encoding]::UTF8.GetString($Bytes)
    if ($utf8.IndexOf([char]0xFFFD) -lt 0) { return $utf8 }
    return [System.Text.Encoding]::GetEncoding(0).GetString($Bytes)
}

function Invoke-OneCodeBinary {
    param([string[]]$Arguments)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExePath
    $startInfo.Arguments = Get-CommandLine $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8

    $proc = [System.Diagnostics.Process]::Start($startInfo)
    # 两个流都必须异步读:顺序 ReadToEnd 会在子进程写满缓冲区时死锁。
    # stderr 取原始字节而不是经过 StreamReader,才能按实际编码解码。
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errBuffer = [System.IO.MemoryStream]::new()
    $errCopy = $proc.StandardError.BaseStream.CopyToAsync($errBuffer)
    $proc.WaitForExit()
    $errCopy.GetAwaiter().GetResult() | Out-Null

    [pscustomobject]@{
        ExitCode = $proc.ExitCode
        StdOut   = $outTask.Result
        StdErr   = ConvertFrom-ProcessBytes $errBuffer.ToArray()
    }
}

# ── 入口校验 ──────────────────────────────────────────────────────
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Fail "找不到可执行文件: $ExePath"
    exit 1
}

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$probeDir = Split-Path -Parent $ExePath
$missingDir = Join-Path $probeDir ("__missing__" + [guid]::NewGuid().ToString("N"))

# 形状允许 semver 预发布后缀与构建元数据(如 1.0.0-beta.1+abc1234 (OneCode)):
# SDK 会把源码版本号拼进 InformationalVersion,断言不能因此误报。
$versionShape = '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?(\+[0-9A-Za-z.-]+)? \(OneCode\)$'

$cases = @(
    @{ Name = "--version 快路径";           Args = @("--version"); Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "-v 短别名";                  Args = @("-v");        Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "-V 短别名";                  Args = @("-V");        Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "--cwd <有效目录> --version"; Args = @("--cwd", $probeDir, "--version"); Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "-C <有效目录> --version";    Args = @("-C", $probeDir, "--version");    Exit = 0; OutLike = $versionShape; Err = $null }
    @{ Name = "--cwd 缺少目录参数";         Args = @("--cwd");              Exit = 2; OutLike = $null; Err = "用法错误" }
    @{ Name = "--cwd= 空路径";              Args = @("--cwd=");             Exit = 2; OutLike = $null; Err = "用法错误" }
    @{ Name = "--cwd <不存在的目录>";       Args = @("--cwd", $missingDir); Exit = 2; OutLike = $null; Err = "目录不存在" }
)

foreach ($case in $cases) {
    Write-Step $case.Name
    $result = Invoke-OneCodeBinary $case.Args

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
