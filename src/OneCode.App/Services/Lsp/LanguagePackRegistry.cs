using OneCode.Core.Lsp;
using OneCode.Infrastructure;

namespace OneCode.App.Services.Lsp;

/// <summary>
/// Discovers and manages language packs.
/// Loads built-in packs from <see cref="BuiltInLanguagePacks"/> and user-defined packs
/// from JSON files in ~/.onecode/lsp/. Maintains an extension→packId map for routing
/// file-based requests to the correct server.
/// </summary>
public sealed class LanguagePackRegistry(ILogger<LanguagePackRegistry> logger)
{
    private readonly ConcurrentDictionary<string, LanguagePack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _extensionMap = new(StringComparer.OrdinalIgnoreCase);
    private int _initialized;

    /// <summary>Directory containing user-defined language pack JSON files.</summary>
    public static string UserPackDirectory => Path.Combine(
        PathsHelper.GetUserConfigDir(), "lsp");

    /// <summary>
    /// Ensures built-in and user packs have been loaded.
    /// Thread-safe via CompareExchange — loads exactly once.
    /// </summary>
    private void EnsureInitialized()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        foreach (var pack in BuiltInLanguagePacks.All)
            RegisterPackInternal(pack);

        LoadUserPacks();
    }

    /// <summary>Register or replace a language pack.</summary>
    public void RegisterPack(LanguagePack pack)
    {
        EnsureInitialized();
        RegisterPackInternal(pack);
    }

    private void RegisterPackInternal(LanguagePack pack)
    {
        _packs[pack.Id] = pack;
        foreach (var ext in pack.Extensions)
            _extensionMap[ext.ToLowerInvariant()] = pack.Id;
    }

    /// <summary>Get all registered language packs.</summary>
    public IReadOnlyList<LanguagePack> GetAllPacks()
    {
        EnsureInitialized();
        return _packs.Values.ToList();
    }

    /// <summary>Get a language pack by id. Returns null if not found.</summary>
    public LanguagePack? GetPack(string packId)
    {
        EnsureInitialized();
        return _packs.TryGetValue(packId, out var pack) ? pack : null;
    }

    /// <summary>
    /// Resolve a language pack by file path (based on extension).
    /// Returns null if no pack handles this file type.
    /// </summary>
    public LanguagePack? ResolvePackByFilePath(string filePath)
    {
        EnsureInitialized();
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return null;
        return _extensionMap.TryGetValue(ext.ToLowerInvariant(), out var packId)
            ? GetPack(packId)
            : null;
    }

    /// <summary>
    /// Resolve the LSP server name for a file path.
    /// The server name equals the pack id (see <see cref="LanguagePack.ToServerConfig"/>).
    /// Returns null if no pack handles this file type.
    /// </summary>
    public string? ResolveServerName(string filePath) => ResolvePackByFilePath(filePath)?.Id;

    /// <summary>Load user-defined language packs from ~/.onecode/lsp/*.json</summary>
    private void LoadUserPacks()
    {
        var dir = UserPackDirectory;
        if (!Directory.Exists(dir))
            return;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var pack = JsonSerializer.Deserialize<LanguagePack>(json, options);
                if (pack is not null)
                    RegisterPackInternal(pack);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load language pack from {Path}", file);
            }
        }
    }
}
