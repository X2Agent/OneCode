using Microsoft.Agents.AI;
using OneCode.Infrastructure.Config;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Resolves the store root for Harness session working memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the Harness default.</b> When no store is supplied, Harness roots working memory at
/// <c>{Directory.GetCurrentDirectory()}/agent-file-memory</c>. That is the <b>process</b> directory,
/// not the session's project: switching projects with <c>/cd</c> or opening a different workspace would
/// silently read and write a different working-memory tree, and two sessions in different projects
/// running from the same process directory would collide.
/// </para>
/// <para>
/// The root is therefore derived from the session's working directory and kept inside the project's own
/// <c>.onecode</c> directory, next to the long-term <c>MEMORY.md</c>. The two stay separate
/// (<c>agent-file-memory</c> vs <c>memory</c>) because they hold different things: raw working notes
/// versus curated knowledge.
/// </para>
/// </remarks>
public static class FileMemoryStorePaths
{
    /// <summary>Subdirectory of <c>.onecode</c> that holds session working memory.</summary>
    public const string WorkingMemoryDirName = "agent-file-memory";

    /// <summary>
    /// Returns the working-memory root for a project.
    /// </summary>
    /// <param name="workingDirectory">The session's project directory.</param>
    /// <returns>
    /// <c>{workingDirectory}/.onecode/agent-file-memory</c>, normalized to an absolute path.
    /// </returns>
    public static string ResolveRoot(string workingDirectory)
    {
        var normalized = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(workingDirectory);

        return Path.Combine(normalized, Constants.App.ConfigDirName, WorkingMemoryDirName);
    }

    /// <summary>
    /// Creates the working-memory store for a project.
    /// </summary>
    /// <param name="workingDirectory">The session's project directory.</param>
    /// <remarks>
    /// The directory itself is created lazily by the store on first write, so a session that never uses
    /// working memory leaves no trace on disk.
    /// </remarks>
    public static AgentFileStore CreateStore(string workingDirectory)
        => new FileSystemAgentFileStore(ResolveRoot(workingDirectory));
}
