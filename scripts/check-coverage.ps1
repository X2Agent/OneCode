#Requires -Version 7
<#
.SYNOPSIS
    校验单元测试覆盖率不低于回归下限，并把各程序集覆盖率打印出来便于人工观察。
.DESCRIPTION
    解析 coverlet 生成的 cobertura 报告，断言总行覆盖率与总分支覆盖率不低于给定下限。
    下限取值是"实测值减约 2 个百分点"的回归护栏，不是质量目标：它只负责拦住倒退，
    不代表某个覆盖率数字就是合格线。质量目标见 src/AGENTS.md。
    低于下限时退出码为 1，供 CI 调用。
.PARAMETER ResultsDirectory
    `dotnet test --collect:"XPlat Code Coverage" --results-directory <dir>` 的输出目录，
    脚本在其中递归查找 coverage.cobertura.xml。
.PARAMETER MinimumLine
    总行覆盖率下限（百分比）。
.PARAMETER MinimumBranch
    总分支覆盖率下限（百分比）。
.EXAMPLE
    ./check-coverage.ps1 -ResultsDirectory ./artifacts/coverage -MinimumLine 60 -MinimumBranch 48
#>
param(
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '../artifacts/coverage'),
    [double]$MinimumLine = 60,
    [double]$MinimumBranch = 48
)

$ErrorActionPreference = 'Stop'

function Write-Fail { param($Message) Write-Host "  ✗ $Message" -ForegroundColor Red }

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  OneCode Coverage Check                           ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  ResultsDirectory: $ResultsDirectory" -ForegroundColor White
Write-Host "  下限: 行 $MinimumLine% / 分支 $MinimumBranch%" -ForegroundColor White
Write-Host ""

if (-not (Test-Path -LiteralPath $ResultsDirectory -PathType Container)) {
    Write-Fail "覆盖率结果目录不存在: $ResultsDirectory"
    exit 1
}

# 报告缺失或出现多份都必须显式失败：把"没跑出覆盖率"当成"通过"是最危险的假绿。
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter 'coverage.cobertura.xml' -File)
if ($reports.Count -eq 0) {
    Write-Fail "未找到 coverage.cobertura.xml（测试是否漏了 --collect 覆盖率收集？）"
    exit 1
}
if ($reports.Count -gt 1) {
    Write-Fail "找到 $($reports.Count) 份覆盖率报告，无法判定应校验哪一份:"
    $reports | ForEach-Object { Write-Host "   $($_.FullName)" }
    exit 1
}

$reportPath = $reports[0].FullName
[xml]$report = Get-Content -LiteralPath $reportPath -Raw
$coverage = $report.coverage

# 属性缺失时不能默认成 0 就算通过，直接判定报告不可信。
$lineRate = $coverage.'line-rate'
$branchRate = $coverage.'branch-rate'
if ([string]::IsNullOrWhiteSpace($lineRate) -or [string]::IsNullOrWhiteSpace($branchRate)) {
    Write-Fail "报告缺少 line-rate/branch-rate 属性，格式不可识别: $reportPath"
    exit 1
}

$linePercent = [double]$lineRate * 100
$branchPercent = [double]$branchRate * 100

# 按覆盖率升序打印：最薄弱的部分应该一眼可见。
$packages = @(
    $coverage.packages.package | ForEach-Object {
        [pscustomobject]@{
            Name   = $_.name
            Line   = [double]$_.'line-rate' * 100
            Branch = [double]$_.'branch-rate' * 100
        }
    }
) | Sort-Object Line

if ($packages.Count -gt 0) {
    foreach ($pkg in $packages) {
        Write-Host ("    {0,-34} 行 {1,6:N2}%  分支 {2,6:N2}%" -f $pkg.Name, $pkg.Line, $pkg.Branch) -ForegroundColor DarkGray
    }
    Write-Host ""
}

Write-Host ("  总计: 行 {0:N2}% (覆盖 {1}/{2} 行)  分支 {3:N2}% (覆盖 {4}/{5} 分支)" -f `
        $linePercent, $coverage.'lines-covered', $coverage.'lines-valid', `
        $branchPercent, $coverage.'branches-covered', $coverage.'branches-valid') -ForegroundColor White
Write-Host ""

if ($linePercent -lt $MinimumLine) {
    Write-Fail "行覆盖率 $([math]::Round($linePercent, 2))% 低于下限 $MinimumLine%"
    exit 1
}
if ($branchPercent -lt $MinimumBranch) {
    Write-Fail "分支覆盖率 $([math]::Round($branchPercent, 2))% 低于下限 $MinimumBranch%"
    exit 1
}

Write-Host "  ✓ 覆盖率达标" -ForegroundColor Green
Write-Host ""
