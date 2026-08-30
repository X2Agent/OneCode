using OneCode.App.Services.Hooks;
using OneCode.Core.Hooks;

namespace OneCode.Tests;

/// <summary>
/// HookRegistry 单测——覆盖 ReplaceAll 原子整体替换：
///   旧注册清空 → 新注册生效 → matcher 索引重建 → 空序列清空注册表
/// </summary>
public sealed class HookRegistryTests
{
    private static HookRegistration Make(
        string name,
        HookEvent @event = HookEvent.PreToolUse,
        string matcher = "Bash") => new()
    {
        Name = name,
        Event = @event,
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
            Make("new-2", HookEvent.Stop, "*"),
        };

        registry.ReplaceAll(fresh);

        var all = registry.GetAll();
        all.Should().HaveCount(2);
        all.Should().OnlyContain(h => h.Name.StartsWith("new"));
        registry.GetMatchesForEvent(HookEvent.PreToolUse, "Bash")
            .Should().ContainSingle(h => h.Name == "new-1");
        registry.GetMatchesForEvent(HookEvent.Stop, "any")
            .Should().ContainSingle(h => h.Name == "new-2");
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