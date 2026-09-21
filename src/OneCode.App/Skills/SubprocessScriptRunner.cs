using System.Diagnostics;
using Microsoft.Agents.AI;

namespace OneCode.App.Skills;

/// <summary>
/// Runs file-based skill scripts as child processes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a sandbox.</b> Approval to run a script means the user agreed to run it; it says nothing about
/// isolation. The child process has the same filesystem and network access as the agent itself.
/// </para>
/// <para>
/// <b>Bounded.</b> A script that hangs, floods stdout or exits non-zero must not be able to wedge the
/// agent or masquerade as success. Each of those has an explicit outcome: a timeout, a truncation marker,
/// or an error result carrying the exit code.
/// </para>
/// </remarks>
public static class SubprocessScriptRunner
{
    /// <summary>Maximum time a single script may run before it is killed.</summary>
    /// <remarks>
    /// Scripts are helper steps inside a tool call, not long-running jobs. A hung interpreter would
    /// otherwise hold the tool call open until the user cancels the whole run.
    /// </remarks>
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Maximum characters of stdout returned to the model.</summary>
    private const int MaxOutputChars = 30_000;

    /// <summary>Maximum characters of stderr echoed into a failure result.</summary>
    private const int MaxErrorChars = 2_000;

    /// <summary>Marker appended when output was cut short, so truncation is visible to the model.</summary>
    private const string TruncationMarker = "\n… [output truncated]";

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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ScriptTimeout);

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
                return ToolFailure(scriptPath, "the process could not be started.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                // A non-zero exit is a failure, not a result. Returning stdout here would let the
                // model treat a crashed script's partial output as the answer.
                logger?.LogWarning(
                    "Skill script {Path} exited with code {ExitCode}",
                    scriptPath, process.ExitCode);
                return ToolFailure(scriptPath, $"exit code {process.ExitCode}. {Bound(stderr, MaxErrorChars)}");
            }

            return Bound(stdout, MaxOutputChars);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation stays cancellation — the agent run is stopping.
            KillProcessTree(process, logger, scriptPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Only the timeout fired. Kill the tree before reporting: the interpreter may have spawned
            // children that would otherwise keep running after the tool call returned.
            KillProcessTree(process, logger, scriptPath);
            logger?.LogWarning("Skill script {Path} timed out after {Seconds}s", scriptPath, ScriptTimeout.TotalSeconds);
            return ToolFailure(scriptPath, $"timed out after {ScriptTimeout.TotalSeconds:F0}s.");
        }
        catch (Exception ex)
        {
            KillProcessTree(process, logger, scriptPath);
            logger?.LogError(ex, "Failed to run script {Path}", scriptPath);
            return ToolFailure(scriptPath, "the process could not be run.");
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Kills the child process and its descendants.
    /// </summary>
    /// <remarks>
    /// Cancelling the wait or disposing the <see cref="Process"/> only detaches from the child; the
    /// interpreter keeps running. <c>Kill(entireProcessTree: true)</c> is what actually terminates it.
    /// </remarks>
    private static void KillProcessTree(Process? process, ILogger? logger, string scriptPath)
    {
        if (process is null || process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // The process may have exited between the check and the kill; nothing actionable remains.
            logger?.LogDebug(ex, "Failed to kill skill script {Path}", scriptPath);
        }
    }

    private static string ToolFailure(string scriptPath, string reason) =>
        $"Error running script '{Path.GetFileName(scriptPath)}': {reason}";

    /// <summary>Bounds a value and marks the cut so the model knows the output is incomplete.</summary>
    private static string Bound(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + TruncationMarker;
}
