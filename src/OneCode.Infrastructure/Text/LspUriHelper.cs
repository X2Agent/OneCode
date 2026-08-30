namespace OneCode.Infrastructure.Text;

/// <summary>
/// LSP file:// URI ⇄ file path conversion helpers.
///
/// Centralises URI↔path logic used across
/// <c>LspTool</c>, <c>EnhancedLspService</c>, <c>LspNotifier</c>,
/// <c>SymbolSearchTool</c>, <c>FindReferencesTool</c>, and
/// <c>ApplyWorkspaceEditTool</c>.
/// </summary>
public static class LspUriHelper
{
    /// <summary>
    /// Build a proper <c>file://</c> URI from a file path.
    /// Handles Windows drive letters (<c>C:\</c> → <c>file:///C:/</c>) and
    /// Unix paths (<c>/home</c> → <c>file:///home</c>).
    /// If the input already starts with <c>file://</c> it is returned unchanged.
    /// </summary>
    public static string BuildFileUri(string filePath)
    {
        if (filePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return filePath;

        var normalized = filePath.Replace('\\', '/');

        // Windows absolute path (e.g., C:/Users/...) needs file:///C:/...
        if (normalized.Length >= 2 && normalized[1] == ':')
            return $"file:///{normalized}";

        // Unix absolute path
        if (normalized.StartsWith('/'))
            return $"file://{normalized}";

        return $"file://{normalized}";
    }

    /// <summary>
    /// Convert a <c>file://</c> URI back to a platform-native file path.
    /// Strips the <c>file://</c> authority prefix and converts
    /// forward slashes to <see cref="Path.DirectorySeparatorChar"/>.
    /// Unix 路径保留前导 '/'（<c>file:///tmp/a</c> → <c>/tmp/a</c>）；
    /// Windows 盘符 URI 的 path 部分是 <c>/C:/...</c>，需去掉前导 '/'。
    /// Non-file URIs are returned unchanged.
    /// </summary>
    public static string UriToFilePath(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return uri;

        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return uri;

        // "file://" 之后即 URI path，始终以 '/' 开头（authority 为空）
        var path = uri["file://".Length..];

        // Windows 盘符形式：/C:/... → C:/...
        if (path.Length >= 3 && path[0] == '/' && path[2] == ':')
            path = path[1..];

        return path.Replace('/', Path.DirectorySeparatorChar);
    }
}
