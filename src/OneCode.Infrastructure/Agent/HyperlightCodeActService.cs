// MAAI001 suppressed: HyperlightCodeActProvider integration uses experimental Hyperlight APIs
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hyperlight;
using Microsoft.Extensions.AI;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Hyperlight CodeAct 沙箱服务。默认启用——沙箱能用就用，无需配置。
/// 工作目录自动挂载到沙箱，AI 生成的代码可直接操作工作目录文件。
/// 审批模式固定为 AlwaysRequire——沙箱内执行的代码须经过标准 MAF 审批/权限管道。
/// 仅当 Hyperlight 运行时不可用时静默降级（返回 null）。
/// </summary>
public sealed class HyperlightCodeActService(ILogger<HyperlightCodeActService> logger)
    : IHyperlightCodeActService
{
    /// <summary>
    /// 尝试创建 HyperlightCodeActProvider。
    /// 无需配置：工作目录自动挂载，审批模式固定为 AlwaysRequire（沙箱内代码须经过标准 MAF 审批/权限管道）。
    /// 如果 Hyperlight 运行时不可用，返回 null（调用方静默跳过）。
    /// </summary>
    public AIContextProvider? TryCreateProvider(
        string workingDirectory,
        IReadOnlyList<AIFunction>? sandboxTools = null)
    {
        try
        {
            // Use AlwaysRequire so that sandbox-executed code goes through the
            // standard MAF approval/permission pipeline.
            var options = new HyperlightCodeActProviderOptions
            {
                ApprovalMode = CodeActApprovalMode.AlwaysRequire,
            };

            if (sandboxTools is { Count: > 0 })
                options.Tools = sandboxTools;

            // 自动挂载工作目录——沙箱内代码需能访问工作目录文件
            if (Directory.Exists(workingDirectory))
            {
                var mountPath = workingDirectory.TrimEnd('/', '\\');
                options.FileMounts = [new FileMount(workingDirectory, mountPath)];
            }

            logger.LogInformation(
                "HyperlightCodeActProvider created: tools={ToolCount}, fileMounts={MountCount}",
                sandboxTools?.Count ?? 0,
                options.FileMounts?.Count() ?? 0);

            return new HyperlightCodeActProvider(options);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create HyperlightCodeActProvider — sandbox will not be available");
            return null;
        }
    }
}
