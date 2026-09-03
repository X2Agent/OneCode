// MAAI001 suppressed: HyperlightCodeActProvider integration uses experimental Hyperlight APIs
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hyperlight;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Hyperlight CodeAct 沙箱服务。默认启用——沙箱能用就用，无需配置。
/// 工作目录以只读方式暴露为沙箱 <c>/input</c>（<see cref="HyperlightCodeActProviderOptions.HostInputDirectory"/>），
/// AI 生成的代码可读取工作目录文件；写操作不走沙箱，由宿主 Write/Edit 工具经权限审批完成。
/// 审批模式固定为 AlwaysRequire——沙箱内执行的代码须经过标准 MAF 审批/权限管道。
/// 仅当 Hyperlight 运行时（WHP/KVM）不可用时静默降级（返回 null）。
/// </summary>
public sealed class HyperlightCodeActService(
    ILogger<HyperlightCodeActService> logger,
    IHyperlightRuntimeProbe runtimeProbe)
    : IHyperlightCodeActService
{
    /// <summary>
    /// 尝试创建 HyperlightCodeActProvider。
    /// 无需配置：工作目录只读挂载为 <c>/input</c>，审批模式固定为 AlwaysRequire。
    /// 如果 Hyperlight 运行时不可用（WHP/KVM 缺失），返回 null（调用方静默跳过）。
    /// </summary>
    public AIContextProvider? TryCreateProvider(string workingDirectory)
    {
        if (!runtimeProbe.IsAvailable())
        {
            logger.LogWarning(
                "Hyperlight runtime (WHP/KVM) unavailable — CodeAct sandbox will not be enabled");
            return null;
        }

        try
        {
            return new HyperlightCodeActProvider(BuildOptions(workingDirectory));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create HyperlightCodeActProvider — sandbox will not be available");
            return null;
        }
    }

    /// <summary>
    /// 构建 Hyperlight CodeAct 选项。
    /// 工作目录以只读方式暴露为沙箱 <c>/input</c>（<c>HostInputDirectory</c>），
    /// 不配置 <c>FileMounts</c>（当前 Hyperlight .NET SDK 尚未把 FileMount 接线到底层挂载 API），
    /// 也不向沙箱注入任何宿主工具（<c>execute_code</c> 作为纯代码解释器运行）。
    /// </summary>
    internal static HyperlightCodeActProviderOptions BuildOptions(string workingDirectory)
    {
        var options = new HyperlightCodeActProviderOptions
        {
            ApprovalMode = CodeActApprovalMode.AlwaysRequire,
        };

        if (Directory.Exists(workingDirectory))
        {
            options.HostInputDirectory = workingDirectory;
        }

        return options;
    }
}
