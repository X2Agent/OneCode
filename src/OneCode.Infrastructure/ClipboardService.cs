using OneCode.Core.IO;

namespace OneCode.Infrastructure;

/// <summary>
/// 跨平台剪贴板服务实现。
///
/// 合并自历史上的两个 ClipboardHelper：
/// - 原 Commands/ClipboardHelper（实例类，仅文本写入，Windows 用 clip 命令存在 GBK 乱码）
/// - 原 Tui/ClipboardHelper（静态类，文本+图像+文件，Windows 用 PowerShell+UTF-8 临时文件）
///
/// 现统一采用 Tui 版本的实现策略：
/// - Windows 文本写入：PowerShell + UTF-8 临时文件（解决 OEM 编码乱码）
/// - Unix 文本写入：pbcopy / wl-copy / xclip（stdin 使用 UTF-8 编码）
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    private static readonly (string Cmd, string Args)[] LinuxCopyTools =
    [
        ("wl-copy",  ""),
        ("xclip",    "-selection clipboard"),
        ("xsel",     "--clipboard --input"),
    ];

    private readonly ILogger<ClipboardService> _logger;

    public ClipboardService(ILogger<ClipboardService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string?> TryCopyTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        try
        {
            if (OperatingSystem.IsWindows())
                return await CopyWindowsAsync(text, ct).ConfigureAwait(false);

            return await CopyUnixAsync(text, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClipboardService.TryCopyTextAsync failed");
            return ex.Message;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetTextAsync(CancellationToken ct = default)
    {
        try
        {
            var (fileName, args) = GetPasteCommand();
            if (fileName is null)
                return null;

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 ? output.TrimEnd() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClipboardService.GetTextAsync failed");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<List<string>> GetFilesAsync(CancellationToken ct = default)
    {
        var files = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("Get-Clipboard -Format FileDropList | ForEach-Object { $_.FullName }");

                using var proc = Process.Start(psi);
                if (proc is null) return files;

                var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var path = line.Trim();
                        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                            files.Add(path);
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("try\nset theFiles to (the clipboard as «class furl»)\nrepeat with f in theFiles\nlog POSIX path of f\nend repeat\nend try");

                using var proc = Process.Start(psi);
                if (proc is null) return files;

                var output = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var path = line.Trim();
                    if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                        files.Add(path);
                }
            }
            // Linux: wl-paste --list-types / xclip — file list not universally supported,
            // fall back to text-based path detection in the caller.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClipboardService.GetFilesAsync failed");
        }
        return files;
    }

    /// <inheritdoc />
    public async Task<string?> GetImageAsync(CancellationToken ct = default)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"clipboard_{Guid.NewGuid():N}.png");
                // Add-Type loads System.Windows.Forms which provides Get-Clipboard -Format Image.
                var script = $@"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$img = Get-Clipboard -Format Image
if ($img -ne $null) {{
    $img.Save('{tempPath.Replace("'", "''")}', [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output '{tempPath.Replace("'", "''")}'
}}";
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(script);

                using var proc = Process.Start(psi);
                if (proc is null) return null;

                var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                var result = output.Trim();
                if (proc.ExitCode == 0 && File.Exists(result))
                    return result;
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (IsCommandAvailable("pngpaste"))
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"clipboard_{Guid.NewGuid():N}.png");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pngpaste",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add(tempPath);

                    using var proc = Process.Start(psi);
                    if (proc is null) return null;

                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    if (proc.ExitCode == 0 && File.Exists(tempPath))
                        return tempPath;
                }
            }
            else if (OperatingSystem.IsLinux() && IsCommandAvailable("xclip"))
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"clipboard_{Guid.NewGuid():N}.png");
                var psi = new ProcessStartInfo
                {
                    FileName = "xclip",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-selection");
                psi.ArgumentList.Add("clipboard");
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add("image/png");
                psi.ArgumentList.Add("-o");

                using var proc = Process.Start(psi);
                if (proc is null) return null;

                await using var fs = File.Create(tempPath);
                await proc.StandardOutput.BaseStream.CopyToAsync(fs, ct).ConfigureAwait(false);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                if (proc.ExitCode == 0 && new FileInfo(tempPath).Length > 0)
                    return tempPath;

                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClipboardService.GetImageAsync failed");
        }
        return null;
    }

    /// <summary>
    /// Windows 剪贴板写入：通过 UTF-8 临时文件 + PowerShell Get-Content | Set-Clipboard。
    ///
    /// 不能直接管道到 PowerShell stdin，因为 Windows PowerShell 5.1 使用系统 OEM 编码
    /// （如中文 Windows 的 GBK/936）读取 stdin，会乱码非 ASCII 字符。
    /// </summary>
    private async Task<string?> CopyWindowsAsync(string text, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"onecode_clip_{Guid.NewGuid():N}.txt");
        // BOM-less UTF-8 — PowerShell Get-Content -Encoding UTF8 兼容有无 BOM
        await File.WriteAllTextAsync(tempFile, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            // -Raw preserves newlines and reads as a single string
            psi.ArgumentList.Add($"Get-Content -LiteralPath '{tempFile}' -Encoding UTF8 -Raw | Set-Clipboard");

            using var proc = Process.Start(psi);
            if (proc is null) return "Failed to start powershell.exe";
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 ? null : $"PowerShell exit code {proc.ExitCode}";
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch (Exception ex) { _logger.LogWarning(ex, "ClipboardService failed to delete temp file {Path}", tempFile); }
        }
    }

    private async Task<string?> CopyUnixAsync(string text, CancellationToken ct)
    {
        var (fileName, args) = GetCopyCommand();
        if (fileName is null)
            return "No clipboard tool found. Install wl-clipboard, xclip, or xsel.";

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return $"Failed to start {fileName}";

        await proc.StandardInput.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
        proc.StandardInput.Close();

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode == 0 ? null : $"{fileName} exit code {proc.ExitCode}";
    }

    private static (string? fileName, string[] args) GetCopyCommand()
    {
        if (OperatingSystem.IsMacOS())
            return ("pbcopy", []);
        if (OperatingSystem.IsLinux())
        {
            foreach (var (cmd, cmdArgs) in LinuxCopyTools)
                if (IsCommandAvailable(cmd))
                    return (cmd, cmdArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return (null, []);
    }

    private static (string? fileName, string[] args) GetPasteCommand()
    {
        if (OperatingSystem.IsWindows())
            return ("powershell.exe", ["-NoProfile", "-Command", "Get-Clipboard"]);
        if (OperatingSystem.IsMacOS())
            return ("pbpaste", []);
        if (OperatingSystem.IsLinux())
        {
            if (IsCommandAvailable("wl-paste"))
                return ("wl-paste", []);
            if (IsCommandAvailable("xclip"))
                return ("xclip", ["-selection", "clipboard", "-o"]);
        }
        return (null, []);
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                ArgumentList = { command },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // Best-effort probe — log at Debug to keep the trace without spamming warnings.
            System.Diagnostics.Debug.WriteLine($"IsCommandAvailable({command}) probe failed: {ex.Message}");
            return false;
        }
    }
}
