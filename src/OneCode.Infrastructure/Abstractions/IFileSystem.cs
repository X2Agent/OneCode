namespace OneCode.Infrastructure.Abstractions;

public interface IFileSystem
{
    Task<string?> ReadTextFileAsync(string path, CancellationToken ct = default);
    Task WriteTextFileAsync(string path, string content, CancellationToken ct = default);
    IReadOnlyList<string> FindFiles(string directory, string? patterns = null, string[]? excludeDirs = null);
    bool MatchesGlob(string filePath, string pattern);
    long GetMtimeMs(string path);
}
