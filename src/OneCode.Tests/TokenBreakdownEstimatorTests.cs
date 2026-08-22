using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Observability;
using OneCode.Core.Domain;
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

        breakdown.SystemPrompt.Should().BeGreaterThan(0);
        breakdown.ToolsAndSkills.Should().BeGreaterThan(0);
        breakdown.Messages.Should().BeGreaterThan(0);
        breakdown.Other.Should().Be(0); // 无 actualInputTokens 时 Other=0
        breakdown.TotalEstimated.Should().Be(
            breakdown.SystemPrompt + breakdown.ToolsAndSkills + breakdown.Messages);
    }

    [Fact]
    public void Estimate_WithActualInputTokens_CalculatesOtherAsRemainder()
    {
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default);
        var systemPrompt = "Short prompt";
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hi") };

        var breakdown = sut.Estimate(systemPrompt, tools: null, messages, actualInputTokens: 1000);

        // Other = 1000 - system - messages，应为正数
        breakdown.Other.Should().BeGreaterThan(0);
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
        nullBreakdown.ToolsAndSkills.Should().Be(0);
        nullBreakdown.Messages.Should().Be(0);
        nullBreakdown.Other.Should().Be(0);
        nullBreakdown.TotalEstimated.Should().Be(0);
        nullBreakdown.SystemPromptDetail.Should().BeNull();

        var emptyBreakdown = sut.Estimate("", Array.Empty<AIFunction>(), Array.Empty<ChatMessage>());
        emptyBreakdown.SystemPrompt.Should().Be(0);
        emptyBreakdown.ToolsAndSkills.Should().Be(0);
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

        // 包含 schema 的工具 token 数应 > 0
        singleToolBreakdown.ToolsAndSkills.Should().BeGreaterThan(0);
        // 多个工具的 token 数应大于单个工具
        multiToolBreakdown.ToolsAndSkills.Should().BeGreaterThan(singleToolBreakdown.ToolsAndSkills);
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
        var tracker = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var breakdown1 = new TokenBreakdown(50, 30, 20, 0, 100);
        var breakdown2 = new TokenBreakdown(50, 30, 20, 0, 100);
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
        var tracker = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var sut = new TokenBreakdownEstimator(TestTokenEstimators.Default, tracker: tracker);

        var result = sut.Estimate("prompt", null, null, actualInputTokens: 1000);

        result.Other.Should().Be(1000 - result.SystemPrompt - result.Messages - result.ToolsAndSkills,
            "Other should be the residual when actualInputTokens is provided");
        result.TotalEstimated.Should().Be(1000);
    }

    [Fact]
    public void Estimate_CalibrationFactorPropagatesToAllScenarios()
    {
        var tracker = new TokenUsageTracker(new CostTracker(), TestSupport.NullSessionIdProvider.Instance);
        var b1 = new TokenBreakdown(100, 60, 40, 0, 200);
        var b2 = new TokenBreakdown(100, 60, 40, 0, 200);
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
