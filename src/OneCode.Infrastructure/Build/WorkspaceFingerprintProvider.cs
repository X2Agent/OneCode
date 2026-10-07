using System.Security.Cryptography;
using OneCode.Core.Build;

namespace OneCode.Infrastructure.Build;

/// <summary>
/// Computes a deterministic workspace fingerprint from relative paths and file lengths.
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
            // B5：指纹只含相对路径 + 文件长度，不含 mtime——mtime 入哈希会让
            // 「用户仅保存未改内容」的文件漂移指纹，Goal 的恢复与发布被永久卡死。
            // 代价：等长内容替换不会漂移指纹，恢复/发布守卫不拦（build/test 门禁只覆盖
            // 步骤自身 ChangedFiles，兜不住目标工作区的外部漂移）。这是有意取舍，非遗漏。
            var entry = $"{relative}\0{info.Length}\n";
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
