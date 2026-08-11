using CliWrap;
using CliWrap.Buffered;
namespace OneCode.App.Services.Hooks;

/// <summary>
/// Command 类型 Hook 执行器——通过 CliWrap 执行外部进程
///
/// Exit code 语义：
/// - 0: 成功
/// - 2: 阻断
/// - 其他: 非阻断错误
/// </summary>
public sealed class CommandHookExecutor : IHookExecutor
{
    private readonly ILogger<CommandHookExecutor> _logger;

    public CommandHookExecutor(ILogger<CommandHookExecutor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public HookType Type => HookType.Command;

    public async Task<HookResult?> ExecuteAsync(
        HookPayload payload, HookConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
        {
            _logger.LogWarning("Command hook has no command specified");
            return null;
        }

        var command = config.Command;
        var shell = GetShell();
        var shellArg = GetShellArgument();
        var timeoutMs = config.TimeoutMs ?? 5000;

        var payloadJson = JsonSerializer.Serialize(payload, HookSerializerContext.Default.HookPayload);

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var result = await CliWrap.Cli.Wrap(shell)
                .WithArguments([shellArg, command])
                .WithStandardInputPipe(PipeSource.FromString(payloadJson))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(linkedCts.Token).ConfigureAwait(false);

            if (result.ExitCode == 2)
            {
                return new HookResult
                {
                    Outcome = HookOutcome.Blocking,
                    Message = result.StandardError,
                    BlockingError = new HookBlockingError(result.StandardError, command),
                };
            }

            if (result.ExitCode != 0)
            {
                return new HookResult
                {
                    Outcome = HookOutcome.NonBlockingError,
                    Message = result.StandardError,
                };
            }

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
                return null;

            return ParseStdoutResult(result.StandardOutput);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = $"Command hook timed out after {timeoutMs}ms",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Command hook execution failed");
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = $"Hook execution error: {ex.Message}",
            };
        }
    }

    private HookResult? ParseStdoutResult(string stdout)
    {
        try
        {
            return JsonSerializer.Deserialize(stdout, HookSerializerContext.Default.HookResult);
        }
        catch (JsonException ex)
        {
            // hook stdout 不是合法 JSON → 降级为纯文本消息。降级必须留痕，
            // 否则 hook 作者拿不到任何格式错误反馈。
            _logger.LogDebug(ex, "Hook stdout is not valid JSON — degrading to plain-text message");
            return new HookResult { Message = stdout };
        }
    }

    private static string GetShell() =>
        OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static string GetShellArgument() =>
        OperatingSystem.IsWindows() ? "/c" : "-c";
}
