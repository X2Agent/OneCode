using OneCode.Infrastructure.Middleware;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="ToolExecutionBudgetMiddleware"/>
/// </summary>
public sealed class ToolExecutionBudgetMiddlewareTests
{
    [Fact]
    public void LooksLikeJson_JsonObject_ReturnsTrue()
    {
        var json = "{\"key\":\"value\"}";

        // 通过反射调用私有静态方法
        var method = typeof(ToolExecutionBudgetMiddleware).GetMethod("LooksLikeJson",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { json });

        result.Should().Be(true);
    }

    [Fact]
    public void LooksLikeJson_JsonArray_ReturnsTrue()
    {
        var json = "[1,2,3]";

        var method = typeof(ToolExecutionBudgetMiddleware).GetMethod("LooksLikeJson",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { json });

        result.Should().Be(true);
    }

    [Fact]
    public void LooksLikeJson_PlainText_ReturnsFalse()
    {
        var text = "This is plain text";

        var method = typeof(ToolExecutionBudgetMiddleware).GetMethod("LooksLikeJson",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { text });

        result.Should().Be(false);
    }

    [Fact]
    public void LooksLikeJson_JsonWithLeadingWhitespace_ReturnsTrue()
    {
        var json = "   {\"key\":\"value\"}";

        var method = typeof(ToolExecutionBudgetMiddleware).GetMethod("LooksLikeJson",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { json });

        result.Should().Be(true);
    }

    [Fact]
    public void FindSafeTruncationIndex_SmallText_ReturnsTextLength()
    {
        var text = "Short text";

        var method = typeof(ToolExecutionBudgetMiddleware).GetMethod("FindSafeTruncationIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { text, 100 });

        result.Should().Be(10);
    }

    [Fact]
    public void FindSafeTruncationIndex_LargeText_ReturnsMaxChars()
    {
        var text = new string('a', 200);

        var method = typeof(ToolExecutionBudgetMiddleware).GetMethod("FindSafeTruncationIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { text, 100 });

        result.Should().Be(100);
    }

    [Fact]
    public void FindSafeTruncationIndex_JsonWithNewline_TruncatesAtNewline()
    {
        var json = "{\"key1\":\"value1\"}\n{\"key2\":\"value2\"}";

        var method = typeof(ToolExecutionBudgetMiddleware).GetMethod("FindSafeTruncationIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method?.Invoke(null, new object[] { json, 25 });

        // \n is at index 17; LastIndexOfAny finds it, returns 17+1=18
        result.Should().Be(18);
    }
}
