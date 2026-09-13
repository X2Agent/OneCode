#Requires -Version 7
<#
.SYNOPSIS
    校验 docs/commands.md 与斜杠命令注册表（CommandServiceExtensions.AddCommands）同步。
.DESCRIPTION
    真相源：src/OneCode.App/Commands/CommandServiceCollectionExtensions.cs 中 AddSingleton<ICommand, XxxCommand> 注册。
    脚本从各命令源码解析 Name => "xxx"，与 docs/commands.md 中的 "### /xxx" 标题集合做 diff。
    不一致时退出码为 1，供 CI / release workflow 调用。
#>
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
$registryPath = Join-Path $RepoRoot 'src/OneCode.App/Commands/CommandServiceCollectionExtensions.cs'
$commandsDir  = Join-Path $RepoRoot 'src/OneCode.App/Commands'
$docsPath     = Join-Path $RepoRoot 'docs/commands.md'

# 1. 从 DI 注册表提取命令类名（直接类型注册 + 工厂转发两种模式）
$registryText = Get-Content $registryPath -Raw
$classes = @(
    [regex]::Matches($registryText, 'AddSingleton<ICommand,\s*(\w+)>') | ForEach-Object { $_.Groups[1].Value }
    [regex]::Matches($registryText, 'GetRequiredService<(\w+)>')       | ForEach-Object { $_.Groups[1].Value }
) | Select-Object -Unique
if (-not $classes) { Write-Error "未在 $registryPath 中找到任何命令注册"; exit 1 }

# 2. 解析每个命令类的 Name 属性（支持主构造函数类与表达式体属性）
$registered = foreach ($cls in $classes) {
    $file = Join-Path $commandsDir "$cls.cs"
    if (-not (Test-Path $file)) {
        # 类可能定义在共享文件中（如 DebugCommands.cs），按类名全局搜索
        $hit = Get-ChildItem $commandsDir -Filter *.cs |
            Select-String -Pattern ("class\s+" + $cls + "\b") | Select-Object -First 1
        if ($hit) { $file = $hit.Path }
        else { Write-Warning "找不到命令源文件：$cls"; continue }
    }
    $text = Get-Content $file -Raw
    $m = [regex]::Match($text, '(?:public\s+)?override\s+string\??\s+Name\s*=>\s*"([^"]+)"')
    if ($m.Success) { $m.Groups[1].Value }
    else { Write-Warning "无法在 $cls.cs 中解析 Name 属性"; ($cls -replace 'Command$', '').ToLowerInvariant() }
}
$registered = $registered | Sort-Object -Unique

# 3. 提取 docs 中记录的命令标题
$docsText = Get-Content $docsPath -Raw
$documented = [regex]::Matches($docsText, '^### /([a-z0-9-]+)', 'Multiline') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

# 4. 双向 diff（隐藏命令如 gc-stats 也应记录，便于审计）
$missingInDocs = $registered | Where-Object { $_ -notin $documented }
$staleInDocs   = $documented | Where-Object { $_ -notin $registered }

if ($missingInDocs -or $staleInDocs) {
    if ($missingInDocs) {
        Write-Host "❌ 已注册但 docs/commands.md 缺少章节：" -ForegroundColor Red
        $missingInDocs | ForEach-Object { Write-Host "   /$_" }
    }
    if ($staleInDocs) {
        Write-Host "❌ docs/commands.md 记录了已不存在的命令：" -ForegroundColor Red
        $staleInDocs | ForEach-Object { Write-Host "   /$_" }
    }
    Write-Host "`n请在 src/OneCode.App/Commands/CommandServiceCollectionExtensions.cs 与 docs/commands.md 之间同步。" -ForegroundColor Yellow
    exit 1
}

Write-Host "✓ docs/commands.md 与命令注册表一致（共 $($registered.Count) 个命令）" -ForegroundColor Green
