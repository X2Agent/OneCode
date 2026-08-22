using OneCode.App.Services.Lsp;
using OneCode.Infrastructure;

namespace OneCode.App.Tui;

/// <summary>
/// LSP diagnostics rendering partial for <see cref="ChatTranscriptView"/>.
///
/// Adds <see cref="AddLspDiagnostics"/> which renders an inline diagnostics
/// block after EditTool/WriteTool completes, showing errors and warnings
/// published by the LSP server for the just-modified file.
/// </summary>
public sealed partial class ChatTranscriptView
{
    /// <summary>
    /// Renders an inline LSP diagnostics block for <paramref name="filePath"/>.
    /// Only diagnostics whose <see cref="LspDiagnostic.FilePath"/> matches
    /// <paramref name="filePath"/> are included. When no diagnostics exist
    /// for the file, the method is a no-op (no empty block is rendered).
    /// </summary>
    /// <remarks>
    /// Path matching uses <see cref="Path.GetFullPath(string)"/> for OS-canonical
    /// comparison rather than <c>EndsWith</c>, which would incorrectly match
    /// <c>"Bar/Foo.cs"</c> against a query for <c>"Foo.cs"</c>.
    /// </remarks>
    public void AddLspDiagnostics(IReadOnlyList<LspDiagnostic> diagnostics, string filePath)
    {
        if (diagnostics is null || diagnostics.Count == 0)
            return;

        var normalizedQuery = PathsHelper.NormalizePath(filePath);
        var matching = diagnostics
            .Where(d => string.Equals(
                PathsHelper.NormalizePath(d.FilePath), normalizedQuery,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 0)
            return;

        var lines = ChatBlockRenderers.RenderLspDiagnosticsBlock(
            Path.GetFileName(filePath), matching);
        if (_stream.IsStreaming)
            AddFormattedLines(lines);
        else
            AppendCommittedBlock(_ => lines);
    }

}
