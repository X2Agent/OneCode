using OneCode.App.Services.Hooks;
using OneCode.Core.Hooks;

namespace OneCode.Tests;

/// <summary>
/// HookRegistry 单测——覆盖 ReplaceAll 原子整体替换与代次递增：
///   旧注册清空 → 新注册生效 → matcher 索引重建 → 空序列清空注册表 → Generation 单调递增
/// </summary>
public sealed class HookRegistryTests
{
    private static HookRegistration Make(
        string name,
        HookInterceptionPoint point = HookInterceptionPoint.PreToolCall,
        string matcher = "Bash") => new()
    {
        Name = name,
        Point = point,
        Matcher = matcher,
        ExecutorType = HookType.Command,
        TimeoutMs = 5000,
        Config = new HookConfig(),
    };

    [Fact]
    public void ReplaceAll_RemovesOldRegistrationsAndRebuildsMatcherIndex()
    {
        var registry = new HookRegistry(new GlobHookMatcher());
        registry.Register(Make("old"));
        var fresh = new[]
        {
            Make("new-1"),
            Make("new-2", HookInterceptionPoint.Output, "*"),
        };

        registry.ReplaceAll(fresh);

        var all = registry.GetAll();
        all.Should().HaveCount(2);
        all.Should().OnlyContain(h => h.Name.StartsWith("new"));
        registry.GetMatchesForPoint(HookInterceptionPoint.PreToolCall, "Bash")
            .Should().ContainSingle(h => h.Name == "new-1");
        registry.GetMatchesForPoint(HookInterceptionPoint.Output, "any")
            .Should().ContainSingle(h => h.Name == "new-2");
    }

    [Fact]
    public void ReplaceAll_IncrementsGeneration()
    {
        var registry = new HookRegistry(new GlobHookMatcher());
        registry.Generation.Should().Be(0);

        registry.ReplaceAll([Make("g1")]);
        var first = registry.Generation;

        registry.ReplaceAll([]);
        var second = registry.Generation;

        first.Should().Be(1);
        second.Should().Be(2);
    }

    [Fact]
    public void ReplaceAll_EmptySequence_ClearsRegistry()
    {
        var registry = new HookRegistry(new GlobHookMatcher());
        registry.Register(Make("old"));

        registry.ReplaceAll([]);

        registry.GetAll().Should().BeEmpty();
    }
}