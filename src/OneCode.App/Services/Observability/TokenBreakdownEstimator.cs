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
/// 由于 API 返回的 Usage 只给总 input/output，不细分场景，
/// 此结构通过客户端 TokenEstimator 估算各部分 token 数。
///
/// 场景分类（用户指定）：
///   - SystemPrompt：系统提示词（PromptConfigBuilder 构造的字符串）
///   - ToolsAndSkills：工具 JSON schema + 技能描述（用户认为工具和技能同一类）
///   - Messages：历史对话消息文本总和
///   - Other：其他 AIContextProvider 注入的内容（memory、design、plan mode、todo 等）
///            = API 实际 InputTokens - 上述三者（反算）
///
/// SystemPromptDetail（可选）：将 SystemPrompt 进一步按 markdown 标题细分为
/// TemplateBody / Environment / ProjectContext / Memory / OtherSections。
/// </summary>
public sealed record TokenBreakdown(
    int SystemPrompt,
    int ToolsAndSkills,
    int Messages,
    int Other,
    int TotalEstimated,
    SystemPromptBreakdown? SystemPromptDetail = null)
{
    /// <summary>
    /// 从 API 实际 InputTokens 反算 Other 部分。
    /// 当 actualInputTokens 可用时，Other = actual - (system + tools + messages)。
    /// 当 actualInputTokens 不可用时，Other = 0，TotalEstimated 为估算总和。
    ///
    /// 接受 double 参数以避免逐组件取整后再求和导致的累积误差：
    /// round(a*c) + round(b*c) + round(c*c) ≠ round((a+b+c)*c)，
    /// Other 应从未取整的精确和反算，再统一取整。
    /// </summary>
    public static TokenBreakdown FromEstimates(
        double systemPrompt,
        double toolsAndSkills,
        double messages,
        int? actualInputTokens = null,
        SystemPromptBreakdown? systemPromptDetail = null)
    {
        var sum = systemPrompt + toolsAndSkills + messages;

        if (actualInputTokens is { } actual && actual > 0)
        {
            var other = Math.Max(0, actual - sum);
            return new TokenBreakdown(
                (int)Math.Round(systemPrompt),
                (int)Math.Round(toolsAndSkills),
                (int)Math.Round(messages),
                (int)Math.Round(other),
                actual,
                systemPromptDetail);
        }

        var total = (int)Math.Round(sum);
        return new TokenBreakdown(
            (int)Math.Round(systemPrompt),
            (int)Math.Round(toolsAndSkills),
            (int)Math.Round(messages),
            0,
            total,
            systemPromptDetail);
    }
}

/// <summary>
/// 分场景 Token 估算器 — 使用 TokenEstimator 估算各部分 token 数。
/// 支持校准系数：从 TokenUsageTracker 获取校准系数，将估算值乘以系数后逼近 API 真实值。
/// </summary>
public sealed partial class TokenBreakdownEstimator : ITokenBreakdownEstimator
{
    private readonly ITokenEstimator _estimator;
    private readonly ITokenUsageTracker? _tracker;
    private readonly ILogger<TokenBreakdownEstimator> _logger;

    public TokenBreakdownEstimator(
        ITokenEstimator estimator,
        ITokenUsageTracker? tracker = null,
        ILogger<TokenBreakdownEstimator>? logger = null)
    {
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        _tracker = tracker;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TokenBreakdownEstimator>.Instance;
    }

    /// <summary>
    /// 估算分场景 token 数。
    /// 当注入了 TokenUsageTracker 且校准系数有效时，估算值会乘以校准系数修正。
    /// </summary>
    /// <param name="systemPrompt">系统提示词字符串。</param>
    /// <param name="tools">工具列表（AIFunction，估算 JsonSchema + Name + Description）。</param>
    /// <param name="messages">历史对话消息。</param>
    /// <param name="actualInputTokens">API 实际返回的 InputTokens（用于反算 Other）。</param>
    public TokenBreakdown Estimate(
        string? systemPrompt,
        IReadOnlyList<AIFunction>? tools,
        IReadOnlyList<ChatMessage>? messages,
        int? actualInputTokens = null)
    {
        var calibration = _tracker?.CalibrationFactor ?? 1.0;

        var systemDetail = ParseSystemPromptSections(systemPrompt, calibration);
        var systemTokens = EstimateText(systemPrompt) * calibration;
        var toolTokens = EstimateTools(tools) * calibration;
        var messageTokens = EstimateMessages(messages) * calibration;

        return TokenBreakdown.FromEstimates(systemTokens, toolTokens, messageTokens, actualInputTokens, systemDetail);
    }

    private int EstimateText(string? text)
        => string.IsNullOrEmpty(text) ? 0 : _estimator.EstimateTokens(text);

    /// <summary>
    /// 估算工具列表的 token 数。
    /// 每个 AIFunction 仅序列化 Name + Description + JsonSchema（API 层面实际发送给 LLM 的内容），
    /// 不包含运行时字段（Metadata、底层委托等），避免高估。
    /// </summary>
    private int EstimateTools(IReadOnlyList<AIFunction>? tools)
    {
        if (tools is null or { Count: 0 }) return 0;

        var total = 0;
        foreach (var tool in tools)
        {
            try
            {
                // 构造精简 DTO：只包含 LLM 实际看到的 schema 信息。
                // 估算路径非 AOT 关键路径，使用反射式序列化即可。
                var dto = new ToolSchemaDto(
                    tool.Name,
                    tool.Description,
                    tool.JsonSchema);
                var json = JsonSerializer.Serialize(dto);
                total += _estimator.EstimateTokens(json);
            }
            catch (Exception ex)
            {
                // 序列化失败时用 name + description 兜底
                _logger.LogDebug(ex, "Token estimate: tool schema serialize failed for {Tool}, falling back to name+description", tool.Name);
                total += _estimator.EstimateTokens(tool.Name) +
                         _estimator.EstimateTokens(tool.Description);
            }
        }

        return total;
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

            var tokens = (int)Math.Round(_estimator.EstimateTokens(sectionText.ToString()) * calibration);

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
