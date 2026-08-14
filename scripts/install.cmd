@echo off
setlocal enabledelayedexpansion

:: OneCode Installer for Windows (CMD)
:: Usage: curl -fsSL https://raw.githubusercontent.com/X2Agent/OneCode/main/scripts/install.cmd -o install.cmd && install.cmd && del install.cmd

echo.
echo ╔══════════════════════════════════════════════════╗
echo ║       OneCode Installer (CMD)                    ║
echo ╚══════════════════════════════════════════════════╝
echo.

:: ── 配置 ──────────────────────────────────────────────────────────
set "REPO_OWNER=X2Agent"
set "REPO_NAME=OneCode"
set "INSTALL_DIR=%USERPROFILE%\.onecode\bin"
set "BINARY_NAME=onecode.exe"

if defined ONECODE_REPO_OWNER set "REPO_OWNER=%ONECODE_REPO_OWNER%"
if defined ONECODE_REPO_NAME  set "REPO_NAME=%ONECODE_REPO_NAME%"
if defined ONECODE_INSTALL_DIR set "INSTALL_DIR=%ONECODE_INSTALL_DIR%"

:: ── 检测架构 ──────────────────────────────────────────────────────
set "ARCH=x64"
if "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "ARCH=arm64"
set "PLATFORM=win-%ARCH%"

echo ▸ 检测平台: %PLATFORM%

:: ── 获取最新版本 ──────────────────────────────────────────────────
set "VERSION="
if defined ONECODE_VERSION (
    set "VERSION=%ONECODE_VERSION%"
    echo ▸ 指定版本: !VERSION!
) else (
    echo ▸ 正在检测最新版本...
    :: 使用 PowerShell 获取 GitHub API；失败时必须中止，禁止静默回退到不存在的版本。
    for /f "delims=" %%v in ('powershell -NoProfile -Command "$headers=@{'Accept'='application/vnd.github+json';'User-Agent'='onecode-installer';'X-GitHub-Api-Version'='2022-11-28'}; (Invoke-RestMethod -Uri 'https://api.github.com/repos/%REPO_OWNER%/%REPO_NAME%/releases/latest' -Headers $headers -ErrorAction Stop).tag_name" 2^>nul') do set "VERSION=%%v"
    if not defined VERSION (
        echo ✗ 无法获取 %REPO_OWNER%/%REPO_NAME% 的最新 Release!
        exit /b 1
    )
    echo ▸ 最新版本: !VERSION!
)
set "ASSET_VERSION=!VERSION!"
if /i "!ASSET_VERSION:~0,1!"=="v" set "ASSET_VERSION=!ASSET_VERSION:~1!"

:: ── 创建安装目录 ──────────────────────────────────────────────────
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

:: ── 下载 ──────────────────────────────────────────────────────────
set "DOWNLOAD_URL=https://github.com/%REPO_OWNER%/%REPO_NAME%/releases/download/!VERSION!/onecode-!ASSET_VERSION!-%PLATFORM%.zip"
set "TEMP_DIR=%TEMP%\onecode-install-%RANDOM%"
set "EXTRACT_DIR=%TEMP_DIR%\payload"
set "ZIP_FILE=%TEMP_DIR%\onecode.zip"
set "CHECKSUM_FILE=%ZIP_FILE%.sha256"

mkdir "%TEMP_DIR%" 2>nul
mkdir "%EXTRACT_DIR%" 2>nul
echo ▸ 下载: %DOWNLOAD_URL%

:: 优先使用 curl（Win10+ 自带）
where curl >nul 2>nul
if %errorlevel%==0 (
    curl -fSL -o "%ZIP_FILE%" "%DOWNLOAD_URL%"
) else (
    :: 回退到 PowerShell
    powershell -NoProfile -Command "Invoke-WebRequest -Uri '%DOWNLOAD_URL%' -OutFile '%ZIP_FILE%' -UseBasicParsing"
)

if not exist "%ZIP_FILE%" (
    echo ✗ 下载失败!
    exit /b 1
)

powershell -NoProfile -Command "Invoke-WebRequest -Uri '%DOWNLOAD_URL%.sha256' -OutFile '%CHECKSUM_FILE%' -UseBasicParsing; $expected=((Get-Content -LiteralPath '%CHECKSUM_FILE%' -Raw).Trim() -split '\s+')[0]; $actual=(Get-FileHash -LiteralPath '%ZIP_FILE%' -Algorithm SHA256).Hash; if (-not [string]::Equals($expected,$actual,[StringComparison]::OrdinalIgnoreCase)) { throw ('SHA256 mismatch: expected ' + $expected + ', actual ' + $actual) }"
if errorlevel 1 (
    echo ✗ SHA256 校验失败!
    exit /b 1
)

:: ── 解压 ──────────────────────────────────────────────────────────
echo ▸ 解压中...
powershell -NoProfile -Command "Expand-Archive -Path '%ZIP_FILE%' -DestinationPath '%EXTRACT_DIR%' -Force"
if errorlevel 1 (
    echo ✗ 解压失败!
    set "INSTALL_FAILED=1"
    goto cleanup
)

:: 发布包包含 prompts、Team YAML 等运行时资源，必须整体安装。
if not exist "%EXTRACT_DIR%\OneCode.Cli.exe" (
    echo ✗ 在归档根目录中找不到 OneCode.Cli.exe!
    set "INSTALL_FAILED=1"
    goto cleanup
)

:: ── 安装 ──────────────────────────────────────────────────────────
xcopy "%EXTRACT_DIR%\*" "%INSTALL_DIR%\" /E /I /Y /Q >nul
if errorlevel 1 (
    echo ✗ 复制发布资源失败!
    set "INSTALL_FAILED=1"
    goto cleanup
)
move /y "%INSTALL_DIR%\OneCode.Cli.exe" "%INSTALL_DIR%\%BINARY_NAME%" >nul
if errorlevel 1 (
    echo ✗ 重命名主程序失败!
    set "INSTALL_FAILED=1"
    goto cleanup
)
echo ✓ 已安装到 %INSTALL_DIR%\%BINARY_NAME%（含运行时资源）

:: ── 配置 PATH ─────────────────────────────────────────────────────
echo %PATH% | findstr /i /c:"%INSTALL_DIR%" >nul
if %errorlevel%==0 (
    echo ▸ PATH 已包含安装目录
) else (
    :: 使用 PowerShell 持久化 PATH
    powershell -NoProfile -Command "[Environment]::SetEnvironmentVariable('Path', '%INSTALL_DIR%;' + [Environment]::GetEnvironmentVariable('Path', 'User'), 'User')"
    echo ✓ 已将 %INSTALL_DIR% 添加到用户 PATH
    :: 更新当前会话
    set "PATH=%INSTALL_DIR%;%PATH%"
)

:: ── 清理 ──────────────────────────────────────────────────────────
:cleanup
rmdir /s /q "%TEMP_DIR%" 2>nul
if defined INSTALL_FAILED exit /b 1

:: ── 完成 ──────────────────────────────────────────────────────────
echo.
echo ╔══════════════════════════════════════════════════╗
echo ║  安装完成!                                        ║
echo ╚══════════════════════════════════════════════════╝
echo.
echo   运行 onecode --help 开始使用
echo   请重新打开终端使 PATH 生效
echo.

endlocal
