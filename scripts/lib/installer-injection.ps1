#!/usr/bin/env pwsh
<#
.SYNOPSIS
    install.ps1 配置注入与用户环境指纹的共享实现。
.DESCRIPTION
    由 scripts/local-release-e2e.ps1（本地发布源）与 scripts/verify-github-release.ps1（真实 Release）
    共同 dot-source，使两条验证路径的注入安全规则只有一份实现：

      - 锚点必须命中且**唯一**：配置块结构漂移时立刻失败，不静默替换错位置；
      - 注入后**还原比对**：逐条退回注入值后必须与源文本逐字符相等，证明没有波及其他逻辑。

    注入只改配置块的字面量，生产逻辑（下载、SHA256 校验、解压、落位、PATH 写入）逐字符保持原样。
#>

# ── 注入核心 ──────────────────────────────────────────────────────
function New-InjectedText {
    param(
        [Parameter(Mandatory = $true)][string]$SourceText,
        [Parameter(Mandatory = $true)][object[]]$Edits,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $resolved = foreach ($edit in $Edits) {
        if ([string]::IsNullOrEmpty($edit.Old)) {
            throw "注入锚点不得为空"
        }
        if ([string]::Equals($edit.Old, $edit.New, [System.StringComparison]::Ordinal)) {
            throw "注入值为空替换（Old 与 New 相同）: [$($edit.Old)]"
        }

        $index = $SourceText.IndexOf($edit.Old, [System.StringComparison]::Ordinal)
        if ($index -lt 0) {
            throw "配置块锚点漂移：未找到 [$($edit.Old)]"
        }
        if ($SourceText.IndexOf($edit.Old, $index + 1, [System.StringComparison]::Ordinal) -ge 0) {
            throw "配置块锚点不唯一：[$($edit.Old)] 出现多次"
        }
        [pscustomobject]@{ Index = $index; Old = $edit.Old; New = $edit.New }
    }

    # 从头拼接重建：替换值长度与锚点不同，不能复用源文本偏移做原地改写。
    $builder = [System.Text.StringBuilder]::new()
    $cursor = 0
    foreach ($edit in ($resolved | Sort-Object Index)) {
        [void]$builder.Append($SourceText.Substring($cursor, $edit.Index - $cursor))
        [void]$builder.Append($edit.New)
        $cursor = $edit.Index + $edit.Old.Length
    }
    [void]$builder.Append($SourceText.Substring($cursor))
    $injected = $builder.ToString()

    # 还原比对：逐条把注入值退回后必须与源文件逐字符相等，证明没有波及其他逻辑。
    $restored = $injected
    foreach ($edit in $resolved) {
        $index = $restored.IndexOf($edit.New, [System.StringComparison]::Ordinal)
        if ($index -lt 0 -or $restored.IndexOf($edit.New, $index + 1, [System.StringComparison]::Ordinal) -ge 0) {
            throw "注入值不唯一，无法还原比对: [$($edit.New)]"
        }
        $restored = $restored.Remove($index, $edit.New.Length).Insert($index, $edit.Old)
    }
    if (-not [string]::Equals($restored, $SourceText, [System.StringComparison]::Ordinal)) {
        throw "注入副本还原后与源文件不一致"
    }

    Set-Content -LiteralPath $TargetPath -Value $injected -Encoding utf8
    return $resolved.Count
}

# ── 安装目录安全 ──────────────────────────────────────────────────
function Assert-SafeInstallDir {
    param([Parameter(Mandatory = $true)][string]$Directory)

    # 注入值会被写进双引号字符串：$ 会破坏求值，引号会截断路径。
    if ($Directory -like '*$*') {
        throw "安装目录不能包含 `$ 字符（会破坏注入副本的字符串求值）: $Directory"
    }
    if ($Directory -like '*`*' -or $Directory -like '*"*') {
        throw "安装目录不能包含反引号或双引号（会破坏注入副本的字符串字面量）: $Directory"
    }
}

# ── 场景一：本地发布源 ────────────────────────────────────────────
function New-LocalInstaller {
    param(
        [Parameter(Mandatory = $true)][string]$SourceText,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$TargetInstallDir
    )

    Assert-SafeInstallDir -Directory $TargetInstallDir

    # 锚点为 install.ps1 配置块的字面量整行：命中数必须恰为 1，否则说明配置块结构变了。
    return New-InjectedText -SourceText $SourceText -TargetPath $TargetPath -Edits @(
        [pscustomobject]@{ Old = '$GitHubBase     = "https://github.com/$RepoOwner/$RepoName"'; New = ('$GitHubBase     = "http://127.0.0.1:{0}/X2Agent/OneCode"' -f $Port) }
        [pscustomobject]@{ Old = '$ApiBase        = "https://api.github.com"';                   New = ('$ApiBase        = "http://127.0.0.1:{0}"' -f $Port) }
        [pscustomobject]@{ Old = '$InstallDir     = Join-Path $env:USERPROFILE ".onecode\bin"';  New = ('$InstallDir     = "{0}"' -f $TargetInstallDir) }
        [pscustomobject]@{ Old = '$WritePathToEnv = $true';                                      New = '$WritePathToEnv = $false' }
    )
}

# ── 场景二：真实 GitHub Release 上的指定 tag ──────────────────────
function New-GitHubTagInstaller {
    param(
        [Parameter(Mandatory = $true)][string]$SourceText,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$TargetInstallDir
    )

    Assert-SafeInstallDir -Directory $TargetInstallDir

    if ($Tag -notmatch '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "tag 不符合发布约定（须带 v 前缀的语义化版本）: $Tag"
    }

    # GitHubBase / ApiBase 保持生产值：这一步验证的正是真实域名上的版本解析与下载。
    # 只把「取 latest」换成固定 tag（预发布不占 /releases/latest，生产安装器按设计取不到它），
    # 并指向临时安装目录、跳过 PATH 写入；其余代码逐字符保持生产版本。
    return New-InjectedText -SourceText $SourceText -TargetPath $TargetPath -Edits @(
        [pscustomobject]@{ Old = '$version = Get-LatestVersion';                                 New = ('$version = "{0}"' -f $Tag) }
        [pscustomobject]@{ Old = '$InstallDir     = Join-Path $env:USERPROFILE ".onecode\bin"';  New = ('$InstallDir     = "{0}"' -f $TargetInstallDir) }
        [pscustomobject]@{ Old = '$WritePathToEnv = $true';                                      New = '$WritePathToEnv = $false' }
    )
}

# ── 环境指纹 ──────────────────────────────────────────────────────
# 用环境变量读取而非注册表原文：这里只做「前后是否被改动」的指纹比对，不参与写回，
# 展开与否在前后两次取值上是一致的。
function Get-UserPathHash {
    $value = [Environment]::GetEnvironmentVariable("Path", "User")
    if ([string]::IsNullOrEmpty($value)) { return "" }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($value))
    }
    finally {
        $sha.Dispose()
    }
    return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
}
