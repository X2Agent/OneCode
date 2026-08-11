using OneCode.Core.Memory;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Memory;

/// <summary>
/// File-system path helpers for the memory subsystem.
/// Used only by <see cref="MemoryEntryStore"/> to resolve physical directories for each
/// <see cref="MemoryScope"/>. Callers outside the store should use
/// <see cref="IMemoryEntryStore"/> with scope parameters, never raw paths.
/// </summary>
public static class MemdirPaths
{
    public static string ConfigDirName => Constants.App.ConfigDirName;
    public const string MemoryDirName = "memory";
    public const string EntrypointFileName = "MEMORY.md";

    public static string NormalizeWorkingDirectory(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Environment.CurrentDirectory;

        return Path.GetFullPath(workingDirectory);
    }

    public static string UserConfigDir => PathsHelper.GetUserConfigDir();

    public static string UserMemoryDir => Path.Combine(UserConfigDir, MemoryDirName);

    public static string UserEntrypointFile => Path.Combine(UserMemoryDir, EntrypointFileName);

    public static string ProjectConfigDir(string workingDirectory) =>
        Path.Combine(NormalizeWorkingDirectory(workingDirectory), ConfigDirName);

    public static string ProjectMemoryDir(string workingDirectory) =>
        Path.Combine(ProjectConfigDir(workingDirectory), MemoryDirName);

    public static string ProjectEntrypointFile(string workingDirectory) =>
        Path.Combine(ProjectMemoryDir(workingDirectory), EntrypointFileName);
}
