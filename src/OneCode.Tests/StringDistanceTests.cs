using OneCode.Core.Text;

namespace OneCode.Tests;

/// <summary>Levenshtein 编辑距离的内核契约：模糊搜索的阈值判定建立在准确的距离值上。</summary>
public sealed class StringDistanceTests
{
    [Theory]
    [InlineData("", "abc", 3)]
    [InlineData("hello", "hello", 0)]
    [InlineData("GetUsr", "GetUser", 1)]
    [InlineData("GetUserr", "GetUser", 1)]
    [InlineData("GtUsr", "GetUser", 2)]
    public void Levenshtein_KnownValues(string a, string b, int expected)
        => StringDistance.Levenshtein(a, b).Should().Be(expected);
}
