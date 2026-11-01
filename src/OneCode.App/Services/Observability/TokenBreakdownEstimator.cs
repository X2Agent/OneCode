using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OneCode.Core;

namespace OneCode.App.Services.Observability;

/// <summary>
/// 系统提示词内部分场景估算结果。
/// 按 markdown 一级标题（# Xxx）切分 systemPrompt，分别估算各段 token。
/// </summary>
/// <param name="TemplateBody">模板硬编码部分（首个 # 标题之前的引导文本 + 未被识别的 section）。</param>
/// <param name="Environment"># Environment section（OS、Git、平台信息）。</param>
/// <param name="ProjectContext"># Project Context section（AGENTS.md、规则文件）。</param>
/// <param name="Memory"># Memory section（MEMORY.md 索引、相关记忆）。</param>
/// <param name="OtherSections">其他被识别为独立 section 但未单独命名的 token 总和。</param>
public sealed record SystemPromptBreakdown(
    int TemplateBody,
    int Environment,
    int ProjectContext,
    int Memory,
    int OtherSections)
{
    /// <summary>所有部分的总和，应等于 SystemPrompt 估算值。</summary>
    public int Total => TemplateBody + Environment + ProjectContext + Memory + OtherSections;
}

/// <summary>
/// 分场景 Token 估算结果。
/// API 返回的 Usage 只给总 input/output，不细分场景；本结构通过客户端 TokenEstimator
/// 估算各部分 token，并用 API 实际 InputTokens 反算残差（Other）。
///
/// 场景分类：
///   - SystemPrompt：产品模板正文估算
///   - ProductTools / McpTools：工具 JSON schema（`mcp__` 前缀单列）
///   - Messages：持久对话历史（provider 注入内容不落转录，不在此桶）
///   - ContextProviders：消息级 AIContextProvider 注入的逐贡献者分桶（sourceId → tokens，
///     由 TranscriptChatHistoryProvider 在组装点累加；经 AIContext.Instructions 注入的
///     skills / memory / design 等无 sourceId——合并进 ChatOptions.Instructions 后不可分辨，
///     当前落在 Other）
///   - Other：API 实际 InputTokens 减上述各项的残差（harness 片段 + 协议开销 + 估算误差）
/// </summary>
public sealed record TokenBreakdown(
    int SystemPrompt,
    int ProductTools,
    int McpTools,
    int Messages,
    IReadOnlyDictionary<string, int> ContextProviders,
    int Other,
    int TotalEstimated,
    SystemPromptBreakdown? SystemPromptDetail = null)
{
    /// <summary>全部工具 schema 之和（产品工具 + MCP）。</summary>
    public int Tools => ProductTools + McpTools;

    /// <summary>
    /// 从各分量估算构造，Other 用 API 实际 InputTokens 反算。
    /// 接受 double 分量以避免逐组件取整后再求和的累积误差：
    /// round(a·c) + round(b·c) ≠ round((a+b)·c)，Other 应从未取整的精确和反算，再统一取整。
    /// </summary>
    public static TokenBreakdown FromEstimates(
        double systemPrompt,
        double productTools,
        double mcpTools,
        double messages,
        IReadOnlyDictionary<string, int>? contextProviders,
        int? actualInputTokens = null,
        SystemPromptBreakdown? systemPromptDetail = null)
    {
        var providers = contextProviders ?? new Dictionary<string, int>();
        var sum = systemPrompt + productTools + mcpTools + messages + providers.Values.Sum(v => (double)v);
        // 有实际值时 Other 是残差（总量钉在实际 InputTokens 上），否则 Other = 0、总量取估算和。
        var actual = actualInputTokens is > 0 ? actualInputTokens : null;

        return new TokenBreakdown(
            (int)Math.Round(systemPrompt),
            (int)Math.Round(productTools),
            (int)Math.Round(mcpTools),
            (int)Math.Round(messages),
            providers,
            (int)Math.Round(actual is { } a ? Math.Max(0, a - sum) : 0),
            actual ?? (int)Math.Round(sum),
            systemPromptDetail);
    }
}

/// <summary>
/// 分场景 Token 估算器 — 使用 TokenEstimator 估算各部分 token 数。
/// 支持校准系数：从 TokenUsageTracker 获取校准系数，将估算值乘以系数后逼近 API 真实值。
/// </summary>
public sealed partial class TokenBreakdownEstimator(
    ITokenEstimator estimator,
    ITokenUsageTracker? tracker = null,
    ILogger<TokenBreakdownEstimator>? logger = null,
    ContextTokenTallyObserver? tally = null) : ITokenBreakdownEstimator
{
    private readonly ITokenEstimator _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
    private readonly ITokenUsageTracker? _tracker = tracker;
    private readonly ILogger<TokenBreakdownEstimator> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TokenBreakdownEstimator>.Instance;
    private readonly ContextTokenTallyObserver? _tally = tally;

    /// <summary>
    /// 单个工具 schema 的估算结果缓存：工具目录在一次查询内不变，每次估算都重新序列化
    /// 全部 schema 是纯重复工作。键含 name/description/schema 原文，schema 变更即失效。
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Fingerprint, int Tokens)> _toolSchemaTokens =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 估算分场景 token 数。
    /// 当注入了 TokenUsageTracker 且校准系数有效时，估算值会乘以校准系数修正。
    /// provider 注入分桶从 <see cref="ContextTokenTallyObserver"/> 取走（读取即消费，一次查询一次）。
    /// </summary>
    /// <param name="systemPrompt">系统提示词字符串。</param>
    /// <param name="tools">工具列表（AIFunction，估算 JsonSchema + Name + Description；`mcp__` 前缀单列）。</param>
    /// <param name="messages">历史对话消息（持久转录）。</param>
    /// <param name="actualInputTokens">API 实际返回的 InputTokens（用于反算 Other）。</param>
    public TokenBreakdown Estimate(
        string? systemPrompt,
        IReadOnlyList<AIFunction>? tools,
        IReadOnlyList<ChatMessage>? messages,
        int? actualInputTokens = null)
    {
        var calibration = _tracker?.CalibrationFactor ?? 1.0;

        // SystemPrompt 取 SystemPromptDetail 之和：整段再估一遍会因分段取整产生与细项不一致的偏差。
        var systemDetail = ParseSystemPromptSections(systemPrompt, calibration);
        var (productToolTokens, mcpToolTokens) = EstimateTools(tools, calibration);
        var messageTokens = EstimateMessages(messages) * calibration;

        return TokenBreakdown.FromEstimates(
            systemDetail?.Total ?? 0, productToolTokens, mcpToolTokens, messageTokens,
            _tally?.Consume(), actualInputTokens, systemDetail);
    }

    /// <summary>
    /// 估算工具列表的 token 数，按 `mcp__` 命名前缀拆分产品工具与 MCP 工具
    /// （命名规约见 McpToolNaming）。
    /// 每个 AIFunction 仅序列化 Name + Description + JsonSchema（API 层面实际发送给 LLM 的内容），
    /// 不包含运行时字段（Metadata、底层委托等），避免高估。
    /// </summary>
    private (double productTools, double mcpTools) EstimateTools(IReadOnlyList<AIFunction>? tools, double calibration)
    {
        if (tools is null or { Count: 0 }) return (0, 0);

        var product = 0;
        var mcp = 0;
        void Bucket(string name, int tokens)
        {
            if (name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
                mcp += tokens;
            else
                product += tokens;
        }

        foreach (var tool in tools)
        {
            try
            {
                Bucket(tool.Name, EstimateToolSchemaTokens(tool));
            }
            catch (Exception ex)
            {
                // 序列化失败时用 name + description 兜底
                _logger.LogDebug(ex, "Token estimate: tool schema serialize failed for {Tool}, falling back to name+description", tool.Name);
                Bucket(tool.Name, _estimator.EstimateTokens(tool.Name) + _estimator.EstimateTokens(tool.Description));
            }
        }

        return (product * calibration, mcp * calibration);
    }

    private int EstimateToolSchemaTokens(AIFunction tool)
    {
        var schemaText = tool.JsonSchema.GetRawText();
        var fingerprint = $"{tool.Name}\u0000{tool.Description}\u0000{schemaText}";

        if (_toolSchemaTokens.TryGetValue(tool.Name, out var cached)
            && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return cached.Tokens;
        }

        // 构造精简 DTO：只包含 LLM 实际看到的 schema 信息。
        var json = JsonSerializer.Serialize(new ToolSchemaDto(tool.Name, tool.Description, tool.JsonSchema));
        var tokens = _estimator.EstimateTokens(json);
        _toolSchemaTokens[tool.Name] = (fingerprint, tokens);
        return tokens;
    }

    /// <summary>
    /// 估算历史消息的 token 数。
    /// 每条消息提取 Text + 工具调用内容估算。
    /// </summary>
    private int EstimateMessages(IReadOnlyList<ChatMessage>? messages)
    {
        if (messages is null or { Count: 0 }) return 0;

        var total = 0;
        foreach (var msg in messages)
        {
            // 主体文本
            if (!string.IsNullOrEmpty(msg.Text))
                total += _estimator.EstimateTokens(msg.Text);

            // 工具调用和结果内容
            if (msg.Contents is { Count: > 0 })
            {
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionCallContent fcc)
                    {
                        try
                        {
                            var json = JsonSerializer.Serialize(fcc.Arguments);
                            total += _estimator.EstimateTokens(fcc.Name) +
                                     _estimator.EstimateTokens(json);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Token estimate: function call args serialize failed for {Function}", fcc.Name);
                            total += _estimator.EstimateTokens(fcc.Name);
                        }
                    }
                    else if (content is FunctionResultContent frc)
                    {
                        try
                        {
                            var json = JsonSerializer.Serialize(frc.Result);
                            total += _estimator.EstimateTokens(json);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Token estimate: function result serialize failed for call {CallId}", frc.CallId);
                        }
                    }
                }
            }
        }

        return total;
    }

    /// <summary>
    /// 按 markdown 一级标题（^# Title）切分 systemPrompt，分别估算各段 token。
    /// 识别 default.prompt 模板中的标准 section：Environment / Project Context / Memory。
    /// 其他 section 归入 OtherSections；首段（首个 # 之前）归入 TemplateBody。
    /// </summary>
    private SystemPromptBreakdown? ParseSystemPromptSections(string? systemPrompt, double calibration)
    {
        if (string.IsNullOrEmpty(systemPrompt))
            return null;

        // 匹配行首的 # 一级标题
        var matches = SectionHeaderRegex().Matches(systemPrompt);
        if (matches.Count == 0)
        {
            // 无标题分割，整体作为 TemplateBody
            var body = (int)Math.Round(_estimator.EstimateTokens(systemPrompt) * calibration);
            return new SystemPromptBreakdown(body, 0, 0, 0, 0);
        }

        int templateBody = 0;
        int environment = 0;
        int projectContext = 0;
        int memory = 0;
        int otherSections = 0;

        for (var i = 0; i <= matches.Count; i++)
        {
            var sectionStart = i == 0 ? 0 : matches[i - 1].Index;
            var sectionEnd = i < matches.Count ? matches[i].Index : systemPrompt.Length;
            var sectionText = systemPrompt.AsSpan(sectionStart, sectionEnd - sectionStart);

            var tokens = (int)Math.Round(_estimator.EstimateTokens(sectionText) * calibration);

            if (i == 0)
            {
                // 首段（首个 # 标题之前）= 模板引导文本
                templateBody = tokens;
                continue;
            }

            // 从 i-1 个 match 取标题名
            var headerLine = matches[i - 1].Value.TrimStart('#').Trim().ToString();
            switch (headerLine.ToLowerInvariant())
            {
                case "environment":
                    environment = tokens;
                    break;
                case "project context":
                    projectContext = tokens;
                    break;
                case "memory":
                    memory = tokens;
                    break;
                default:
                    otherSections += tokens;
                    break;
            }
        }

        return new SystemPromptBreakdown(templateBody, environment, projectContext, memory, otherSections);
    }

    /// <summary>匹配行首 # 一级标题的正则（multiline）。</summary>
    [GeneratedRegex(@"^# .+$", RegexOptions.Multiline)]
    private static partial Regex SectionHeaderRegex();
}

/// <summary>
/// 精简工具 schema DTO，仅包含 LLM 实际看到的字段。
/// 用于 token 估算时序列化，避免 AIFunction 运行时字段干扰。
/// </summary>
internal sealed record ToolSchemaDto(string Name, string? Description, JsonElement? Schema);
