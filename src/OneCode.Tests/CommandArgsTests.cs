using OneCode.Core.Commands;

namespace OneCode.Tests;

/// <summary>
/// CommandArgs 解析规则锚定：位置参数、值型旗标、布尔旗标与内联赋值。
/// 这些规则被 Review/DesignInit/Init 等命令的参数行为依赖，回归即用户可见行为变化。
/// </summary>
public sealed class CommandArgsTests
{
    [Fact]
    public void Parse_MixedTokens_SeparatesPositionalsFromFlags()
    {
        var a = CommandArgs.Parse(["--staged", "file.cs", "--no-edit"]);

        a.Positionals.Should().ContainSingle().Which.Should().Be("file.cs");
        a.Has("staged").Should().BeTrue();
        a.Has("no-edit").Should().BeTrue();
    }

    [Fact]
    public void Parse_ValueFlagConsumesFollowingToken()
    {
        var a = CommandArgs.Parse(["--base", "main", "src/a.cs"], ["base"]);

        a.Value("base").Should().Be("main");
        // 值已被消费，不会混入位置参数（Review 的文件路径过滤依赖此语义）
        a.Positionals.Should().ContainSingle().Which.Should().Be("src/a.cs");
    }

    [Fact]
    public void Parse_UndeclaredValueFlag_IsTreatedAsBoolean()
    {
        var a = CommandArgs.Parse(["--severity", "critical"], []);

        // 未声明为值型 → 旗标本身入 flags，"critical" 成为位置参数
        a.Has("severity").Should().BeTrue();
        a.Value("severity").Should().BeNull();
        a.Positionals.Should().ContainSingle().Which.Should().Be("critical");
    }

    [Fact]
    public void Parse_ValueFlagAtEndWithoutValue_FallsBackToBoolean()
    {
        var a = CommandArgs.Parse(["--output"], ["output"]);

        a.Has("output").Should().BeTrue();
        a.Value("output").Should().BeNull();
    }

    [Fact]
    public void Parse_InlineAssignment_PopulatesValue()
    {
        var a = CommandArgs.Parse(["--base=develop"]);

        a.Value("base").Should().Be("develop");
        a.Positionals.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyArgs_YieldsAllDefaults()
    {
        var a = CommandArgs.Parse([]);

        a.HasPositionals.Should().BeFalse();
        a.SubCommand.Should().BeNull();
        a.Rest.Should().BeEmpty();
        a.JoinedRest.Should().BeNull();
    }

    [Theory]
    [InlineData("LIST", "list")]
    [InlineData("Push", "push")]
    public void SubCommand_IsLowercased(string raw, string expected)
    {
        CommandArgs.Parse([raw]).SubCommand.Should().Be(expected);
    }
}
