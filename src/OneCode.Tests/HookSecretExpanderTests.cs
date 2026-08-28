using OneCode.App.Services.Hooks;

namespace OneCode.Tests;

/// <summary>
/// HookSecretExpander 单元测试（C1）：${ENV_VAR} 展开 + dpapi: 解密。
/// </summary>
public sealed class HookSecretExpanderTests
{
    private const string TestVarName = "ONECODE_HOOK_TEST_SECRET";

    [Fact]
    public void Expand_NullAndEmpty_Passthrough()
    {
        HookSecretExpander.Expand(null).Should().BeNull();
        HookSecretExpander.Expand(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Expand_PlainValue_ReturnsUnchanged()
    {
        HookSecretExpander.Expand("https://example.com/webhook?key=abc")
            .Should().Be("https://example.com/webhook?key=abc");
    }

    [Fact]
    public void Expand_EnvVar_Reference_Resolves()
    {
        try
        {
            Environment.SetEnvironmentVariable(TestVarName, "s3cret-value");
            HookSecretExpander.Expand($"https://example.com?key=${{{TestVarName}}}")
                .Should().Be("https://example.com?key=s3cret-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestVarName, null);
        }
    }

    [Fact]
    public void Expand_EnvVar_Missing_BecomesEmpty()
    {
        HookSecretExpander.Expand("${ONECODE_HOOK_DEFINITELY_UNDEFINED_VAR_42}/hook")
            .Should().Be("/hook");
    }

    [Fact]
    public void Expand_MultipleVars_AllResolved()
    {
        try
        {
            Environment.SetEnvironmentVariable(TestVarName, "v1");
            Environment.SetEnvironmentVariable($"{TestVarName}_2", "v2");
            HookSecretExpander.Expand($"${{{TestVarName}}}:${{{TestVarName}_2}}")
                .Should().Be("v1:v2");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestVarName, null);
            Environment.SetEnvironmentVariable($"{TestVarName}_2", null);
        }
    }

    [Fact]
    public void Expand_Dpapi_Roundtrip_RecoversPlaintext()
    {
        if (!OperatingSystem.IsWindows())
            return; // DPAPI 为 Windows 专属

        const string secret = "my-plain-secret-值";
        var protectedBase64 = Convert.ToBase64String(
            System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(secret), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));

        HookSecretExpander.Expand($"dpapi:{protectedBase64}").Should().Be(secret);
    }

    [Fact]
    public void Expand_Dpapi_InvalidValue_Throws()
    {
        var act = () => HookSecretExpander.Expand("dpapi:not-base64!!");
        act.Should().Throw<Exception>();
    }
}
