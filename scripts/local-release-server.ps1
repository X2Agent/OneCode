#!/usr/bin/env pwsh
<#
.SYNOPSIS
    本地 GitHub Release 源，供本地发布验证使用。
.DESCRIPTION
    在 127.0.0.1 的随机空闲端口上提供与 GitHub Release 同构的最小 HTTP/1.1 服务：

      GET  /repos/{owner}/{repo}/releases/latest            200 JSON {"tag_name":"v..."}
      HEAD /{owner}/{repo}/releases/latest                  302 Location: /{owner}/{repo}/releases/tag/{tag}
      GET  /{owner}/{repo}/releases/tag/{tag}               200 空响应（重定向落点）
      GET  /{owner}/{repo}/releases/download/{tag}/{asset}  200 二进制 + Content-Length（缺失 404）

    安装脚本存在「API 优先 + 302 回退」两条版本解析路径，线上匿名 API 限流时会走回退，
    因此 -FailApi 让 API 返回 503，用于驱动回退分支。
    就绪后向 stdout 打印 "READY {port}"，调用方据此握手，不靠 sleep 猜端口。

    用 TcpListener 而不是 HttpListener：Windows 非管理员绑定 URL 前缀常因 URL ACL
    抛 HttpListenerException(5)，会迫使验证流程提权；TcpListener 绑定回环地址无此限制。
.PARAMETER Root
    资产所在目录（打包产物 onecode-{版本}-{RID}.zip 及其 .sha256）。
.PARAMETER Tag
    发布的 tag（含 v 前缀，如 v1.0.0-local），与资产名中的版本号对应。
.PARAMETER Owner
    仓库所有者段，默认 X2Agent。
.PARAMETER Repo
    仓库名段，默认 OneCode。
.PARAMETER FailApi
    让 REST API 返回 503，迫使安装脚本走 /releases/latest 的 302 回退路径。
.EXAMPLE
    ./local-release-server.ps1 -Root ./artifacts/e2e/20260101-120000/release -Tag v1.0.0-local
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Root,

    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Owner = "X2Agent",

    [string]$Repo = "OneCode",

    [switch]$FailApi
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    [Console]::Error.WriteLine("资产目录不存在: $Root")
    exit 1
}

$assetRoot = (Resolve-Path -LiteralPath $Root).Path

# ── 响应写出 ──────────────────────────────────────────────────────
function Write-Response {
    param(
        [System.IO.Stream]$Stream,
        [int]$StatusCode,
        [string]$StatusText,
        [hashtable]$Headers = @{},
        [byte[]]$Body = [byte[]]::new(0)
    )

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append("HTTP/1.1 $StatusCode $StatusText`r`n")
    foreach ($name in $Headers.Keys) {
        [void]$builder.Append("${name}: $($Headers[$name])`r`n")
    }
    # 每连接只处理一个请求：安装脚本的请求是串行的，短连接最省事也最不容易挂住。
    [void]$builder.Append("Connection: close`r`n")
    [void]$builder.Append("Content-Length: $($Body.Length)`r`n")
    [void]$builder.Append("`r`n")

    $head = [System.Text.Encoding]::ASCII.GetBytes($builder.ToString())
    $Stream.Write($head, 0, $head.Length)
    if ($Body.Length -gt 0) {
        $Stream.Write($Body, 0, $Body.Length)
    }
    $Stream.Flush()
}

function Read-RequestLine {
    param([System.IO.Stream]$Stream)

    # leaveOpen:关闭读取器不能连带关掉网络流，否则后续无法写响应。
    $reader = [System.IO.StreamReader]::new($Stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
    $requestLine = $reader.ReadLine()
    if ([string]::IsNullOrWhiteSpace($requestLine)) {
        return $null
    }

    # 丢弃请求头；本服务不读请求体（只有 GET/HEAD）。
    while ($null -ne ($line = $reader.ReadLine()) -and $line.Length -gt 0) {
        # 空循环
    }

    $parts = $requestLine.Split(' ')
    if ($parts.Length -lt 2) {
        return $null
    }

    [pscustomobject]@{
        Method = $parts[0].ToUpperInvariant()
        Path   = $parts[1].Split('?')[0]
    }
}

# ── 启动 ──────────────────────────────────────────────────────────
# 绑定端口 0 由系统分配空闲端口：比「先探测再释放」少一个竞态窗口。
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port

[Console]::Out.WriteLine("READY $port")
[Console]::Out.Flush()

$jsonBody = [System.Text.Encoding]::UTF8.GetBytes("{`"tag_name`":`"$Tag`"}")

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $request = Read-RequestLine $stream
            if ($null -eq $request) {
                continue
            }

            $segments = $request.Path.Trim('/').Split('/')
            $method = $request.Method

            # REST API：安装脚本用它取 latest tag。
            $isLatestApi = $segments.Count -eq 5 -and
                $segments[0] -eq 'repos' -and
                $segments[1] -eq $Owner -and
                $segments[2] -eq $Repo -and
                $segments[3] -eq 'releases' -and
                $segments[4] -eq 'latest'

            if ($isLatestApi) {
                if ($FailApi) {
                    Write-Response -Stream $stream -StatusCode 503 -StatusText "Service Unavailable"
                }
                else {
                    Write-Response -Stream $stream -StatusCode 200 -StatusText "OK" `
                        -Headers @{ "Content-Type" = "application/json; charset=utf-8" } -Body $jsonBody
                }
                continue
            }

            $isOwnerRepo = $segments.Count -ge 4 -and
                $segments[0] -eq $Owner -and
                $segments[1] -eq $Repo -and
                $segments[2] -eq 'releases'

            if (-not $isOwnerRepo) {
                Write-Response -Stream $stream -StatusCode 404 -StatusText "Not Found"
                continue
            }

            $action = $segments[3]

            # 302 回退：Location 指向 tag 页，安装脚本从 URL 正则取 tag。
            if ($action -eq 'latest') {
                $location = "/$Owner/$Repo/releases/tag/$Tag"
                Write-Response -Stream $stream -StatusCode 302 -StatusText "Found" `
                    -Headers @{ "Location" = $location }
                continue
            }

            if ($action -eq 'tag' -and $segments.Count -eq 5 -and $segments[4] -eq $Tag) {
                Write-Response -Stream $stream -StatusCode 200 -StatusText "OK"
                continue
            }

            if ($action -eq 'download' -and $segments.Count -eq 6 -and $segments[4] -eq $Tag) {
                if ($method -ne 'GET') {
                    Write-Response -Stream $stream -StatusCode 405 -StatusText "Method Not Allowed"
                    continue
                }

                $assetName = [System.Uri]::UnescapeDataString($segments[5])
                $assetPath = Join-Path $assetRoot $assetName
                # 只允许取本目录下的常规文件，避免 ../ 之类的路径逃逸。
                $isDirectChild = [string]::Equals(
                    [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($assetPath)),
                    $assetRoot,
                    [System.StringComparison]::OrdinalIgnoreCase)

                if (-not $isDirectChild -or -not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
                    Write-Response -Stream $stream -StatusCode 404 -StatusText "Not Found"
                    continue
                }

                $bytes = [System.IO.File]::ReadAllBytes($assetPath)
                Write-Response -Stream $stream -StatusCode 200 -StatusText "OK" `
                    -Headers @{ "Content-Type" = "application/octet-stream" } -Body $bytes
                continue
            }

            Write-Response -Stream $stream -StatusCode 404 -StatusText "Not Found"
        }
        catch {
            # 单个连接异常（客户端提前断开等）不应终止服务。
            [Console]::Error.WriteLine("请求处理失败: $($_.Exception.Message)")
        }
        finally {
            $client.Close()
        }
    }
}
finally {
    $listener.Stop()
}
