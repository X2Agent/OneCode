using System.Text.Json;
using OneCode.App.Tools;
using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// PlanStepsArgumentParser：SubmitPlan 领域相关的 <c>steps</c> 形状解析
/// （别名键、包装对象、字符串元素、单字符串字段）。顶层畸形形状（二次编码字符串、单对象）
/// 由 <see cref="ToolArgumentBindingMiddleware"/> 先行归一化，不再由本解析器负责。
/// </summary>
public sealed class PlanStepsArgumentParserTests
{
    [Fact]
    public void Parse_CamelCaseArray_MapsEveryField()
    {
        using var doc = JsonDocument.Parse(
            """
            [
              {"id":"s1","title":"T","description":"D","files":["a.cs","b.cs"],
               "acceptanceCriteria":["c1"],"dependsOn":["s0"],"risk":"high",
               "assignee":"worker","status":"done"}
            ]
            """);

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().ContainSingle();
        var step = result.Steps![0];
        step.Id.Should().Be("s1");
        step.Title.Should().Be("T");
        step.Description.Should().Be("D");
        step.Files.Should().Equal("a.cs", "b.cs");
        step.AcceptanceCriteria.Should().Equal("c1");
        step.DependsOn.Should().Equal("s0");
        step.Risk.Should().Be("high");
        step.Assignee.Should().Be("worker");
        step.Status.Should().Be("done");
    }

    [Fact]
    public void Parse_PascalCaseKeys_MappedLikeCamelCase()
    {
        // 默认编组（AIJsonUtilities.DefaultOptions）大小写不敏感，自定义绑定必须镜像该语义。
        using var doc = JsonDocument.Parse(
            """[{"Id":"s1","Title":"T","Description":"D","AcceptanceCriteria":["c1"]}]""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().ContainSingle();
        result.Steps![0].Id.Should().Be("s1");
        result.Steps![0].Title.Should().Be("T");
        result.Steps![0].Description.Should().Be("D");
        result.Steps![0].AcceptanceCriteria.Should().Equal("c1");
    }

    [Fact]
    public void Parse_SingleStepObject_TreatedAsOneStep()
    {
        // 程序化调用路径仍可直接传裸单步对象；工具路径由通用绑定层先包成单元素数组。
        using var doc = JsonDocument.Parse("""{"title":"T","description":"D"}""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().ContainSingle();
        result.Steps![0].Title.Should().Be("T");
    }

    [Fact]
    public void Parse_WrappedStepsInsideArrayItem_UnwrapsNestedArray()
    {
        // 通用绑定层把顶层包装对象 {"steps":[…]} 归一化成 [{"steps":[…]}] 后的产物。
        using var doc = JsonDocument.Parse(
            """[{"steps":[{"id":"s1","title":"T","description":"D"},{"id":"s2","title":"T2","description":"D2"}]}]""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().HaveCount(2);
        result.Steps![0].Id.Should().Be("s1");
        result.Steps![1].Id.Should().Be("s2");
    }

    [Fact]
    public void Parse_WrappedStepsProperty_UsesWrappedArray()
    {
        using var doc = JsonDocument.Parse(
            """{"steps":[{"id":"s1","title":"T","description":"D"},{"id":"s2","title":"T2","description":"D2"}]}""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().HaveCount(2);
        result.Steps![0].Id.Should().Be("s1");
        result.Steps![1].Id.Should().Be("s2");
    }

    [Fact]
    public void Parse_StringElements_BecomeAutoNumberedTextSteps()
    {
        using var doc = JsonDocument.Parse("""["wire the tool","add regression tests"]""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().HaveCount(2);
        result.Steps![0].Id.Should().Be("step-1");
        result.Steps![0].Title.Should().Be("wire the tool");
        result.Steps![0].Description.Should().Be("wire the tool");
        result.Steps![1].Id.Should().Be("step-2");
        result.Steps![1].Title.Should().Be("add regression tests");
    }

    [Fact]
    public void Parse_DescribedAliasKeys_MapToCanonicalFields()
    {
        // 参数描述向模型广告的措辞（title/label、description/content、dependencies）必须被接住。
        using var doc = JsonDocument.Parse(
            """[{"id":"s1","label":"L","content":"C","dependencies":["s0"]}]""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().ContainSingle();
        var step = result.Steps![0];
        step.Title.Should().Be("L");
        step.Description.Should().Be("C");
        step.DependsOn.Should().Equal("s0");
    }

    [Fact]
    public void Parse_SnakeCaseDependsOn_MapsToDependsOn()
    {
        using var doc = JsonDocument.Parse(
            """[{"id":"s1","title":"T","description":"D","depends_on":["s0"]}]""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps![0].DependsOn.Should().Equal("s0");
    }

    [Fact]
    public void Parse_StepObjectWithNestedStepsArray_PrefersStepSemantics()
    {
        // 顺序守卫：形似单步的对象不得被包装分支吞掉（否则 title 会凭空消失）。
        using var doc = JsonDocument.Parse(
            """{"title":"T","description":"D","steps":[{"id":"inner","title":"Inner","description":"Inner"}]}""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().ContainSingle();
        result.Steps![0].Title.Should().Be("T");
    }

    [Fact]
    public void Parse_UnlistedKeys_AreIgnoredNotAdopted()
    {
        // 无文本依据的猜测键不收录：未知键按忽略处理，由校验器报缺失，而不是被猜成字段值。
        using var doc = JsonDocument.Parse(
            """[{"id":"s1","description":"D","name":"not-a-title","paths":["src/A.cs"]}]""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().ContainSingle();
        result.Steps![0].Title.Should().BeNull("name 不是有依据的 title 别名");
        result.Steps![0].Files.Should().BeNull("paths 不是有依据的 files 别名");
    }

    [Fact]
    public void Parse_FilesGivenAsSingleString_AcceptedAsOneElement()
    {
        using var doc = JsonDocument.Parse(
            """[{"id":"s1","title":"T","description":"D","files":"src/A.cs"}]""");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps![0].Files.Should().Equal("src/A.cs");
    }

    [Fact]
    public void Parse_PlainTextString_TreatedAsOneStep()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize("do the thing"));

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.FailureReason.Should().BeNull();
        result.Steps.Should().ContainSingle();
        result.Steps![0].Title.Should().Be("do the thing");
    }

    [Fact]
    public void Parse_JsonNull_TreatedAsNotProvidedWithoutFailure()
    {
        using var doc = JsonDocument.Parse("null");

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.Steps.Should().BeEmpty();
        result.FailureReason.Should().BeNull("JSON null 与此前语义一致：视作未提供，不是畸形输入");
    }

    [Fact]
    public void Parse_MissingValue_TreatedAsNotProvidedWithoutFailure()
    {
        var result = PlanStepsArgumentParser.Parse(null);

        result.Steps.Should().BeEmpty();
        result.FailureReason.Should().BeNull();
    }

    [Theory]
    [InlineData("42", "number")]
    [InlineData("true", "boolean")]
    [InlineData("[1, 2]", "array without step objects")]
    [InlineData("""{"foo": 1}""", "object without step fields")]
    public void Parse_UnusableShape_ReturnsFailureReasonWithoutThrowing(string json, string scenario)
    {
        using var doc = JsonDocument.Parse(json);

        var result = PlanStepsArgumentParser.Parse(doc.RootElement);

        result.Steps.Should().BeNull($"steps 形状不可用时应留空（{scenario}）");
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }
}
