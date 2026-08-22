using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// 从团队 YAML 成员工具声明推导的真实能力画像。
/// 计划生成（TeamRequirementService）据此选择计划形态：
/// 有写能力的团队走 analysis→implementation→validation 流水线；
/// 只读团队走纯研讨计划，不挂 build/unit-test 门禁。
/// 这是计划形态的单一事实来源，避免控制面假设与团队能力错配。
/// </summary>
internal sealed record TeamCapabilityProfile(bool CanWriteFiles, bool HasWebAccess)
{
    /// <summary>未提供 TeamConfig 时的保守默认：保持与历史行为一致（写导向流水线）。</summary>
    public static TeamCapabilityProfile WriteCapable { get; } = new(CanWriteFiles: true, HasWebAccess: false);

    public static TeamCapabilityProfile From(TeamConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var canWrite = config.Members.Any(member =>
            member.AllowedTools?.Any(tool
                => tool is "Edit" or "Write") == true);
        var hasWeb = config.Members.Any(member =>
            member.AllowedTools?.Any(tool
                => tool is "WebSearch" or "WebFetch") == true);
        return new(CanWriteFiles: canWrite, HasWebAccess: hasWeb);
    }
}
