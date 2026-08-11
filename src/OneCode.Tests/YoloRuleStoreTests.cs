using OneCode.Core.Permissions.Yolo;

namespace OneCode.Tests;

/// <summary>
/// YoloRuleStore 单元测试。
///
/// YoloRuleStore 构造时加载内置默认规则（安全改进：避免启动竞态期间规则集为空）。
/// 测试中调用 ClearRules() 清空内置规则，以隔离测试用户规则操作行为。
/// </summary>
public sealed class YoloRuleStoreTests
{
    // AddRule / Rules

    [Fact]
    public void AddRule_AddsToRulesList()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        var rule = new UserRule("allow", "git", "safe git");

        store.AddRule(rule);

        store.Rules.Should().ContainSingle().Which.Should().Be(rule);
    }

    [Fact]
    public void Rules_ReturnsDefensiveCopy_NotLiveReference()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        store.AddRule(new UserRule("allow", "a", "desc"));
        var snapshot1 = store.Rules;
        store.AddRule(new UserRule("allow", "b", "desc"));

        snapshot1.Should().ContainSingle();
        store.Rules.Should().HaveCount(2);
    }

    [Fact]
    public void AddRule_MultipleRules_PreservesInsertionOrder()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        store.AddRule(new UserRule("allow", "first", "first"));
        store.AddRule(new UserRule("deny", "second", "second"));
        store.AddRule(new UserRule("allow", "third", "third"));

        store.Rules.Select(r => r.Pattern).Should().Equal(["first", "second", "third"]);
    }

    // RemoveRule

    [Fact]
    public void RemoveRule_RemovesByPattern()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        store.AddRule(new UserRule("allow", "git", "desc"));
        store.AddRule(new UserRule("deny", "rm", "desc"));

        var removed = store.RemoveRule("git");

        removed.Should().BeTrue();
        store.Rules.Should().ContainSingle().Which.Pattern.Should().Be("rm");
    }

    [Fact]
    public void RemoveRule_IsCaseInsensitive()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        store.AddRule(new UserRule("allow", "Git", "desc"));

        var removed = store.RemoveRule("GIT");

        removed.Should().BeTrue();
        store.Rules.Should().BeEmpty();
    }

    [Fact]
    public void RemoveRule_NonExistentPattern_ReturnsFalse()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        store.AddRule(new UserRule("allow", "git", "desc"));

        var removed = store.RemoveRule("nonexistent");

        removed.Should().BeFalse();
        store.Rules.Should().HaveCount(1);
    }

    // MatchRule

    [Fact]
    public void MatchRule_RegexPattern_MatchesCorrectly()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        store.AddRule(new UserRule("allow", @"^git\s+status$", "safe git status"));

        var match = store.MatchRule("git status");

        match.Should().NotBeNull();
        match!.Pattern.Should().Be(@"^git\s+status$");
    }

    [Fact]
    public void MatchRule_CaseInsensitiveMatch()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        store.AddRule(new UserRule("allow", "GIT", "desc"));

        var match = store.MatchRule("git status");

        match.Should().NotBeNull();
    }

    [Fact]
    public void MatchRule_NoMatch_ReturnsNull()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        store.AddRule(new UserRule("allow", @"^ls$", "desc"));

        var match = store.MatchRule("rm -rf /");

        match.Should().BeNull();
    }

    [Fact]
    public void MatchRule_MultipleRules_ReturnsFirstMatch()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        var firstRule = new UserRule("allow", "git", "first");
        var secondRule = new UserRule("deny", "git", "second");
        store.AddRule(firstRule);
        store.AddRule(secondRule);

        var match = store.MatchRule("git status");

        match.Should().Be(firstRule);
    }

    [Fact]
    public void MatchRule_InvalidRegex_ReturnsNullAndDoesNotThrow()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        // Invalid regex: unterminated character class
        store.AddRule(new UserRule("allow", "[unclosed", "bad pattern"));

        var act = () => store.MatchRule("anything");

        act.Should().NotThrow();
        var match = act();
        match.Should().BeNull();
    }

    [Fact]
    public void MatchRule_EmptyCommand_NoMatchForNonEmptyPattern()
    {
        var store = new YoloRuleStore(logger: null);
        store.ClearRules();

        store.AddRule(new UserRule("allow", "git", "desc"));

        var match = store.MatchRule("");

        match.Should().BeNull();
    }

    // Built-in default rules

    [Fact]
    public void Constructor_LoadsBuiltInDefaultRules()
    {
        var store = new YoloRuleStore(logger: null);

        // 构造时应加载内置默认规则（不依赖 LoadRulesAsync）
        store.Rules.Should().NotBeEmpty();
        // 应包含可逆 Git 操作的 deny 规则
        store.Rules.Should().Contain(r => r.Pattern.Contains("git") && r.Type == "deny");
        // 应包含只读操作的 allow 规则
        store.Rules.Should().Contain(r => r.Type == "allow");
    }

    [Fact]
    public void ClearRules_EmptiesRuleSet()
    {
        var store = new YoloRuleStore(logger: null);
        store.Rules.Should().NotBeEmpty();

        store.ClearRules();

        store.Rules.Should().BeEmpty();
    }
}
