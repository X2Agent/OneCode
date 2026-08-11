namespace OneCode.App.Services.Notifier;

public interface INotifierService
{
    Task SendNotificationAsync(string title, string message, CancellationToken ct = default);
    bool IsSupported { get; }
}

/// <summary>
/// 跨平台桌面通知服务：Windows Toast / macOS osascript / Linux notify-send。
///
/// 与 hooks.json 配置的外部通知（飞书/企业微信）互补并存——hook 通知走外部 SaaS，
/// 本服务走本地 OS 桌面通知，适合"长任务完成"等本地用户体验场景。
/// </summary>
public sealed class NotifierService : INotifierService
{
    private readonly ILogger<NotifierService>? _logger;

    public NotifierService(ILogger<NotifierService>? logger = null)
    {
        _logger = logger;
    }

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public async Task SendNotificationAsync(string title, string message, CancellationToken ct = default)
    {
        if (!IsSupported) return;

        try
        {
            if (OperatingSystem.IsWindows())
                await SendWindowsNotificationAsync(title, message, ct).ConfigureAwait(false);
            else if (OperatingSystem.IsMacOS())
                await SendMacOsNotificationAsync(title, message, ct).ConfigureAwait(false);
            else if (OperatingSystem.IsLinux())
                await SendLinuxNotificationAsync(title, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send desktop notification: {Title}", title);
        }
    }

    private static async Task SendWindowsNotificationAsync(string title, string message, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            $"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
            $"$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02); " +
            $"$texts = $template.GetElementsByTagName('text'); $texts[0].AppendChild($template.CreateTextNode('{Escape(title)}')) | Out-Null; " +
            $"$texts[1].AppendChild($template.CreateTextNode('{Escape(message)}')) | Out-Null; " +
            $"$toast = [Windows.UI.Notifications.ToastNotification]::new($template); " +
            $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('OneCode').Show($toast);");

        using var proc = Process.Start(psi);
        if (proc != null)
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    private static async Task SendMacOsNotificationAsync(string title, string message, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"");

        using var proc = Process.Start(psi);
        if (proc != null)
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    private static async Task SendLinuxNotificationAsync(string title, string message, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "notify-send",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Escape(title));
        psi.ArgumentList.Add(Escape(message));

        using var proc = Process.Start(psi);
        if (proc != null)
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    private static string Escape(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("$", "\\$");
    }
}
