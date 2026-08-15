using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Hooks;
using OneCode.Core.Hooks;
using OneCode.Core.Hooks.Notifications;

namespace OneCode.Tests;

public sealed class NotificationHookExecutorTests
{
    private static readonly HookPayload SamplePayload = new()
    {
        Event = HookEvent.UserPromptSubmit,
        SessionId = "sess-123",
        Cwd = "/home/user/project",
        ToolName = "Bash",
        UserMessage = "Fix the bug",
        AgentId = "agent-001",
        AgentType = "coder",
        Timestamp = new DateTimeOffset(2026, 6, 20, 10, 30, 0, TimeSpan.FromHours(8)),
    };

    // Provider 解析

    [Fact]
    public async Task ExecuteAsync_MissingProvider_ReturnsNonBlockingError()
    {
        var sut = CreateSut();
        var config = new HookConfig { WebhookUrl = "https://example.com/hook" };

        var result = await sut.ExecuteAsync(SamplePayload, config, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(HookOutcome.NonBlockingError);
        result.Message.Should().Contain("provider");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownProvider_ReturnsNonBlockingError()
    {
        var sut = CreateSut();
        var config = new HookConfig { Provider = "nonexistent", WebhookUrl = "https://example.com/hook" };

        var result = await sut.ExecuteAsync(SamplePayload, config, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(HookOutcome.NonBlockingError);
        result.Message.Should().Contain("nonexistent");
    }

    [Fact]
    public async Task ExecuteAsync_MissingWebhookUrl_ReturnsNonBlockingError()
    {
        var provider = CreateMockProvider("feishu");
        var sut = CreateSut(provider);
        var config = new HookConfig { Provider = "feishu" };

        var result = await sut.ExecuteAsync(SamplePayload, config, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(HookOutcome.NonBlockingError);
        result.Message.Should().Contain("webhookUrl");
    }

    // 成功路径

    [Fact]
    public async Task ExecuteAsync_ProviderSucceeds_ReturnsNull()
    {
        var provider = CreateMockProvider("feishu", NotificationSendResult.Ok());
        var sut = CreateSut(provider);
        var config = new HookConfig
        {
            Provider = "feishu",
            WebhookUrl = "https://example.com/hook",
            Message = "Hello {{Event}}",
        };

        var result = await sut.ExecuteAsync(SamplePayload, config, TestContext.Current.CancellationToken);

        result.Should().BeNull();
        await provider.Received(1).SendAsync(
            Arg.Is<NotificationMessage>(m => m.Text == "Hello UserPromptSubmit"),
            "https://example.com/hook",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // Provider 失败路径

    [Fact]
    public async Task ExecuteAsync_ProviderReturnsFail_ReturnsNonBlockingError()
    {
        var provider = CreateMockProvider("feishu",
            NotificationSendResult.Fail("webhook rejected", 400));
        var sut = CreateSut(provider);
        var config = new HookConfig
        {
            Provider = "feishu",
            WebhookUrl = "https://example.com/hook",
        };

        var result = await sut.ExecuteAsync(SamplePayload, config, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(HookOutcome.NonBlockingError);
        result.Message.Should().Contain("webhook rejected");
    }

    [Fact]
    public async Task ExecuteAsync_ProviderThrowsException_ReturnsNonBlockingError()
    {
        var provider = CreateMockProvider("feishu");
        provider.SendAsync(Arg.Any<NotificationMessage>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<NotificationSendResult>(new InvalidOperationException("network unreachable")));
        var sut = CreateSut(provider);
        var config = new HookConfig
        {
            Provider = "feishu",
            WebhookUrl = "https://example.com/hook",
        };

        var result = await sut.ExecuteAsync(SamplePayload, config, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(HookOutcome.NonBlockingError);
        result.Message.Should().Contain("network unreachable");
    }

    // 超时处理

    [Fact]
    public async Task ExecuteAsync_TimeoutMsElapses_ReturnsTimeoutError()
    {
        var provider = CreateMockProvider("feishu");
        provider.SendAsync(Arg.Any<NotificationMessage>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                // 模拟 Provider 永远不返回，直到被取消
                await Task.Delay(10_000, ci.Arg<CancellationToken>());
                return NotificationSendResult.Ok();
            });
        var sut = CreateSut(provider);
        var config = new HookConfig
        {
            Provider = "feishu",
            WebhookUrl = "https://example.com/hook",
            TimeoutMs = 50, // 50ms 超时
        };

        var result = await sut.ExecuteAsync(SamplePayload, config, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(HookOutcome.NonBlockingError);
        result.Message.Should().Contain("timed out").And.Contain("50");
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutMsNull_AppliesDefaultTimeout()
    {
        CancellationToken? providerToken = null;
        var provider = CreateMockProvider("feishu");
        provider.SendAsync(Arg.Any<NotificationMessage>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                providerToken = ci.Arg<CancellationToken>();
                return NotificationSendResult.Ok();
            });
        var sut = CreateSut(provider);
        var config = new HookConfig
        {
            Provider = "feishu",
            WebhookUrl = "https://example.com/hook",
            TimeoutMs = null, // 显式 null → 应回落到默认超时（5000ms）
        };

        // 外部 ct 传 None：捕获到的 token 若可取消，唯一来源就是默认超时 CTS
        await sut.ExecuteAsync(SamplePayload, config, CancellationToken.None);

        providerToken.Should().NotBeNull();
        providerToken!.Value.CanBeCanceled.Should().BeTrue(
            "TimeoutMs=null 必须应用默认超时 CTS，而不是把 CancellationToken.None 传给 Provider");
    }

    // CancellationToken 传播

    [Fact]
    public async Task ExecuteAsync_ExternalCancellation_PropagatesOperationCanceledException()
    {
        var provider = CreateMockProvider("feishu");
        provider.SendAsync(Arg.Any<NotificationMessage>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await Task.Delay(10_000, ci.Arg<CancellationToken>());
                return NotificationSendResult.Ok();
            });
        var sut = CreateSut(provider);
        var config = new HookConfig
        {
            Provider = "feishu",
            WebhookUrl = "https://example.com/hook",
            TimeoutMs = 10_000, // 超时设长，确保外部取消先触发
        };
        using var cts = new CancellationTokenSource();

        var act = async () => await sut.ExecuteAsync(SamplePayload, config, cts.Token);
        cts.CancelAfter(50);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // 模板渲染

    [Theory]
    [InlineData("{{Event}}", "UserPromptSubmit")]
    [InlineData("{{SessionId}}", "sess-123")]
    [InlineData("{{Cwd}}", "/home/user/project")]
    [InlineData("{{ToolName}}", "Bash")]
    [InlineData("{{UserMessage}}", "Fix the bug")]
    [InlineData("{{AgentId}}", "agent-001")]
    [InlineData("{{AgentType}}", "coder")]
    public void RenderTemplate_SingleField_ReplacesCorrectly(string template, string expected)
    {
        var rendered = HookTemplateRenderer.Render(template, SamplePayload);
        rendered.Should().Be(expected);
    }

    [Fact]
    public void RenderTemplate_Timestamp_FormatsAsDateTime()
    {
        var rendered = HookTemplateRenderer.Render("{{Timestamp}}", SamplePayload);
        rendered.Should().Be("2026-06-20 10:30:00");
    }

    [Fact]
    public void RenderTemplate_MultipleFields_ReplacesAll()
    {
        var template = "[{{Event}}] {{AgentType}} {{AgentId}}: {{UserMessage}}";
        var rendered = HookTemplateRenderer.Render(template, SamplePayload);
        rendered.Should().Be("[UserPromptSubmit] coder agent-001: Fix the bug");
    }

    [Fact]
    public void RenderTemplate_UnknownField_KeepsOriginalPlaceholder()
    {
        var template = "Hello {{UnknownField}} world";
        var rendered = HookTemplateRenderer.Render(template, SamplePayload);
        rendered.Should().Be("Hello {{UnknownField}} world");
    }

    [Fact]
    public void RenderTemplate_NullPayloadField_ReplacesWithEmptyString()
    {
        var payload = SamplePayload with { UserMessage = null, AgentId = null };
        var rendered = HookTemplateRenderer.Render("msg=[{{UserMessage}}] agent=[{{AgentId}}]", payload);
        rendered.Should().Be("msg=[] agent=[]");
    }

    [Fact]
    public void RenderTemplate_EmptyTemplate_ReturnsEmpty()
    {
        var rendered = HookTemplateRenderer.Render(string.Empty, SamplePayload);
        rendered.Should().BeEmpty();
    }

    [Fact]
    public void RenderTemplate_NullTemplate_ReturnsNull()
    {
        var rendered = HookTemplateRenderer.Render(null!, SamplePayload);
        rendered.Should().BeNull();
    }

    [Fact]
    public void RenderTemplate_NoPlaceholders_ReturnsOriginal()
    {
        var template = "plain text without placeholders";
        var rendered = HookTemplateRenderer.Render(template, SamplePayload);
        rendered.Should().Be(template);
    }

    // Provider 名称大小写不敏感

    [Fact]
    public async Task ExecuteAsync_ProviderNameCaseInsensitive_MatchesProvider()
    {
        var provider = CreateMockProvider("feishu", NotificationSendResult.Ok());
        var sut = CreateSut(provider);
        var config = new HookConfig
        {
            Provider = "FEISHU", // 大写
            WebhookUrl = "https://example.com/hook",
        };

        var result = await sut.ExecuteAsync(SamplePayload, config, TestContext.Current.CancellationToken);

        result.Should().BeNull();
        await provider.Received(1).SendAsync(
            Arg.Any<NotificationMessage>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // Helpers

    private static NotificationHookExecutor CreateSut(params INotificationProvider[] providers)
    {
        return new NotificationHookExecutor(providers, NullLogger<NotificationHookExecutor>.Instance);
    }

    private static INotificationProvider CreateMockProvider(
        string name, NotificationSendResult? result = null)
    {
        var provider = Substitute.For<INotificationProvider>();
        provider.Name.Returns(name);
        if (result is not null)
        {
            provider.SendAsync(
                Arg.Any<NotificationMessage>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
                .Returns(result);
        }
        return provider;
    }
}
