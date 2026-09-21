using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Memory;
using OneCode.Core.Memory;

namespace OneCode.Tests;

/// <summary>
/// M5 评测：固定样本下的长期记忆召回。样本与期望在代码里冻结，改动检索算法必须让本文件保持通过。
/// </summary>
/// <remarks>
/// <para>
/// 这些用例断言的是**相对召回**（期望条目必须出现在 Top-N），不是绝对分数——
/// 分数随语料变化，锁定分数会让每次新增条目都变成无意义的测试维护。
/// </para>
/// <para>
/// 语料刻意混合中文与代码术语：中文没有空格，正则按 <c>\p{L}</c> 取连续串会把整句切成
/// 一个 token；代码术语则依赖大小写不敏感的子串。两类都必须能召回。
/// </para>
/// </remarks>
public sealed class MemoryRecallEvaluationTests
{
    private const int TopN = 3;

    /// <summary>固定的中文查询样本。</summary>
    [Theory]
    [InlineData("构建命令", "build-command")]
    [InlineData("测试怎么跑", "test-command")]
    [InlineData("数据库迁移流程", "db-migration")]
    [InlineData("部署步骤", "deploy-steps")]
    [InlineData("中文注释要求", "comment-policy")]
    [InlineData("代码规范", "code-style")]
    public async Task FindRelevantMemoriesAsync_ChineseQuery_RecallsExpectedEntry(string query, string expectedKey)
    {
        var service = CreateService();

        var matches = await service.FindRelevantMemoriesAsync(query);

        matches.Take(TopN).Select(m => m.Entry.Key)
            .Should().Contain(expectedKey,
                $"a Chinese query is a first-class input; '{query}' must recall '{expectedKey}'");
    }

    /// <summary>固定的代码术语查询样本。</summary>
    [Theory]
    [InlineData("compaction", "compaction-design")]
    [InlineData("MemoryEntryStore", "memory-store-lock")]
    [InlineData("FileMemoryProvider", "file-memory-scope")]
    [InlineData("ripgrep timeout", "grep-timeout")]
    [InlineData("onecode.slnx", "build-command")]
    public async Task FindRelevantMemoriesAsync_CodeTermQuery_RecallsExpectedEntry(string query, string expectedKey)
    {
        var service = CreateService();

        var matches = await service.FindRelevantMemoriesAsync(query);

        matches.Take(TopN).Select(m => m.Entry.Key)
            .Should().Contain(expectedKey,
                $"code identifiers and file names are the dominant query shape in this tool; '{query}' must recall '{expectedKey}'");
    }

    /// <summary>
    /// 负样本：无关查询不得凭空召回。没有这条，任何「把全部条目都返回」的实现都能通过上面的用例。
    /// </summary>
    [Theory]
    [InlineData("quantum chromodynamics lattice gauge")]
    [InlineData("紫水晶地质年代测量")]
    public async Task FindRelevantMemoriesAsync_UnrelatedQuery_RecallsNothing(string query)
    {
        var service = CreateService();

        var matches = await service.FindRelevantMemoriesAsync(query);

        matches.Should().BeEmpty($"'{query}' shares no term with the corpus");
    }

    /// <summary>反证核心：命中必须来自真实词项，而不是「查询非空就返回前 N 条」。</summary>
    [Fact]
    public async Task FindRelevantMemoriesAsync_EveryMatch_ContainsAQueryTerm()
    {
        var service = CreateService();

        var matches = await service.FindRelevantMemoriesAsync("构建命令");

        matches.Should().NotBeEmpty();
        foreach (var match in matches)
        {
            var haystack = (match.Entry.Key + " " + match.Entry.Value).ToLowerInvariant();
            haystack.Should().MatchRegex("构建|命令|build|command",
                "a returned entry must actually contain part of the query");
        }
    }

    private static MemoryService CreateService()
    {
        var store = new InMemoryMemoryEntryStore();
        var now = DateTimeOffset.UtcNow;

        store.UpsertAsync(MemoryScope.Project,
        [
            Entry("build-command", "运行 dotnet build src/OneCode.slnx 构建整个解决方案；构建命令必须无新增警告。", now),
            Entry("test-command", "测试用 dotnet test src/OneCode.slnx；跑测试前先构建。", now),
            Entry("db-migration", "数据库迁移流程：先备份，再执行迁移脚本，最后校验版本号。", now),
            Entry("deploy-steps", "部署步骤：发布 CLI，然后替换服务端二进制并重启。", now),
            Entry("comment-policy", "中文注释要求：公开成员必须有 XML 文档，代码注释以中文为主。", now),
            Entry("code-style", "代码规范：单类不超过 500 行，构造注入不超过 8 个参数。", now),
            Entry("compaction-design", "Compaction 只有一个所有者：策略经 HarnessAgentOptions.CompactionStrategy 交给 Harness。", now),
            Entry("memory-store-lock", "MemoryEntryStore 用文件锁文件实现跨进程提交，避免 async 下的 Mutex 线程亲和问题。", now),
            Entry("file-memory-scope", "FileMemoryProvider 的 store 根绑定项目，仅 Main profile 启用。", now),
            Entry("grep-timeout", "ripgrep 与 C# fallback 的 regex 都有 5 秒超时；超时按错误处理。", now),
        ]).GetAwaiter().GetResult();

        return new MemoryService(NullLogger<MemoryService>.Instance, store);
    }

    private static MemoryEntry Entry(string key, string value, DateTimeOffset now) => new()
    {
        Key = key,
        Value = value,
        Source = "manual",
        Category = MemoryEntry.DeriveCategory(key),
        CreatedAt = now,
        UpdatedAt = now,
    };
}
