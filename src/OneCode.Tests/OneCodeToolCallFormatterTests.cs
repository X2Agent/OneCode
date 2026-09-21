using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// B2 行为契约：L1 折叠必须保留调用参数并有长度上限。
/// </summary>
/// <remarks>
/// MAF 默认 formatter 按工具名分组结果、直接拼接 <c>Result.ToString()</c>，
/// 既不保留参数（折叠后的 Read/Grep 不再说明覆盖了哪个文件或模式），也不限制正文长度。
/// </remarks>
public sealed class OneCodeToolCallFormatterTests
{
    [Fact]
    public void Format_KeepsArgumentsAlongsideResult()
    {
        var group = CreateGroup(
            new FunctionCallContent("c1", "Read", new Dictionary<string, object?> { ["file_path"] = "src/Program.cs" }),
            new FunctionResultContent("c1", "namespace OneCode;"));

        var formatted = OneCodeToolCallFormatter.Format(group);

        formatted.Should().Contain("Read:");
        formatted.Should().Contain("file_path=src/Program.cs",
            "a folded call without its arguments no longer identifies what it covered");
        formatted.Should().Contain("namespace OneCode;");
    }

    /// <summary>反证：超大结果必须被截断并显式标注，不能无界占用预算。</summary>
    [Fact]
    public void Format_OversizedResult_IsTruncatedAndMarked()
    {
        var huge = new string('x', 50_000);
        var group = CreateGroup(
            new FunctionCallContent("c1", "Bash", new Dictionary<string, object?> { ["command"] = "dotnet test" }),
            new FunctionResultContent("c1", huge));

        var formatted = OneCodeToolCallFormatter.Format(group);

        formatted.Should().Contain(OneCodeToolCallFormatter.TruncationMarker,
            "silent truncation would let the model assume it saw the whole output");
        formatted.Length.Should().BeLessThan(2_000, "the folded form must actually bound the payload");
        formatted.Should().Contain("dotnet test");
    }

    /// <summary>并行工具调用必须各自与自己的结果配对，不能交叉或错位。</summary>
    [Fact]
    public void Format_ParallelCalls_PairEachResultWithItsOwnCall()
    {
        var group = CreateGroup(
            new FunctionCallContent("c1", "Read", new Dictionary<string, object?> { ["file_path"] = "a.cs" }),
            new FunctionCallContent("c2", "Read", new Dictionary<string, object?> { ["file_path"] = "b.cs" }),
            new FunctionResultContent("c1", "content-of-a"),
            new FunctionResultContent("c2", "content-of-b"));

        var formatted = OneCodeToolCallFormatter.Format(group);

        var firstCall = formatted.IndexOf("file_path=a.cs", StringComparison.Ordinal);
        var secondCall = formatted.IndexOf("file_path=b.cs", StringComparison.Ordinal);
        var firstResult = formatted.IndexOf("content-of-a", StringComparison.Ordinal);
        var secondResult = formatted.IndexOf("content-of-b", StringComparison.Ordinal);

        firstCall.Should().BeLessThan(firstResult);
        firstResult.Should().BeLessThan(secondCall, "results must stay attached to their own call");
        secondCall.Should().BeLessThan(secondResult);
    }

    private static CompactionMessageGroup CreateGroup(params AIContent[] contents)
    {
        var message = new ChatMessage(ChatRole.Assistant, contents);
        var ctor = typeof(CompactionMessageGroup).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)[0];

        return (CompactionMessageGroup)ctor.Invoke(
            [CompactionGroupKind.ToolCall, new List<ChatMessage> { message }, 0, 0, null]);
    }
}
