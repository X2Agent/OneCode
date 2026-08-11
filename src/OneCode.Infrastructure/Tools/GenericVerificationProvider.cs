using OneCode.Core.Tools;
using OneCode.Infrastructure.Abstractions;

namespace OneCode.Infrastructure.Tools;

/// <summary>
/// 通用验证提供者——按 <see cref="VerificationProfile"/> 配置驱动，支持多语言验证。
/// </summary>
/// <remarks>
/// 替代原 <c>DotNetCompilationChecker</c>。通过注入的 profile 列表自动路由：
/// 按 modifiedFiles 扩展名 + 工作目录项目标记文件双重判断，匹配到的 profile 执行验证命令并解析错误。
/// 编译器/构建工具的单行输出格式由 profile.ErrorPattern 描述（多行输出应使用 --message-format=short 等 flag）。
/// </remarks>
public sealed class GenericVerificationProvider : IVerificationProvider
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<GenericVerificationProvider> _logger;
    private readonly IReadOnlyList<VerificationProfile> _profiles;
    private readonly Dictionary<string, VerificationProfile> _extensionMap;
    private readonly Dictionary<string, Regex> _errorRegexByProfileName;

    public GenericVerificationProvider(
        IProcessRunner processRunner,
        ILogger<GenericVerificationProvider> logger,
        IReadOnlyList<VerificationProfile>? profiles = null)
    {
        _processRunner = processRunner;
        _logger = logger;
        _profiles = profiles ?? VerificationProfile.BuiltIn;

        _extensionMap = new Dictionary<string, VerificationProfile>(StringComparer.OrdinalIgnoreCase);
        _errorRegexByProfileName = new Dictionary<string, Regex>(StringComparer.Ordinal);
        foreach (var profile in _profiles)
        {
            foreach (var ext in profile.FileExtensions)
                _extensionMap[ext.ToLowerInvariant()] = profile;

            // 预编译错误解析正则，避免每次 VerifyAsync 调用时重新编译
            _errorRegexByProfileName[profile.Name] = new Regex(
                profile.ErrorPattern,
                RegexOptions.Multiline | RegexOptions.Compiled);
        }
    }

    /// <inheritdoc />
    public bool IsSourceFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return false;
        return _extensionMap.ContainsKey(ext);
    }

    /// <inheritdoc />
    public Task<VerificationResult> VerifyAsync(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles,
        CancellationToken ct = default)
        => VerifyCoreAsync(workingDirectory, modifiedFiles, includeTests: false, ct);

    /// <inheritdoc />
    public Task<VerificationResult> VerifyTestsAsync(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles,
        CancellationToken ct = default)
        => VerifyTestsCoreAsync(workingDirectory, modifiedFiles, ct);

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyBuildAndTestsAsync(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles,
        CancellationToken ct = default)
    {
        var build = await VerifyAsync(workingDirectory, modifiedFiles, ct).ConfigureAwait(false);
        if (!build.Success || build.Skipped)
            return build;

        var tests = await VerifyTestsAsync(workingDirectory, modifiedFiles, ct).ConfigureAwait(false);
        return MergeResults([build, tests]);
    }

    private async Task<VerificationResult> VerifyCoreAsync(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles,
        bool includeTests,
        CancellationToken ct)
    {
        var matchedProfiles = ResolveProfiles(workingDirectory, modifiedFiles);
        if (matchedProfiles.Count == 0)
            return new VerificationResult { Success = true, Skipped = true, Errors = [] };

        var results = new List<VerificationResult>();
        foreach (var profile in matchedProfiles)
        {
            var buildResult = await RunProfileAsync(profile, workingDirectory, ct).ConfigureAwait(false);
            results.Add(buildResult);
            if (!buildResult.Success || buildResult.Skipped || !includeTests)
                continue;

            results.Add(await RunTestProfileAsync(profile, workingDirectory, ct).ConfigureAwait(false));
        }

        return MergeResults(results);
    }

    private async Task<VerificationResult> VerifyTestsCoreAsync(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles,
        CancellationToken ct)
    {
        var matchedProfiles = ResolveProfiles(workingDirectory, modifiedFiles);
        if (matchedProfiles.Count == 0)
            return new VerificationResult { Success = true, Skipped = true, Errors = [] };

        var results = new List<VerificationResult>(matchedProfiles.Count);
        foreach (var profile in matchedProfiles)
            results.Add(await RunTestProfileAsync(profile, workingDirectory, ct).ConfigureAwait(false));
        return MergeResults(results);
    }

    private Task<VerificationResult> RunTestProfileAsync(
        VerificationProfile profile,
        string workingDirectory,
        CancellationToken ct)
    {
        if (profile.TestCommand is null || profile.TestArgs is null)
        {
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Errors = [new VerificationError("(test)", 0, 0, "error", $"{profile.Name} test command is not configured.")],
            });
        }

        return RunCommandAsync(
            profile,
            profile.TestCommand,
            profile.TestArgs,
            "test",
            workingDirectory,
            ct);
    }

    private HashSet<VerificationProfile> ResolveProfiles(
        string workingDirectory,
        IReadOnlyList<string> modifiedFiles)
    {
        var matchedProfiles = new HashSet<VerificationProfile>();
        foreach (var file in modifiedFiles)
        {
            var ext = Path.GetExtension(file);
            if (string.IsNullOrEmpty(ext))
                continue;
            if (_extensionMap.TryGetValue(ext.ToLowerInvariant(), out var profile)
                && HasProjectMarker(workingDirectory, profile.ProjectMarkers))
            {
                matchedProfiles.Add(profile);
            }
        }

        return matchedProfiles;
    }

    private static VerificationResult MergeResults(IReadOnlyList<VerificationResult> results) => new()
    {
        Success = results.All(result => result.Success),
        Skipped = results.All(result => result.Skipped),
        Errors = results.SelectMany(result => result.Errors).ToList(),
        Duration = TimeSpan.FromTicks(results.Sum(result => result.Duration.Ticks)),
    };

    private Task<VerificationResult> RunProfileAsync(
        VerificationProfile profile,
        string workingDirectory,
        CancellationToken ct)
        => RunCommandAsync(profile, profile.Command, profile.Args, "build", workingDirectory, ct);

    private async Task<VerificationResult> RunCommandAsync(
        VerificationProfile profile,
        string command,
        IReadOnlyList<string> args,
        string stage,
        string workingDirectory,
        CancellationToken ct)
    {
        var availabilityCmd = profile.AvailabilityCommand ?? command;
        if (!await _processRunner.CommandExistsAsync(availabilityCmd).ConfigureAwait(false))
        {
            _logger.LogWarning("{ProfileName} {Stage} command '{Command}' not found, skipping verification",
                profile.Name, stage, availabilityCmd);
            return new VerificationResult { Success = true, Skipped = true, Errors = [] };
        }

        var started = DateTimeOffset.UtcNow;

        ProcessResult? result;
        try
        {
            result = await _processRunner.ExecuteWithTimeoutAsync(
                command,
                args.ToArray(),
                workingDirectory: workingDirectory,
                timeoutMs: profile.TimeoutMs,
                ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ProfileName} {Stage} invocation failed", profile.Name, stage);
            return new VerificationResult
            {
                Success = false,
                Errors = [new VerificationError($"({stage})", 0, 0, "error", $"{profile.Name} {stage} failed: {ex.Message}")],
                Duration = DateTimeOffset.UtcNow - started,
            };
        }

        if (result is null)
        {
            return new VerificationResult
            {
                Success = false,
                Errors = [new VerificationError($"({stage})", 0, 0, "error", $"{profile.Name} {stage} returned null result.")],
                Duration = DateTimeOffset.UtcNow - started,
            };
        }

        if (result.TimedOut)
        {
            _logger.LogWarning("{ProfileName} {Stage} timed out after {TimeoutMs}ms",
                profile.Name, stage, profile.TimeoutMs);
            return new VerificationResult
            {
                Success = false,
                Errors = [new VerificationError($"({stage})", 0, 0, "error", $"{profile.Name} {stage} timed out after {profile.TimeoutMs}ms.")],
                Duration = DateTimeOffset.UtcNow - started,
            };
        }

        var errors = ParseErrors(profile, result.Stdout, result.Stderr, stage);
        var success = result.ExitCode == 0;
        if (!success && errors.Count == 0)
        {
            errors.Add(new VerificationError(
                $"({stage})",
                0,
                0,
                "error",
                $"{profile.Name} {stage} exited with code {result.ExitCode}."));
        }

        return new VerificationResult
        {
            Success = success,
            Errors = errors,
            Duration = DateTimeOffset.UtcNow - started,
        };
    }

    /// <summary>
    /// 按 profile.ErrorPattern 解析命令输出中的错误。
    /// </summary>
    private List<VerificationError> ParseErrors(
        VerificationProfile profile,
        string stdout,
        string stderr,
        string stage)
    {
        var combined = string.IsNullOrEmpty(stderr) ? stdout : stdout + "\n" + stderr;
        if (string.IsNullOrWhiteSpace(combined))
            return [];

        var errors = new List<VerificationError>();
        var regex = _errorRegexByProfileName[profile.Name];

        foreach (Match match in regex.Matches(combined))
        {
            var file = match.Groups["file"].Value;
            var line = int.TryParse(match.Groups["line"].Value, out var l) ? l : 0;
            var col = int.TryParse(match.Groups["col"].Value, out var c) ? c : 0;
            var severity = match.Groups["severity"].Success ? match.Groups["severity"].Value : "error";
            var message = match.Groups["message"].Value;
            if (match.Groups["code"].Success)
                message = $"[{match.Groups["code"].Value}] {message}";

            errors.Add(new VerificationError(file, line, col, severity, message));
        }

        // 如果没有匹配到格式化错误但 exit code 非零，把原始输出作为一条错误
        if (errors.Count == 0 && !string.IsNullOrWhiteSpace(combined))
        {
            var firstErrorLine = combined
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase));

            if (firstErrorLine is not null)
                errors.Add(new VerificationError($"({stage})", 0, 0, "error", firstErrorLine.Trim()));
        }

        return errors;
    }

    /// <summary>
    /// 检查工作目录（向上递归到根）是否存在任一标记文件。支持 glob 通配符。
    /// 复用 <see cref="ProjectCommandDetector.HasMarker"/> 保持单一实现。
    /// </summary>
    internal static bool HasProjectMarker(string workingDirectory, IReadOnlySet<string> markers)
        => ProjectCommandDetector.HasMarker(workingDirectory, [.. markers]);
}
