using System.Security.Cryptography;
using OneCode.Core.Build;

namespace OneCode.Infrastructure.Build;

/// <summary>
/// Computes a deterministic workspace fingerprint from relative paths, lengths and UTC write times.
/// Build artifacts and VCS metadata are excluded so recovery checks represent user-source changes.
/// </summary>
public sealed class WorkspaceFingerprintProvider : IWorkspaceFingerprintProvider
{
    private static readonly HashSet<string> s_excludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".onecode", ".workbuddy", "bin", "obj", "node_modules", "outputs",
    };

    public Task<string> ComputeAsync(string workingDirectory, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Build workspace '{root}' does not exist.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateFiles(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var entry = $"{relative}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(entry));
        }

        return Task.FromResult(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if (!s_excludedDirectories.Contains(Path.GetFileName(directory)))
                    pending.Push(directory);
            }
            foreach (var file in Directory.EnumerateFiles(current))
                yield return file;
        }
    }
}
