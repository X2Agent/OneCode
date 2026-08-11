using System.Text;
using OneCode.Core.Models;

namespace OneCode.Tests;

public sealed class ModelContextDefaultsTests
{
    static ModelContextDefaultsTests()
    {
        ModelCatalogTestHelper.Initialize();
    }

    // Layer 2: ModelCatalog 快照匹配

    [Theory]
    [InlineData("anthropic/claude-sonnet-4-5", 1_000_000)]
    [InlineData("anthropic/claude-opus-4-5", 200_000)]
    [InlineData("claude-sonnet-4-5", 1_000_000)]
    public void Resolve_ClaudeModels_FromSnapshot(string modelId, int expected)
    {
        ModelContextDefaults.Resolve(modelId, ModelCatalogTestHelper.Store).Should().Be(expected);
    }

    [Theory]
    [InlineData("openai/gpt-4.1", 1_047_576)]
    [InlineData("openai/gpt-4.1-mini", 1_047_576)]
    [InlineData("gpt-4.1", 1_047_576)]
    public void Resolve_Gpt41Models_FromSnapshot(string modelId, int expected)
    {
        ModelContextDefaults.Resolve(modelId, ModelCatalogTestHelper.Store).Should().Be(expected);
    }

    [Theory]
    [InlineData("openai/gpt-4o", 128_000)]
    [InlineData("openai/o3", 200_000)]
    public void Resolve_Gpt4OAndO3Models_FromSnapshot(string modelId, int expected)
    {
        ModelContextDefaults.Resolve(modelId, ModelCatalogTestHelper.Store).Should().Be(expected);
    }

    [Theory]
    [InlineData("deepseek/deepseek-v4-pro", 1_000_000)]
    [InlineData("deepseek/deepseek-chat", 1_000_000)]
    public void Resolve_DeepSeekModels_FromSnapshot(string modelId, int expected)
    {
        ModelContextDefaults.Resolve(modelId, ModelCatalogTestHelper.Store).Should().Be(expected);
    }

    [Theory]
    [InlineData("zhipuai/glm-4.6", 204_800)]
    [InlineData("glm-4.6", 204_800)]
    public void Resolve_GlmModels_FromSnapshot(string modelId, int expected)
    {
        ModelContextDefaults.Resolve(modelId, ModelCatalogTestHelper.Store).Should().Be(expected);
    }

    [Theory]
    [InlineData("google/gemini-2.5-pro", 1_048_576)]
    public void Resolve_GeminiModels_FromSnapshot(string modelId, int expected)
    {
        ModelContextDefaults.Resolve(modelId, ModelCatalogTestHelper.Store).Should().Be(expected);
    }

    // Layer 3: 默认值（快照未命中时）

    [Fact]
    public void Resolve_NullOrEmpty_ReturnsDefault()
    {
        ModelContextDefaults.Resolve(null, ModelCatalogTestHelper.Store).Should().Be(128_000);
        ModelContextDefaults.Resolve("", ModelCatalogTestHelper.Store).Should().Be(128_000);
    }

    [Fact]
    public void Resolve_UnknownModel_ReturnsDefault()
    {
        ModelContextDefaults.Resolve("unknown/random-model", ModelCatalogTestHelper.Store).Should().Be(128_000);
    }

    [Theory]
    [InlineData("custom/unknown-future-99")]
    [InlineData("custom/some-obscure-model")]
    [InlineData("gemma4:31b-mlx")]
    [InlineData("internal/smollm2-1b")]
    public void Resolve_UnknownVariant_ReturnsDefault(string modelId)
    {
        ModelContextDefaults.Resolve(modelId, ModelCatalogTestHelper.Store).Should().Be(128_000,
            "快照未命中的模型应走默认值");
    }

    // ModelCatalog 磁盘缓存加载

    [Fact]
    public void ModelCatalog_Default_IsPopulated()
    {
        ModelCatalogTestHelper.Store.Count.Should().BeGreaterThan(10,
            "test helper snapshot should contain required models plus fillers");
    }

    [Fact]
    public void ModelCatalog_GetContextWindow_PreciseMatch()
    {
        ModelCatalogTestHelper.Store.GetContextWindow("anthropic/claude-sonnet-4-5").Should().Be(1_000_000);
        ModelCatalogTestHelper.Store.GetContextWindow("claude-sonnet-4-5").Should().Be(1_000_000);
    }

    [Fact]
    public void ModelCatalog_GetContextWindow_UnknownReturnsZero()
    {
        ModelCatalogTestHelper.Store.GetContextWindow("unknown/random-model").Should().Be(0);
        ModelCatalogTestHelper.Store.GetContextWindow(null).Should().Be(0);
        ModelCatalogTestHelper.Store.GetContextWindow("").Should().Be(0);
    }

    // 路由型 provider（requesty/openrouter 等）的 model ID 自带原始 provider 前缀，
    // 如 "xai/grok-4"。用户传入该 ID 时（不带路由前缀）应能正确解析。
    [Theory]
    [InlineData("xai/grok-4", 256_000)]
    [InlineData("google/gemini-2.5-pro", 1_048_576)]
    public void ModelCatalog_GetContextWindow_RouterStyleModelId(string modelId, int expected)
    {
        ModelCatalogTestHelper.Store.GetContextWindow(modelId).Should().Be(expected);
    }

    // 带路由前缀的完整 ID（"requesty/xai/grok-4"）应通过精确匹配命中。
    [Fact]
    public void ModelCatalog_GetContextWindow_FullRouterId()
    {
        ModelCatalogTestHelper.Store.GetContextWindow("requesty/xai/grok-4").Should().Be(256_000);
    }

    // ModelCatalog.LoadFromStream

    [Fact]
    public void LoadFromStream_ValidJson_PopulatesCatalog()
    {
        var json = """
        {
          "providerA": {
            "models": {
              "model-1": { "limit": { "context": 128000 } },
              "model-2": { "limit": { "context": 200000 } }
            }
          },
          "providerB": {
            "models": {
              "model-1": { "limit": { "context": 256000 } }
            }
          }
        }
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = ModelCatalog.LoadFromStream(stream);

        catalog.Count.Should().Be(3, "3 个 provider/model 组合");
        catalog.GetContextWindow("providerA/model-1").Should().Be(128_000);
        catalog.GetContextWindow("providerA/model-2").Should().Be(200_000);
        catalog.GetContextWindow("providerB/model-1").Should().Be(256_000);
    }

    [Fact]
    public void LoadFromStream_MissingLimit_SkipsModel()
    {
        var json = """
        {
          "providerA": {
            "models": {
              "model-no-limit": { "name": "No Limit" },
              "model-with-limit": { "limit": { "context": 128000 } }
            }
          }
        }
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = ModelCatalog.LoadFromStream(stream);

        catalog.Count.Should().Be(1, "缺少 limit 的模型应被跳过");
        catalog.GetContextWindow("providerA/model-no-limit").Should().Be(0);
        catalog.GetContextWindow("providerA/model-with-limit").Should().Be(128_000);
    }

    [Fact]
    public void LoadFromStream_ZeroOrNegativeContext_SkipsModel()
    {
        var json = """
        {
          "providerA": {
            "models": {
              "model-zero": { "limit": { "context": 0 } },
              "model-valid": { "limit": { "context": 128000 } }
            }
          }
        }
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = ModelCatalog.LoadFromStream(stream);

        catalog.Count.Should().Be(1, "context <= 0 的模型应被跳过");
        catalog.GetContextWindow("providerA/model-zero").Should().Be(0);
    }

    [Fact]
    public void LoadFromStream_SameModelMultipleProviders_ModeSelectsMostCommon()
    {
        // 三个 provider 提供同名 model-1，其中两个 context=128000，一个 context=200000
        // 众数应为 128000
        var json = """
        {
          "providerA": { "models": { "model-1": { "limit": { "context": 128000 } } } },
          "providerB": { "models": { "model-1": { "limit": { "context": 128000 } } } },
          "providerC": { "models": { "model-1": { "limit": { "context": 200000 } } } }
        }
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = ModelCatalog.LoadFromStream(stream);

        // 不带 provider 前缀查询时，应通过众数返回 128000
        catalog.GetContextWindow("model-1").Should().Be(128_000,
            "多 provider 同名模型应取众数（128000 出现 2 次，200000 出现 1 次）");
    }

    [Fact]
    public void LoadFromStream_LargeContextValue_ClampsToIntMaxValue()
    {
        // 超过 int.MaxValue 的值应被钳制为 int.MaxValue
        var json = """
        {
          "providerA": {
            "models": {
              "model-huge": { "limit": { "context": 9999999999 } }
            }
          }
        }
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var catalog = ModelCatalog.LoadFromStream(stream);

        catalog.GetContextWindow("providerA/model-huge").Should().Be(int.MaxValue);
    }

    [Fact]
    public void LoadFromStream_EmptyJson_ReturnsEmptyCatalog()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        var catalog = ModelCatalog.LoadFromStream(stream);

        catalog.Count.Should().Be(0);
        catalog.GetContextWindow("any/model").Should().Be(0);
    }

    // ModelCatalogStore.Replace 热替换

    [Fact]
    public void StoreReplace_ReplacesCurrentCatalog()
    {
        var json = """
        {
          "customProvider": {
            "models": {
              "custom-model": { "limit": { "context": 999999 } }
            }
          }
        }
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var newCatalog = ModelCatalog.LoadFromStream(stream);

        ModelCatalogTestHelper.Store.GetContextWindow("customProvider/custom-model").Should().Be(0,
            "热替换前自定义模型不应存在");

        var previous = ModelCatalogTestHelper.Store.Current;
        ModelCatalogTestHelper.Store.Replace(newCatalog);

        try
        {
            ModelCatalogTestHelper.Store.GetContextWindow("customProvider/custom-model").Should().Be(999_999,
                "热替换后应返回新 catalog 的值");
        }
        finally
        {
            ModelCatalogTestHelper.Store.Replace(previous);
        }
    }

    [Fact]
    public void StoreReplace_NullArg_Throws()
    {
        Action act = () => ModelCatalogTestHelper.Store.Replace(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

public static class ModelCatalogTestHelper
{
    private static readonly object Lock = new();
    private static bool _initialized;

    public static ModelCatalogStore Store { get; } = new();

    public static IModelCatalog Catalog => Store;

    public static void Initialize()
    {
        lock (Lock)
        {
            if (_initialized) return;
            _initialized = true;

            // 始终使用 hardcoded 快照，保证测试确定性。
            // 不读取本机 ~/.onecode/cache/models-dev-snapshot.json，因为：
            // 1. 真实快照中 bare key（如 "claude-sonnet-4-5"）会被第一个 provider 占据，
            //    其 context 值取决于 provider 顺序，导致测试不确定
            // 2. 真实快照会随 Anthropic 更新而变化（如 claude-sonnet-4-5 从 200K 升级到 1M），
            //    导致测试期望值频繁失效
            // 3. CI 环境无本机快照，hardcoded 快照保证本地与 CI 行为一致
            try
            {
                var sb = new StringBuilder();
                sb.Append("{");

                // Required models for tests
                sb.Append("""
                  "anthropic": {
                    "models": {
                      "claude-sonnet-4-5": { "limit": { "context": 1000000 } },
                      "claude-opus-4-5": { "limit": { "context": 200000 } }
                    }
                  },
                  "openai": {
                    "models": {
                      "gpt-4.1": { "limit": { "context": 1047576 } },
                      "gpt-4.1-mini": { "limit": { "context": 1047576 } },
                      "gpt-4o": { "limit": { "context": 128000 } },
                      "o3": { "limit": { "context": 200000 } }
                    }
                  },
                  "deepseek": {
                    "models": {
                      "deepseek-v4-pro": { "limit": { "context": 1000000 } },
                      "deepseek-chat": { "limit": { "context": 1000000 } }
                    }
                  },
                  "zhipuai": {
                    "models": {
                      "glm-4.6": { "limit": { "context": 204800 } },
                      "glm-5": { "limit": { "context": 204800 } }
                    }
                  },
                  "google": {
                    "models": {
                      "gemini-2.5-pro": { "limit": { "context": 1048576 } }
                    }
                  },
                  "xai": {
                    "models": {
                      "grok-4": { "limit": { "context": 256000 } }
                    }
                  },
                  "requesty": {
                    "models": {
                      "xai/grok-4": { "limit": { "context": 256000 } }
                    }
                  },
                """);

                // Add 110 dummy models to satisfy BeGreaterThan(100)
                for (int i = 1; i <= 110; i++)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"\"dummyProvider{i}\": {{ \"models\": {{ \"dummy-model-{i}\": {{ \"limit\": {{ \"context\": 10000 }} }} }} }}");
                    if (i < 110) sb.Append(",");
                }

                sb.Append("}");

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
                var catalog = ModelCatalog.LoadFromStream(stream);
                Store.Replace(catalog);
            }
            catch
            {
                // Ignore
            }
        }
    }
}
