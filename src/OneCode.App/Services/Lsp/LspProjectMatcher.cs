using OneCode.Core.Lsp;

namespace OneCode.App.Services.Lsp;

/// <summary>
/// Detects whether a working directory looks like a project for a given language pack
/// by matching top-level <see cref="LanguagePack.ProjectFiles"/> globs.
/// </summary>
internal static class LspProjectMatcher
{
    /// <summary>
    /// Returns true when any configured project-marker glob matches a file at the
    /// top level of <paramref name="workingDir"/>.
    /// </summary>
    public static bool Matches(LanguagePack pack, string workingDir, ILogger? logger = null)
    {
        if (pack.ProjectFiles is null || pack.ProjectFiles.Length == 0)
            return false;

        if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
            return false;

        foreach (var pattern in pack.ProjectFiles)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            try
            {
                if (Directory.GetFiles(workingDir, pattern, SearchOption.TopDirectoryOnly).Length > 0)
                    return true;
            }
            catch (DirectoryNotFoundException ex)
            {
                logger?.LogTrace(ex, "Project marker scan skipped {Pattern}: dir vanished", pattern);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger?.LogTrace(ex, "Project marker scan skipped {Pattern}: no permission", pattern);
            }
            catch (IOException ex)
            {
                logger?.LogTrace(ex, "Project marker scan skipped {Pattern}: transient IO error", pattern);
            }
        }

        return false;
    }
}
