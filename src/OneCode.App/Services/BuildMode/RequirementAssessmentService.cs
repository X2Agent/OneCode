using System.Text.RegularExpressions;
using OneCode.Core.Build;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// Deterministic, multi-dimensional clarification gate. The model is not trusted to decide
/// whether write capabilities may be exposed; each dimension is derived from explicit signals.
/// </summary>
public sealed partial class RequirementAssessmentService
{
    private static readonly string[] s_broadProductTerms =
    [
        "system", "platform", "product", "application", "系统", "平台", "产品", "应用", "全套", "完整应用",
    ];

    private static readonly string[] s_explicitTargetTerms =
    [
        ".cs", ".csproj", ".sln", ".slnx", "line ", "行", "method", "方法", "class", "类",
        "module", "模块", "service", "服务", "component", "组件", "api", "接口",
    ];

    private static readonly string[] s_acceptanceTerms =
    [
        "run", "build", "compile", "test", "verify", "pass", "构建", "编译", "测试", "验证", "通过", "验收",
    ];

    private static readonly string[] s_constraintTerms =
    [
        "must", "should", "without", "only", "compatible", "performance", "security", "必须", "应当", "禁止",
        "兼容", "性能", "安全", "不允许", "仅", "保持", ".net", "c#", "ef core", "sql server",
    ];

    private static readonly string[] s_architectureChoiceTerms =
    [
        " or ", "或者", "任选", "二选一", "which", "哪种", "前端还是", "数据库选", "architecture", "架构选择",
    ];

    private static readonly string[] s_externalDependencyTerms =
    [
        "database", "storage", "queue", "cache", "cloud", "deploy", "external api", "数据库", "存储", "消息队列",
        "缓存", "云服务", "部署", "外部接口", "第三方", "凭证", "密钥",
    ];

    private static readonly string[] s_highRiskTerms =
    [
        "delete", "remove", "drop", "migration", "authorization", "permission", "authentication", "transaction",
        "concurrency", "删除", "移除", "迁移", "权限", "认证", "事务", "并发", "数据一致性", "公共 api",
    ];

    public RequirementAssessment Assess(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var normalized = Normalize(prompt);
        var hasBroadProductTerm = ContainsAny(normalized, s_broadProductTerms);
        var hasExplicitTarget = ContainsAny(normalized, s_explicitTargetTerms)
            || FilePathPattern().IsMatch(normalized)
            || SymbolPattern().IsMatch(normalized);
        var hasConcreteAction = ActionPattern().IsMatch(normalized);
        var hasAcceptanceEvidence = ContainsAny(normalized, s_acceptanceTerms)
            || ExpectedOutcomePattern().IsMatch(normalized);
        var hasConstraintEvidence = ContainsAny(normalized, s_constraintTerms);
        var hasUnresolvedChoice = ContainsAny(normalized, s_architectureChoiceTerms);
        var externalDependencies = CountMatches(normalized, s_externalDependencyTerms);
        var businessDomains = CountBusinessDomains(normalized);
        var scopeIsLarge = hasBroadProductTerm || businessDomains >= 3 || externalDependencies >= 2;
        var targetRepositoryUnclear = scopeIsLarge && !hasExplicitTarget;

        var goalIsClear = hasConcreteAction && (!hasBroadProductTerm || hasExplicitTarget);
        var scopeIsBounded = hasExplicitTarget && !targetRepositoryUnclear;
        var acceptanceIsDeterministic = hasAcceptanceEvidence
            && (hasExplicitTarget || ExpectedOutcomePattern().IsMatch(normalized));
        var constraintsAreComplete = !scopeIsLarge
            || hasConstraintEvidence
            || (hasExplicitTarget && externalDependencies == 0);
        var requiresUserDecision = hasUnresolvedChoice
            || targetRepositoryUnclear
            || (scopeIsLarge && externalDependencies > 0 && !hasConstraintEvidence);
        var risk = DetermineRisk(normalized, scopeIsLarge, externalDependencies);

        var reasons = new List<string>();
        if (!goalIsClear) reasons.Add("The requested outcome does not identify both a concrete action and a bounded target.");
        if (!scopeIsBounded) reasons.Add("The target repository, module, file, symbol, or output boundary is not explicit.");
        if (!acceptanceIsDeterministic) reasons.Add("No deterministic test, command, invariant, or observable expected result can be derived.");
        if (!constraintsAreComplete) reasons.Add("Material technology, compatibility, infrastructure, performance, or security constraints are missing.");
        if (requiresUserDecision) reasons.Add("A material product, architecture, repository, or external-dependency decision remains unresolved.");
        if (businessDomains >= 3) reasons.Add($"The request spans at least {businessDomains} business domains and exceeds a single bounded change.");

        return new RequirementAssessment(
            goalIsClear,
            scopeIsBounded,
            acceptanceIsDeterministic,
            constraintsAreComplete,
            requiresUserDecision,
            risk,
            reasons);
    }

    public IReadOnlyList<string> BuildClarificationQuestions(RequirementAssessment assessment, string? prompt = null)
    {
        var subject = BuildQuestionSubject(prompt);
        var questions = new List<string>();
        if (!assessment.GoalIsClear)
            questions.Add($"针对{subject}，最终需要新增、修复或重构成什么可观察行为？");
        if (!assessment.ScopeIsBounded)
            questions.Add($"针对{subject}，修改范围限定在哪个仓库、模块、文件或公共接口？哪些内容明确不改？");
        if (!assessment.AcceptanceIsDeterministic)
            questions.Add($"针对{subject}，请给出验收方式：应运行什么构建/测试命令，或观察到什么结果才算完成？");
        if (!assessment.ConstraintsAreComplete)
            questions.Add($"实施{subject}时，有哪些必须遵守的技术、兼容性、部署、性能、数据或安全约束？没有可回答“无额外约束”。");
        if (assessment.RequiresUserDecision)
            questions.Add($"关于{subject}，仍未确定的产品范围、仓库、数据模型、外部依赖或架构选项应选择哪一个？");
        return questions;
    }

    private static string BuildQuestionSubject(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "这项需求";

        var normalized = Normalize(prompt);
        const int maxLength = 48;
        var summary = normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd() + "…";
        return $"需求“{summary}”";
    }

    private static BuildRiskLevel DetermineRisk(
        string normalized,
        bool scopeIsLarge,
        int externalDependencies)
    {
        if (ContainsAny(normalized, s_highRiskTerms))
            return BuildRiskLevel.High;
        if (scopeIsLarge || externalDependencies > 0)
            return BuildRiskLevel.Medium;
        return BuildRiskLevel.Low;
    }

    private static int CountBusinessDomains(string value)
    {
        var domainGroups = new[]
        {
            new[] { "user", "account", "用户", "账号" },
            new[] { "order", "payment", "订单", "支付" },
            new[] { "product", "catalog", "商品", "产品目录" },
            new[] { "requirement", "需求" },
            new[] { "development", "code", "研发", "开发", "编码" },
            new[] { "test", "quality", "测试", "质量" },
            new[] { "release", "deploy", "发布", "部署" },
            new[] { "report", "analytics", "报表", "分析" },
        };
        return domainGroups.Count(group => group.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static int CountMatches(string value, IEnumerable<string> terms)
        => terms.Count(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string value, IEnumerable<string> terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string value)
        => Regex.Replace(value.Trim(), @"\s+", " ");

    [GeneratedRegex(@"(?:^|\s)(?:[A-Za-z]:[\\/]|[./])?[^\s]+\.(?:cs|csproj|slnx?|json|ya?ml|md)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex FilePathPattern();

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9_]*(?:Service|Controller|Repository|Provider|Runner|Dispatcher|Handler|Module|Method|Class)\b")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex(@"\b(fix|add|implement|refactor|update|replace|remove|rename|optimize|diagnose|test|build)\b|修复|新增|实现|重构|更新|替换|移除|重命名|优化|诊断|测试|构建", RegexOptions.IgnoreCase)]
    private static partial Regex ActionPattern();

    [GeneratedRegex(@"(should|must|expect(?:ed)?|returns?|becomes?|remains?|no longer|without)\b|应当|必须|预期|返回|变为|保持|不再|不能|不得|没有", RegexOptions.IgnoreCase)]
    private static partial Regex ExpectedOutcomePattern();
}
