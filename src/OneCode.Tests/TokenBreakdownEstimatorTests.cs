using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Observability;
using OneCode.Infrastructure.Api;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

public sealed class TokenBreakdownEstimatorTests
{
    [Fact]
    public void Estimate_WithAllParts_SumsCorrectly()
    {
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default);
        var systemPrompt = "You are a helpful assistant. Follow instructions carefully.";
        var tools = new List<AIFunction> { CreateFakeTool("Read", "Read a file") };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello, can you help me?"),
            new(ChatRole.Assistant, "Of course! What do you need?"),
        };

        var breakdown = sut.Estimate(systemPrompt, tools, messages, actualInputTokens: null);

        // 58 个 ASCII 字符 → ceil(58 / 4) = 15。
        breakdown.SystemPrompt.Should().Be(15);
        // 无 schema 的工具退化为 name + description 估算：ceil(4 / 4) + ceil(11 / 4) = 4。
        breakdown.ProductTools.Should().Be(4);
        breakdown.McpTools.Should().Be(0);
        breakdown.Tools.Should().Be(4);
        // "Hello, can you help me?" 23 字符 → 6，"Of course! What do you need?" 28 字符 → 7。
        breakdown.Messages.Should().Be(13);
        breakdown.Other.Should().Be(0); // 无 actualInputTokens 时 Other=0
        breakdown.TotalEstimated.Should().Be(
            breakdown.SystemPrompt + breakdown.Tools + breakdown.Messages);
    }

    [Fact]
    public void Estimate_SplitsMcpToolsFromProductTools()
    {
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default);
        var tools = new List<AIFunction>
        {
            CreateFakeTool("Read", "Read a file from disk"),
            CreateFakeTool("mcp__filesystem__read_file", "MCP filesystem read"),
        };

        var breakdown = sut.Estimate(null, tools, null);

        breakdown.ProductTools.Should().BeGreaterThan(0, "产品工具按 `mcp__` 前缀排除在 MCP 桶外");
        breakdown.McpTools.Should().BeGreaterThan(0, "`mcp__` 前缀工具必须单列（McpToolNaming 命名规约）");
        breakdown.Tools.Should().Be(breakdown.ProductTools + breakdown.McpTools);
    }

    [Fact]
    public void Estimate_UsesContextProviderTallySnapshot_AndComputesResidual()
    {
        var estimator = TestTokenEstimators.Default;
        var tally = new ContextTokenTallyObserver(estimator);
        tally.AddMessageTokens("Microsoft.Agents.AI.TodoProvider", "### Current todo list");

        var sut = new TokenBreakdownEstimator(estimator, tally: tally);

        var breakdown = sut.Estimate("sys", tools: null, messages: null, actualInputTokens: 1000);
        var expected = new Dictionary<string, int>
        {
            ["Microsoft.Agents.AI.TodoProvider"] = estimator.EstimateTokens("### Current todo list"),
        };

        breakdown.ContextProviders.Should().BeEquivalentTo(expected, "分桶取自组装点的消息级快照");
        // Other 为残差：actual − (system + 分桶之和)
        breakdown.Other.Should().Be(1000 - breakdown.SystemPrompt - expected.Values.Sum());
        breakdown.TotalEstimated.Should().Be(1000);

        // 读取即消费：下一轮不会重复计入上一轮的注入
        sut.Estimate("sys", tools: null, messages: null, actualInputTokens: 1000)
            .ContextProviders.Should().BeEmpty("分桶在每次查询结束时被取走");
    }

    [Fact]
    public void Estimate_WithActualInputTokens_CalculatesOtherAsRemainder()
    {
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default);
        var systemPrompt = "Short prompt";
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hi") };

        var breakdown = sut.Estimate(systemPrompt, tools: null, messages, actualInputTokens: 1000);

        breakdown.TotalEstimated.Should().Be(1000); // 等于 actualInputTokens
        breakdown.Other.Should().Be(1000 - breakdown.SystemPrompt - breakdown.Messages);
    }

    [Fact]
    public void Estimate_WithNullOrEmptyInputs_ReturnsZeros()
    {
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default);

        // null inputs 和 empty inputs 走相同的早返回路径
        var nullBreakdown = sut.Estimate(null, null, null, actualInputTokens: null);
        nullBreakdown.SystemPrompt.Should().Be(0);
        nullBreakdown.Tools.Should().Be(0);
        nullBreakdown.Messages.Should().Be(0);
        nullBreakdown.Other.Should().Be(0);
        nullBreakdown.TotalEstimated.Should().Be(0);
        nullBreakdown.SystemPromptDetail.Should().BeNull();
        nullBreakdown.ContextProviders.Should().BeEmpty();

        var emptyBreakdown = sut.Estimate("", [], []);
        emptyBreakdown.SystemPrompt.Should().Be(0);
        emptyBreakdown.Tools.Should().Be(0);
        emptyBreakdown.Messages.Should().Be(0);
    }

    [Fact]
    public void Estimate_ToolsSerializedAsJson_ScalesWithToolCountAndSchema()
    {
        // 验证：EstimateTools 使用 JsonSchema + Name + Description 精简 DTO，
        // 而非序列化整个 AIFunction 对象（避免运行时字段高估）。
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default);
        var tools = new List<AIFunction>
        {
            CreateFakeTool("Read", "Read a file from disk", """
                {"type":"object","properties":{"filePath":{"type":"string"}},"required":["filePath"]}
                """),
            CreateFakeTool("Write", "Write content to a file"),
            CreateFakeTool("Grep", "Search file contents using regex"),
        };

        var multiToolBreakdown = sut.Estimate(null, tools, null);
        var singleToolBreakdown = sut.Estimate(null, new[] { tools[0] }, null);

        // 单个工具（含 schema）必须产生有限的开销：若实现改为序列化整个 AIFunction
        // 对象，token 数会显著膨胀并突破上界；若 schema 未被纳入估算则会低于下界。
        singleToolBreakdown.Tools.Should().BeInRange(20, 60);
        // 多个工具的 token 数应大于单个工具
        multiToolBreakdown.Tools.Should().BeGreaterThan(singleToolBreakdown.Tools);
    }

    [Fact]
    public void Estimate_SystemPromptDetail_ParsesMarkdownSections()
    {
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default);
        var systemPrompt = """
            You are OneCode, an AI coding assistant.

            # Doing tasks
            Follow instructions carefully.

            # Environment
            OS: Windows
            Working directory: C:\project

            # Project Context
            AGENTS.md content here.

            # Memory
            MEMORY.md index here.
            """;

        var breakdown = sut.Estimate(systemPrompt, tools: null, messages: null);

        breakdown.SystemPromptDetail.Should().NotBeNull();
        var detail = breakdown.SystemPromptDetail!;
        detail.TemplateBody.Should().BeGreaterThan(0, "template引导文本应被估算");
        detail.Environment.Should().BeGreaterThan(0, "# Environment section 应被识别");
        detail.ProjectContext.Should().BeGreaterThan(0, "# Project Context section 应被识别");
        detail.Memory.Should().BeGreaterThan(0, "# Memory section 应被识别");
        // Doing tasks 归入 OtherSections
        detail.OtherSections.Should().BeGreaterThan(0, "# Doing tasks 应归入 OtherSections");
        // 分段四舍五入再求和与整体估算有微小 rounding 误差，允许 ±3 token
        detail.Total.Should().BeInRange(
            breakdown.SystemPrompt - 3,
            breakdown.SystemPrompt + 3,
            "分段估算与整体估算之间允许微小 rounding 误差");
    }

    [Fact]
    public void Estimate_SystemPromptDetail_NoSections_AllInTemplateBody()
    {
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default);
        var systemPrompt = "You are a helpful assistant with no sections.";

        var breakdown = sut.Estimate(systemPrompt, tools: null, messages: null);

        breakdown.SystemPromptDetail.Should().NotBeNull();
        var detail = breakdown.SystemPromptDetail!;
        detail.TemplateBody.Should().Be(breakdown.SystemPrompt);
        detail.Environment.Should().Be(0);
        detail.ProjectContext.Should().Be(0);
        detail.Memory.Should().Be(0);
        detail.OtherSections.Should().Be(0);
    }

    // Calibration propagation (migrated from TokenBreakdownEstimatorCalibrationTests)

    [Fact]
    public void Estimate_WithCalibratedTracker_AppliesCalibrationFactor()
    {
        var tracker = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown1 = Breakdown(system: 50, tools: 30, messages: 20, total: 100);
        var breakdown2 = Breakdown(system: 50, tools: 30, messages: 20, total: 100);
        tracker.Record(new TokenUsage(200, 50), breakdown1);
        tracker.Record(new TokenUsage(200, 50), breakdown2);
        tracker.CalibrationFactor.Should().BeApproximately(2.0, 0.001);

        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: tracker);
        var uncalibrated = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: null);

        var calibratedResult = sut.Estimate("short prompt", null, null);
        var rawResult = uncalibrated.Estimate("short prompt", null, null);

        // 校准系数 2.0 应使 SystemPrompt 估算值翻倍（允许 ±2 误差取整）
        calibratedResult.SystemPrompt.Should().BeInRange(
            rawResult.SystemPrompt * 2 - 2,
            rawResult.SystemPrompt * 2 + 2);
    }

    [Fact]
    public void Estimate_WithActualInputTokens_CalculatesOtherWithCalibration()
    {
        var tracker = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: tracker);

        var result = sut.Estimate("prompt", null, null, actualInputTokens: 1000);

        result.Other.Should().Be(1000 - result.SystemPrompt - result.Messages - result.Tools,
            "Other should be the residual when actualInputTokens is provided");
        result.TotalEstimated.Should().Be(1000);
    }

    [Fact]
    public void Estimate_CalibrationFactorPropagatesToAllScenarios()
    {
        var tracker = new TokenUsageTracker(new TokenLedger(), TestSupport.NullSessionIdProvider.Instance);
        var b1 = Breakdown(system: 100, tools: 60, messages: 40, total: 200);
        var b2 = Breakdown(system: 100, tools: 60, messages: 40, total: 200);
        tracker.Record(new TokenUsage(400, 50), b1);
        tracker.Record(new TokenUsage(400, 50), b2);

        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: tracker);
        var raw = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: null);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
        };

        var calibrated = sut.Estimate("system prompt", null, messages);
        var rawResult = raw.Estimate("system prompt", null, messages);

        calibrated.SystemPrompt.Should().BeInRange(
            rawResult.SystemPrompt * 2 - 2,
            rawResult.SystemPrompt * 2 + 2);
        calibrated.Messages.Should().BeInRange(
            rawResult.Messages * 2 - 2,
            rawResult.Messages * 2 + 2);
    }

    /// <summary>新形状的便捷构造（ContextProviders 空桶、无 Other）。</summary>
    private static TokenBreakdown Breakdown(int system, int tools, int messages, int total)
        => new(system, tools, 0, messages, new Dictionary<string, int>(), 0, total);

    private static AIFunction CreateFakeTool(string name, string description, string? schemaJson = null)
    {
        var tool = Substitute.For<AIFunction>();
        tool.Name.Returns(name);
        tool.Description.Returns(description);

        if (schemaJson is not null)
        {
            using var schemaDoc = JsonDocument.Parse(schemaJson);
            var element = schemaDoc.RootElement.Clone();
            tool.JsonSchema.Returns(element);
        }

        return tool;
    }
}
