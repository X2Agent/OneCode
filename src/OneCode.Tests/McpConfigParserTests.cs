using OneCode.Core.Mcp;
using OneCode.Infrastructure.Mcp;

namespace OneCode.Tests;

/// <summary>
/// McpConfigParser 回归测试：环境变量展开（${VAR} / ${env:VAR}）与 transport 推断
/// 对 null command/url 的依赖——展开逻辑不得把缺失字段折叠成空串。
/// </summary>
public sealed class McpConfigParserTests : IDisposable
{
    private const string TestVarName = "ONECODE_TEST_MCP_SECRET";
    private readonly List<string> _tempFiles = [];

    public McpConfigParserTests()
    {
        Environment.SetEnvironmentVariable(TestVarName, "tok-123");
    }

    [Fact]
    public void Parse_ExpandsEnvVariablesInCommandArgsEnvAndHeaders()
    {
        // $$""" 中 {{expr}} 即插值，无法直接书写字面量 ${VAR} —— 预构造占位符
        var placeholder = "${" + TestVarName + "}";
        var json = $$"""
            {
              "mcpServers": {
                "github": {
                  "type": "stdio",
                  "command": "{{placeholder}}_runner",
                  "args": ["--token", "{{placeholder}}"],
                  "env": { "GITHUB_TOKEN": "{{placeholder}}" },
                  "headers": { "Authorization": "Bearer {{placeholder}}" }
                }
              }
            }
            """;

        var parsed = McpConfigParser.Parse(json);

        var def = parsed.Servers["github"];
        def.Command.Should().Be("tok-123_runner");
        def.Args.Should().Equal("--token", "tok-123");
        def.Env!["GITHUB_TOKEN"].Should().Be("tok-123");
        def.Headers!["Authorization"].Should().Be("Bearer tok-123");
    }

    [Fact]
    public void Parse_SupportsEnvPrefixForm()
    {
        var json = $$"""
            {
              "mcpServers": {
                "web": {
                  "type": "sse",
                  "url": "https://example.com/sse",
                  "headers": { "X-Key": "${env:{{TestVarName}}}" }
                }
              }
            }
            """;

        var def = McpConfigParser.Parse(json).Servers["web"];

        def.Headers!["X-Key"].Should().Be("tok-123");
    }

    [Fact]
    public void Parse_MissingVariableExpandsToEmptyString()
    {
        var json = """{ "mcpServers": { "x": { "type": "stdio", "command": "run-${ONECODE_TEST_MCP_MISSING_VAR}" } } }""";

        var def = McpConfigParser.Parse(json).Servers["x"];

        def.Command.Should().Be("run-");
    }

    [Fact]
    public void Parse_LeavesNonVariableDollarSequencesUntouched()
    {
        var json = """{ "mcpServers": { "x": { "type": "stdio", "command": "sh", "args": ["-c", "echo $HOME and ${1invalid and ${}"] } } }""";

        var def = McpConfigParser.Parse(json).Servers["x"];

        def.Args.Should().Contain("echo $HOME and ${1invalid and ${}");
    }

    [Fact]
    public void Parse_PreservesNullCommandForTransportInference()
    {
        // command: null（展开前）不得折叠成空串——transport 推断依赖 command == null
        var json = """{ "mcpServers": { "x": { "url": "https://example.com/sse" } } }""";

        var def = McpConfigParser.Parse(json).Servers["x"];

        def.TransportType.Should().Be(McpTransportType.Sse);
        def.Command.Should().BeNull();
    }

    [Fact]
    public void ExpandEnvironmentVariables_WithoutPlaceholderReturnsValueUnchanged()
    {
        McpConfigParser.ExpandEnvironmentVariables("plain-value").Should().Be("plain-value");
        McpConfigParser.ExpandEnvironmentVariables("$HOME not a placeholder ${}").Should().Be("$HOME not a placeholder ${}");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestVarName, null);
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // ===== GetPath project 作用域路径解析（读写一致性防回归）=====

    [Fact]
    public void GetPath_Project_ResolvesNearestAncestorConfig()
    {
        var root = Directory.CreateTempSubdirectory("onecode-mcp-path-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, ".mcp.json"), "{}");
            var sub = Directory.CreateDirectory(Path.Combine(root.FullName, "deep", "deeper"));

            // 写路径必须与 FindProjectConfigPath 的读取路径一致（向上解析）：
            // 否则 /mcp add 等写操作会在 CWD 新建文件，造成配置分裂
            McpConfigFileStore.GetPath("project", sub.FullName)
                .Should().Be(Path.Combine(root.FullName, ".mcp.json"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetPath_Project_FallsBackToWorkingDirectoryWhenUnconfigured()
    {
        var root = Directory.CreateTempSubdirectory("onecode-mcp-path-");
        try
        {
            McpConfigFileStore.GetPath("project", root.FullName)
                .Should().Be(Path.Combine(root.FullName, ".mcp.json"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetPath_Project_StopsAtGitBoundary()
    {
        var outer = Directory.CreateTempSubdirectory("onecode-mcp-path-");
        try
        {
            File.WriteAllText(Path.Combine(outer.FullName, ".mcp.json"), "{}");
            var repo = Directory.CreateDirectory(Path.Combine(outer.FullName, "repo"));
            Directory.CreateDirectory(Path.Combine(repo.FullName, ".git"));

            // .git 边界之上的仓库外配置不可见——与 loader 的读取语义一致
            McpConfigFileStore.GetPath("project", repo.FullName)
                .Should().Be(Path.Combine(repo.FullName, ".mcp.json"));
        }
        finally
        {
            outer.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetPath_User_ReturnsUserProfileConfigPath()
    {
        // user 作用域不受工作目录影响（防回归：工作目录参数不得泄漏到 user 路径）
        var userPath = McpConfigFileStore.GetPath("user");
        McpConfigFileStore.GetPath("user", @"C:\nonexistent\working\directory")
            .Should().Be(userPath);
    }

    // ===== 工具白名单（tools 字段）=====

    [Fact]
    public async Task Parse_ToolsArray_PopulatesEnabledTools()
    {
        var json = """{ "mcpServers": { "x": { "command": "x", "tools": ["get_*", "list_files"] } } }""";
        var parsed = await McpConfigParser.ParseFromFileAsync(
            WriteTempConfig(json), TestContext.Current.CancellationToken);

        parsed.Servers["x"].EnabledTools.Should().Equal("get_*", "list_files");
    }

    [Fact]
    public async Task Parse_MissingToolsField_KeepsEnabledToolsNull()
    {
        var json = """{ "mcpServers": { "x": { "command": "x" } } }""";
        var parsed = await McpConfigParser.ParseFromFileAsync(
            WriteTempConfig(json), TestContext.Current.CancellationToken);

        parsed.Servers["x"].EnabledTools.Should().BeNull();
    }

    [Fact]
    public async Task Parse_EmptyToolsArray_MaterializesEmptyWhitelist()
    {
        var json = """{ "mcpServers": { "x": { "command": "x", "tools": [] } } }""";
        var parsed = await McpConfigParser.ParseFromFileAsync(
            WriteTempConfig(json), TestContext.Current.CancellationToken);

        // 空数组 = 显式"全禁"，不得与缺省（null=全启用）合并
        parsed.Servers["x"].EnabledTools.Should().BeEmpty();
    }

    [Fact]
    public void FileStore_RoundTripsToolsField_WhileOmittingNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"onecode-mcp-rt-{Guid.NewGuid():N}.json");
        try
        {
            var file = new McpConfigFile
            {
                McpServers = new Dictionary<string, McpConfigEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["a"] = new(Type: "stdio", Command: "a", Tools: ["get_*"]),
                    ["b"] = new(Type: "stdio", Command: "b"),
                },
            };
            McpConfigFileStore.SaveAsync(path, file, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

            var raw = File.ReadAllText(path);
            raw.Should().Contain("\"tools\"");
            raw.Should().NotContain("\"tools\": null");

            var loaded = McpConfigFileStore.Load(path)!;
            loaded.McpServers["a"].Tools.Should().Equal("get_*");
            loaded.McpServers["b"].Tools.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private string WriteTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"onecode-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }
}