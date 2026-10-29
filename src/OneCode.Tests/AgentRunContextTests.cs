using OneCode.App.Services.Agent;

namespace OneCode.Tests;

public sealed class AgentRunContextTests
{
    [Fact]
    public async Task WorkingDirectoryOverride_ChildContextWrite_IsVisibleToParentScope()
    {
        var previous = OneCodeAgentRunContext.WorkingDirectoryOverride;
        try
        {
            using var scope = OneCodeAgentRunContext.UseWorkingDirectory(@"C:\repo");
            await Task.Run(async () =>
            {
                await Task.Yield();
                OneCodeAgentRunContext.WorkingDirectoryOverride = @"C:\repo.worktree\feature";
            });

            OneCodeAgentRunContext.WorkingDirectoryOverride.Should().Be(
                @"C:\repo.worktree\feature",
                "工具子 ExecutionContext 必须修改父作用域共享的 holder，而不是只改自己的 AsyncLocal 槽");
        }
        finally
        {
            OneCodeAgentRunContext.WorkingDirectoryOverride = previous;
        }
    }

    [Fact]
    public async Task WorkingDirectoryOverride_NestedScopeChange_DoesNotEscapeOuterScope()
    {
        var previous = OneCodeAgentRunContext.WorkingDirectoryOverride;
        try
        {
            using var outer = OneCodeAgentRunContext.UseWorkingDirectory(@"C:\repo");
            using (var inner = OneCodeAgentRunContext.UseWorkingDirectory(workingDirectory: null))
            {
                OneCodeAgentRunContext.WorkingDirectoryOverride.Should().Be(@"C:\repo");
                await Task.Run(() => OneCodeAgentRunContext.WorkingDirectoryOverride = @"C:\repo.worktree\inner");
                OneCodeAgentRunContext.WorkingDirectoryOverride.Should().Be(@"C:\repo.worktree\inner");
            }

            OneCodeAgentRunContext.WorkingDirectoryOverride.Should().Be(
                @"C:\repo", "嵌套作用域持有独立 holder，退出后必须恢复外层目录");
        }
        finally
        {
            OneCodeAgentRunContext.WorkingDirectoryOverride = previous;
        }
    }

    [Fact]
    public async Task WorkingDirectoryOverride_ParallelScopes_AreIsolated()
    {
        var previous = OneCodeAgentRunContext.WorkingDirectoryOverride;
        try
        {
            using var outer = OneCodeAgentRunContext.UseWorkingDirectory(@"C:\repo");
            var results = await Task.WhenAll(
                RunInScopeAsync(@"C:\goal-one", @"C:\goal-one.worktree"),
                RunInScopeAsync(@"C:\goal-two", @"C:\goal-two.worktree"));

            results[0].Should().Be(@"C:\goal-one.worktree");
            results[1].Should().Be(@"C:\goal-two.worktree");
            OneCodeAgentRunContext.WorkingDirectoryOverride.Should().Be(@"C:\repo");
        }
        finally
        {
            OneCodeAgentRunContext.WorkingDirectoryOverride = previous;
        }
    }

    private static async Task<string> RunInScopeAsync(string initial, string updated)
    {
        using var scope = OneCodeAgentRunContext.UseWorkingDirectory(initial);
        await Task.Yield();
        OneCodeAgentRunContext.WorkingDirectoryOverride = updated;
        return OneCodeAgentRunContext.WorkingDirectoryOverride!;
    }
}
