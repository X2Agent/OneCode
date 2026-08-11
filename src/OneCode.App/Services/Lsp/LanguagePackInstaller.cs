using OneCode.Core.Lsp;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Lsp;

/// <summary>Result of an install or uninstall operation.</summary>
public sealed record InstallResult(bool Success, string Message, string? PackId);

/// <summary>
/// Installs and uninstalls LSP server binaries for language packs.
/// Verifies prerequisites, runs install commands, and starts the server to verify the handshake.
/// </summary>
public sealed class LanguagePackInstaller(
    LanguagePackRegistry registry,
    ILspServerManager serverManager,
    IWorkingDirectoryAccessor workingDirectoryAccessor,
    ILogger<LanguagePackInstaller> logger)
{
    /// <summary>
    /// Install a language pack's server binary and verify it starts correctly.
    /// Steps:
    ///   1. Get pack from registry
    ///   2. Check prerequisites (dotnet/node/python etc.)
    ///   3. Check if server binary is already installed (DetectionCommand)
    ///   4. If not installed, run install command (cmd /c on Windows, bash -c on Unix)
    ///   5. Start server and verify initialization handshake
    ///   6. Return result
    /// </summary>
    public async Task<InstallResult> InstallAsync(string packId, CancellationToken ct = default)
    {
        var pack = registry.GetPack(packId);
        if (pack is null)
            return new InstallResult(false, $"Language pack '{packId}' not found. Use '/lsp list' to see available packs.", null);

        if (pack.Install?.Prerequisites is { Length: > 0 } prereqs)
        {
            foreach (var prereq in prereqs)
            {
                if (!await IsCommandAvailableAsync(prereq, ct).ConfigureAwait(false))
                    return new InstallResult(false, $"Missing prerequisite: '{prereq}'. Please install it before installing this language pack.", pack.Id);
            }
        }
        logger.LogInformation("Prerequisites check passed for {PackId}", pack.Id);

        if (pack.Install is not null && !string.IsNullOrWhiteSpace(pack.Install.DetectionCommand))
        {
            if (await IsInstalledAsync(packId, ct).ConfigureAwait(false))
            {
                logger.LogInformation("Server binary for {PackId} is already installed", pack.Id);
            }
            else
            {
                var installCmd = OperatingSystem.IsWindows()
                    ? pack.Install.Windows
                    : pack.Install.Unix;

                if (string.IsNullOrWhiteSpace(installCmd))
                    return new InstallResult(false, $"No install command defined for '{pack.Id}' on this platform.", pack.Id);

                logger.LogInformation("Installing {PackId}: {Command}", pack.Id, installCmd);
                var (exitCode, _, stderr) = await RunShellCommandAsync(installCmd, Constants.Lsp.InstallTimeout, ct).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(stderr) ? "" : stderr.Trim();
                    return new InstallResult(false, $"Installation failed (exit code {exitCode}).{detail}", pack.Id);
                }
                logger.LogInformation("Installation completed for {PackId}", pack.Id);
            }
        }

        // Start server only when the session directory looks like a project of this language.
        // Starting csharp-ls against bin/Debug (or any non-project folder) causes initialize
        // to hang while Roslyn walks parent dirs looking for a solution.
        var sessionWorkingDir = workingDirectoryAccessor.WorkingDirectory;
        if (!LspProjectMatcher.Matches(pack, sessionWorkingDir, logger))
        {
            logger.LogInformation(
                "Server binary for {PackId} is ready; skipping start (no project markers in {Dir})",
                pack.Id, sessionWorkingDir);
            return new InstallResult(
                true,
                $"Language pack '{pack.Id}' installed. Server not started — no project marker files in '{sessionWorkingDir}'. " +
                $"Open a project directory (or pass --workspace) and run '/lsp enable {pack.Id}'.",
                pack.Id);
        }

        var existingStatus = serverManager.GetStatus().FirstOrDefault(s => s.Name == pack.Id);
        if (existingStatus is { IsRunning: true })
        {
            logger.LogInformation("Server for {PackId} is already running", pack.Id);
        }
        else
        {
            try
            {
                var config = pack.ToServerConfig() with { WorkingDirectory = sessionWorkingDir };
                var started = await serverManager.StartServerAsync(config, ct).ConfigureAwait(false);
                if (!started)
                {
                    var detail = $"Command: {config.Command} {string.Join(' ', config.Args)}\nWorkingDir: {sessionWorkingDir}";
                    return new InstallResult(false,
                        $"Server binary installed but server failed to start.\n{detail}\n" +
                        $"Check ~/.onecode/logs/ for detailed server output.", pack.Id);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start server for {PackId}", pack.Id);
                return new InstallResult(false, $"Server failed to start: {ex.Message}", pack.Id);
            }
        }

        // Verify initialization handshake
        var status = serverManager.GetStatus().FirstOrDefault(s => s.Name == pack.Id);
        if (status is not { IsInitialized: true })
            return new InstallResult(false, "Server started but initialization handshake failed.", pack.Id);

        return new InstallResult(true, $"Language pack '{pack.Id}' installed and server started successfully.", pack.Id);
    }

    /// <summary>
    /// Uninstall a language pack — stops the running server.
    /// Note: the server binary itself is not removed.
    /// </summary>
    public async Task<InstallResult> UninstallAsync(string packId, CancellationToken ct = default)
    {
        var pack = registry.GetPack(packId);
        if (pack is null)
            return new InstallResult(false, $"Language pack '{packId}' not found.", null);

        try
        {
            await serverManager.StopServerAsync(pack.Id, ct).ConfigureAwait(false);
            return new InstallResult(true, $"Language pack '{pack.Id}' server stopped (binary not removed).", pack.Id);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"Failed to stop server: {ex.Message}", pack.Id);
        }
    }

    /// <summary>
    /// Check if the server binary for a language pack is installed
    /// by running the pack's DetectionCommand.
    /// </summary>
    public async Task<bool> IsInstalledAsync(string packId, CancellationToken ct = default)
    {
        var pack = registry.GetPack(packId);
        if (pack is null)
            return false;

        if (pack.Install is null || string.IsNullOrWhiteSpace(pack.Install.DetectionCommand))
            return true; // No detection command — assume installed

        try
        {
            var (exitCode, _, _) = await RunShellCommandAsync(
                pack.Install.DetectionCommand, Constants.Lsp.DetectionTimeout, ct).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "IsInstalled check failed for {PackId}", packId);
            return false;
        }
    }

    /// <summary>
    /// Check if a command (e.g. "dotnet", "node") is available on the system PATH.
    /// Uses 'where' on Windows and 'which' on Unix.
    /// </summary>
    private async Task<bool> IsCommandAvailableAsync(string command, CancellationToken ct)
    {
        var checkCmd = OperatingSystem.IsWindows()
            ? $"where {command}"
            : $"which {command}";

        try
        {
            var (exitCode, _, _) = await RunShellCommandAsync(checkCmd, Constants.Lsp.PrereqCheckTimeout, ct).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Prerequisite check failed for '{Command}'", command);
            return false;
        }
    }

    /// <summary>
    /// Run a shell command with output redirection and timeout.
    /// Uses cmd.exe on Windows and bash on Unix.
    /// </summary>
    private async Task<(int ExitCode, string Output, string Error)> RunShellCommandAsync(
        string command, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = "bash";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Start reading streams before waiting to avoid buffer deadlock.
        // CancellationToken.None: the process lifecycle is managed via WaitForExitAsync;
        // reads should complete naturally when the process exits or is killed.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) { logger.LogWarning(ex, "LanguagePackInstaller process kill failed"); }
            // Drain any partial output before returning
            try { await stdoutTask.ConfigureAwait(false); } catch (Exception ex) { logger.LogDebug(ex, "LanguagePackInstaller stdout drain failed"); }
            try { await stderrTask.ConfigureAwait(false); } catch (Exception ex) { logger.LogDebug(ex, "LanguagePackInstaller stderr drain failed"); }

            if (ct.IsCancellationRequested)
                throw; // External cancellation — propagate

            // Timeout
            return (-1, string.Empty, $"Command timed out after {timeout.TotalSeconds}s");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
