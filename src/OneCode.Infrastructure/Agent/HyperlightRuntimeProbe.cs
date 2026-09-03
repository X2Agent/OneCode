namespace OneCode.Infrastructure.Agent;

/// <summary>
/// 探测宿主是否具备 Hyperlight 沙箱所需的硬件虚拟化运行时。
/// </summary>
public interface IHyperlightRuntimeProbe
{
    /// <summary>宿主是否可用 Hyperlight 沙箱（Windows Hypervisor Platform / KVM）。</summary>
    bool IsAvailable();
}

/// <summary>
/// 轻量运行时探测：Windows 检查 Windows Hypervisor Platform（WHP），Linux 检查 KVM/mshv。
/// 不构造沙箱 VM，避免触发 Hyperlight SDK 的初始化开销。
///
/// <para>
/// 探测为启发式（只判断虚拟化平台是否就绪）：嵌套虚拟化、guest 模块缺失等
/// 更深层失败仍会在首次 <c>execute_code</c> 时暴露，但本探测已覆盖最常见的
/// 「运行时完全不可用」场景，使 <see cref="HyperlightCodeActService"/> 的
/// 静默降级得以真实生效。
/// </para>
/// </summary>
public sealed class HyperlightRuntimeProbe : IHyperlightRuntimeProbe
{
    private readonly Lazy<bool> _available = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsAvailable() => _available.Value;

    private static bool Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            // WHP 特性启用后会安装 WinHvPlatform.dll（Windows Hypervisor Platform API）。
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return File.Exists(Path.Combine(systemDir, "WinHvPlatform.dll"));
        }

        if (OperatingSystem.IsLinux())
        {
            return File.Exists("/dev/kvm") || File.Exists("/dev/mshv");
        }

        return false;
    }
}
