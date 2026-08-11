using Microsoft.Agents.AI;
namespace OneCode.App.Skills;

public static class SubprocessScriptRunner
{
    private static readonly Dictionary<string, string> ExtensionToInterpreter = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "python",
        [".js"] = "node",
        [".sh"] = "bash",
        [".ps1"] = "powershell",
        [".bat"] = "cmd",
        [".cmd"] = "cmd",
    };

    private static readonly Dictionary<string, string> ExtensionToInterpreterArg = new(StringComparer.OrdinalIgnoreCase)
    {
        [".bat"] = "/c",
        [".cmd"] = "/c",
        [".ps1"] = "-ExecutionPolicy Bypass -File",
    };

    /// <summary>
    /// Creates a script runner delegate that captures the logger,
    /// avoiding ServiceLocator access via <c>IServiceProvider.GetService</c> at runtime.
    /// </summary>
    public static AgentFileSkillScriptRunner CreateRunner(ILogger? logger) =>
        (skill, script, arguments, _, ct) => RunCoreAsync(script, arguments, logger, ct);

    private static async Task<object?> RunCoreAsync(
        AgentFileSkillScript script,
        JsonElement? arguments,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var scriptPath = script.FullPath;
        var extension = Path.GetExtension(scriptPath);

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        List<string> args = [];

        if (ExtensionToInterpreter.TryGetValue(extension, out var interpreter))
        {
            psi.FileName = interpreter;

            if (ExtensionToInterpreterArg.TryGetValue(extension, out var interpreterArg))
            {
                foreach (var part in interpreterArg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    args.Add(part);
            }

            args.Add(scriptPath);
        }
        else
        {
            psi.FileName = scriptPath;
        }

        if (arguments is { } argsElement && argsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in argsElement.EnumerateArray())
            {
                var str = arg.ValueKind == JsonValueKind.String
                    ? arg.GetString()
                    : arg.GetRawText();
                if (str is not null)
                    args.Add(str);
            }
        }

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                logger?.LogWarning("Script {Path} exited with code {ExitCode}: {Stderr}", scriptPath, process.ExitCode, stderr);
            }

            return stdout;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "Failed to run script {Path}", scriptPath);
            return $"Error running script: {ex.Message}";
        }
    }
}
