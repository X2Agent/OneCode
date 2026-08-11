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
    /// Strips the <c>file://</c> or <c>file:///</c> prefix and converts
    /// forward slashes to <see cref="Path.DirectorySeparatorChar"/>.
    /// Non-file URIs are returned unchanged.
    /// </summary>
    public static string UriToFilePath(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return uri;

        if (uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri["file:///".Length..];
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri["file://".Length..];
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        return uri;
    }
}
