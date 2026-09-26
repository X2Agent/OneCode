using OneCode.Core.Prompt;

namespace OneCode.Infrastructure.Prompt;

public sealed class FilePromptStore(string basePath, string fileExtension = ".prompt") : IPromptStore
{
    private readonly string _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
    private readonly string _fileExtension = NormalizeExtension(fileExtension);

    private static string NormalizeExtension(string extension)
        => extension.StartsWith('.') ? extension : "." + extension;

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        var filePath = GetFilePath(name);
        if (!File.Exists(filePath))
            return null;

        return await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        string category, CancellationToken ct = default)
    {
        var categoryPath = Path.Combine(_basePath, category);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(categoryPath))
            return result;

        foreach (var file in Directory.GetFiles(categoryPath, "*" + _fileExtension))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            result[name] = content;
        }

        return result;
    }

    public Task<bool> ExistsAsync(string name, CancellationToken ct = default)
    {
        var filePath = GetFilePath(name);
        return Task.FromResult(File.Exists(filePath));
    }

    private string GetFilePath(string name)
    {
        var normalized = name.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_basePath, normalized + _fileExtension);
    }
}
