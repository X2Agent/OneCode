using OneCode.Core.Text;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// 分词器契约：记忆检索与工具检索必须共用同一套 token。
/// </summary>
/// <remarks>
/// 两条检索路径各写一套分词时，同一个中文查询会一边能召回、另一边为空。
/// 本文件锁定共享规则本身，并反证工具检索确实走的是它。
/// </remarks>
public sealed class TextTokenizerTests
{
    [Fact]
    public void Tokenize_ChineseSentence_ProducesBigramsNotOneBlob()
    {
        var tokens = TextTokenizer.Tokenize("测试怎么跑");

        tokens.Should().ContainInOrder("测试", "试怎", "怎么", "么跑");
        tokens.Should().NotContain("测试怎么跑",
            "a whole Chinese sentence as one token is unreachable by any substring query");
    }

    [Fact]
    public void Tokenize_SingleChineseChar_KeepsUnigram()
    {
        TextTokenizer.Tokenize("读").Should().ContainSingle().Which.Should().Be("读");
    }

    [Fact]
    public void Tokenize_PascalCase_SplitsAndStems()
    {
        var tokens = TextTokenizer.Tokenize("FindReferences");

        tokens.Should().Contain("find");
        tokens.Should().Contain("references");
        tokens.Should().Contain("reference", "plural stripping makes query and corpus meet");
        tokens.Should().Contain("findreferences");
    }

    [Fact]
    public void Tokenize_MixedChineseAndCode_SeparatesBoth()
    {
        var tokens = TextTokenizer.Tokenize("用 MemoryEntryStore 存记忆");

        tokens.Should().Contain("memory");
        tokens.Should().Contain("entry");
        tokens.Should().Contain("store");
        tokens.Should().Contain("记忆");
    }

    /// <summary>反证：工具检索若改回私有分词，这条会失败。</summary>
    [Fact]
    public void ToolRetrievalIndex_ChineseQuery_MatchesSharedTokenizerTokens()
    {
        var registry = new ToolMetadataRegistry();
        registry.Register(new ToolMetadata
        {
            Name = "Grep",
            Keywords = ["搜索", "查找"],
            SearchHint = "regex content search",
        });

        var matches = registry.SearchTools("搜索内容", maxResults: 5);

        matches.Should().Contain(m => m.ToolName == "Grep",
            "the tool index must tokenize CJK the same way memory retrieval does");
    }
}
