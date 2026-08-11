using System.ComponentModel;
using System.Text;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Text;

namespace OneCode.App.Tools;

/// <summary>
/// Apply a WorkspaceEdit returned by LSP codeAction/resolve or textDocument/rename
/// to the file system. Each TextEdit's range is interpreted as 0-based line/character
/// offsets (per the LSP spec) and applied via search-and-replace against the file's
/// current content, preserving the original encoding and line-ending style.
///
/// This closes the "codeAction → resolve → apply" loop so the agent can flow:
///   1. LspTool(action="codeAction", ...) → list of CodeActions
///   2. LspTool(action="codeActionResolve", query=&lt;CodeAction&gt;) → resolved edit
///   3. ApplyWorkspaceEdit(workspaceEditJson=&lt;the edit field&gt;) → applied to files
/// </summary>
public sealed class ApplyWorkspaceEditTool
{
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly ILspNotifier _notifier;

    public ApplyWorkspaceEditTool(IWorkingDirectoryAccessor wd, ILspNotifier notifier)
    {
        _wd = wd;
        _notifier = notifier;
    }

    [Description("Apply an LSP WorkspaceEdit to files. The edit is a JSON object with a 'documentChanges' array or a 'changes' map, as returned by Lsp codeActionResolve or rename. Each edit's range is 0-based line/character per the LSP spec.")]
    public async Task<ToolResult> ApplyAsync(
        [Description("JSON-serialized WorkspaceEdit object. May use either 'documentChanges' (preferred) or 'changes'.")] string workspaceEditJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceEditJson))
            return ToolResult.Error("workspaceEditJson is required.");

        JsonElement edit;
        try
        {
            using var doc = JsonDocument.Parse(workspaceEditJson);
            edit = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return ToolResult.Error($"Invalid WorkspaceEdit JSON: {ex.Message}");
        }

        // Collect all file edits into a normalized form so we can apply them
        // in a single pass per file. documentChanges is preferred (and the only
        // shape that supports create/rename/delete); changes is the legacy map
        // of uri→TextEdit[].
        var fileEdits = new Dictionary<string, List<TextEditInfo>>(StringComparer.OrdinalIgnoreCase);
        var operations = new List<FileOperationInfo>();

        if (edit.TryGetProperty("documentChanges", out var dcEl) && dcEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dcEl.EnumerateArray())
            {
                if (!item.TryGetProperty("kind", out var kindEl))
                {
                    // Legacy TextDocumentEdit shape (no kind field)
                    if (TryParseTextDocumentEdit(item, out var tdEdit))
                        AddFileEdit(fileEdits, tdEdit.FilePath, tdEdit.Edits);
                    continue;
                }

                var kind = kindEl.GetString();
                switch (kind)
                {
                    case "edit":
                        if (TryParseTextDocumentEdit(item, out var editOp))
                            AddFileEdit(fileEdits, editOp.FilePath, editOp.Edits);
                        break;
                    case "create":
                        if (item.TryGetProperty("uri", out var uri))
                            operations.Add(new FileOperationInfo("create", UriToFilePath(uri.GetString() ?? "")));
                        break;
                    case "rename":
                        if (item.TryGetProperty("oldUri", out var oldUri) &&
                            item.TryGetProperty("newUri", out var newUri))
                            operations.Add(new FileOperationInfo("rename",
                                UriToFilePath(oldUri.GetString() ?? ""),
                                UriToFilePath(newUri.GetString() ?? "")));
                        break;
                    case "delete":
                        if (item.TryGetProperty("uri", out var delUri))
                            operations.Add(new FileOperationInfo("delete", UriToFilePath(delUri.GetString() ?? "")));
                        break;
                }
            }
        }
        else if (edit.TryGetProperty("changes", out var chEl) && chEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in chEl.EnumerateObject())
            {
                var filePath = UriToFilePath(prop.Name);
                var edits = ParseTextEdits(prop.Value);
                if (edits.Count > 0)
                    AddFileEdit(fileEdits, filePath, edits);
            }
        }

        if (fileEdits.Count == 0 && operations.Count == 0)
            return ToolResult.Error("WorkspaceEdit contains no edits or file operations.");

        // Apply file edits first, then file operations (create/rename/delete).
        // Edits within a single file must be applied bottom-to-top so that
        // earlier offsets remain valid after later edits have been applied.
        var appliedEdits = 0;
        var appliedFiles = 0;
        var sb = new StringBuilder();

        foreach (var (filePath, edits) in fileEdits)
        {
            var resolved = PathsHelper.SafeResolve(filePath, _wd.WorkingDirectory, _wd.AdditionalDirectories);
            if (!resolved.IsSuccess)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Skipped {filePath}: {resolved.Error}");
                continue;
            }
            var fullPath = resolved.Value!;

            try
            {
                if (!File.Exists(fullPath))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Skipped {filePath}: file does not exist (use 'create' operation for new files)");
                    continue;
                }

                var (content, encoding, _) =
                    await FileEncodingHelper.ReadWithEncodingAsync(fullPath, ct).ConfigureAwait(false);

                var newContent = ApplyTextEdits(content, edits);

                await FileEncodingHelper.WriteWithEncodingAsync(fullPath, newContent, encoding, ct)
                    .ConfigureAwait(false);

                await _notifier.NotifyFileUpdatedAsync(fullPath, ct).ConfigureAwait(false);

                appliedFiles++;
                appliedEdits += edits.Count;
                sb.AppendLine(CultureInfo.InvariantCulture, $"Applied {edits.Count} edit(s) to {filePath}");
            }
            catch (Exception ex)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Failed to apply edits to {filePath}: {ex.Message}");
            }
        }

        // Apply file operations (create / rename / delete).
        foreach (var op in operations)
        {
            try
            {
                switch (op.Kind)
                {
                    case "create":
                        var createPath = PathsHelper.SafeResolve(op.TargetPath, _wd.WorkingDirectory, _wd.AdditionalDirectories);
                        if (createPath.IsSuccess && !File.Exists(createPath.Value))
                        {
                            await File.WriteAllTextAsync(createPath.Value!, "", ct).ConfigureAwait(false);
                            sb.AppendLine(CultureInfo.InvariantCulture, $"Created file: {op.TargetPath}");
                        }
                        break;
                    case "rename":
                        var srcResolved = PathsHelper.SafeResolve(op.SourcePath!, _wd.WorkingDirectory, _wd.AdditionalDirectories);
                        var dstResolved = PathsHelper.SafeResolve(op.TargetPath, _wd.WorkingDirectory, _wd.AdditionalDirectories);
                        if (srcResolved.IsSuccess && dstResolved.IsSuccess && File.Exists(srcResolved.Value))
                        {
                            await Task.Run(() => File.Move(srcResolved.Value!, dstResolved.Value!, overwrite: true), ct).ConfigureAwait(false);
                            sb.AppendLine(CultureInfo.InvariantCulture, $"Renamed {op.SourcePath} → {op.TargetPath}");
                        }
                        break;
                    case "delete":
                        var delResolved = PathsHelper.SafeResolve(op.TargetPath, _wd.WorkingDirectory, _wd.AdditionalDirectories);
                        if (delResolved.IsSuccess && File.Exists(delResolved.Value))
                        {
                            await Task.Run(() => File.Delete(delResolved.Value), ct).ConfigureAwait(false);
                            sb.AppendLine(CultureInfo.InvariantCulture, $"Deleted file: {op.TargetPath}");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Failed {op.Kind} on {op.TargetPath}: {ex.Message}");
            }
        }

        var summary = $"Applied {appliedEdits} edit(s) across {appliedFiles} file(s).";
        var detail = sb.ToString().TrimEnd();
        return ToolResult.Success(detail.Length > 0 ? $"{summary}\n{detail}" : summary);
    }

    private static void AddFileEdit(
        Dictionary<string, List<TextEditInfo>> map,
        string filePath,
        List<TextEditInfo> edits)
    {
        if (map.TryGetValue(filePath, out var list))
            list.AddRange(edits);
        else
            map[filePath] = new List<TextEditInfo>(edits);
    }

    private static bool TryParseTextDocumentEdit(JsonElement item, out (string FilePath, List<TextEditInfo> Edits) result)
    {
        result = ("", new List<TextEditInfo>());

        string filePath;
        if (item.TryGetProperty("textDocument", out var td) && td.TryGetProperty("uri", out var uri))
            filePath = UriToFilePath(uri.GetString() ?? "");
        else if (item.TryGetProperty("uri", out var uri2))
            filePath = UriToFilePath(uri2.GetString() ?? "");
        else
            return false;

        if (!item.TryGetProperty("edits", out var editsEl) || editsEl.ValueKind != JsonValueKind.Array)
            return false;

        result = (filePath, ParseTextEdits(editsEl));
        return true;
    }

    private static List<TextEditInfo> ParseTextEdits(JsonElement editsEl)
    {
        var edits = new List<TextEditInfo>();
        if (editsEl.ValueKind != JsonValueKind.Array) return edits;

        foreach (var e in editsEl.EnumerateArray())
        {
            if (!e.TryGetProperty("range", out var range)) continue;
            if (!range.TryGetProperty("start", out var start)) continue;
            if (!range.TryGetProperty("end", out var end)) continue;

            var startLine = start.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0;
            var startChar = start.TryGetProperty("character", out var sc) ? sc.GetInt32() : 0;
            var endLine = end.TryGetProperty("line", out var el2) ? el2.GetInt32() : 0;
            var endChar = end.TryGetProperty("character", out var ec) ? ec.GetInt32() : 0;
            var newText = e.TryGetProperty("newText", out var nt) ? nt.GetString() ?? "" : "";

            edits.Add(new TextEditInfo(startLine, startChar, endLine, endChar, newText));
        }

        return edits;
    }

    /// <summary>
    /// Apply a list of TextEdits to file content. Edits are sorted descending by
    /// start position so that offsets for earlier edits remain valid as later
    /// edits are applied. Each edit's range is 0-based line/character per the LSP spec.
    /// </summary>
    /// <remarks>
    /// Line-ending normalization: LSP ranges are 0-based line/character offsets against
    /// a logical "lines array". To compute those offsets deterministically we first
    /// normalize the file to LF internally, apply all edits, then re-expand back to the
    /// file's original line-ending style (CRLF or LF) on output. Without normalization,
    /// <c>Split('\n')</c> leaves trailing <c>\r</c> on each line for CRLF files, and
    /// <c>string.Join("\r\n", lines)</c> would then double every <c>\r</c> — silently
    /// corrupting the file.
    /// </remarks>
    internal static string ApplyTextEdits(string content, List<TextEditInfo> edits)
    {
        var detectedStyle = FileEncodingHelper.DetectLineEndingStyle(content);
        var lineEnding = detectedStyle == FileEncodingHelper.LineEndingStyle.Crlf ? "\r\n" : "\n";

        // Normalize to LF so that Split('\n') yields clean lines without trailing \r.
        // We restore the original line-ending style at the very end via string.Join.
        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        // Sort descending so we apply from bottom to top, keeping earlier offsets valid.
        var sortedEdits = edits
            .OrderByDescending(e => e.StartLine)
            .ThenByDescending(e => e.StartChar)
            .ToList();

        foreach (var edit in sortedEdits)
        {
            if (edit.StartLine < 0 || edit.StartLine >= lines.Length) continue;

            // Multi-line edits: replace from start position to end position.
            // We splice the lines into [start_line .. end_line] and replace the
            // affected region with newText.
            var startLineIdx = edit.StartLine;
            var endLineIdx = Math.Min(edit.EndLine, lines.Length - 1);

            var startLineText = lines[startLineIdx];
            var endLineText = lines[endLineIdx];

            // Extract prefix (before start position) and suffix (after end position)
            var prefix = startLineText[..Math.Min(edit.StartChar, startLineText.Length)];
            var suffix = endLineText[Math.Min(edit.EndChar, endLineText.Length)..];

            // Build the replacement: prefix + newText + suffix.
            // newText comes from the LSP server which uses LF per spec; we keep it as-is
            // (no normalization needed) and rely on string.Join to expand at the end.
            var replacement = prefix + edit.NewText + suffix;
            var replacementLines = replacement.Split('\n');

            // Reassemble: lines before startLine + replacementLines + lines after endLine
            var before = lines.Take(startLineIdx);
            var after = lines.Skip(endLineIdx + 1);
            lines = before.Concat(replacementLines).Concat(after).ToArray();
        }

        return string.Join(lineEnding, lines);
    }

    private static string UriToFilePath(string uri) => LspUriHelper.UriToFilePath(uri);

    /// <summary>A single LSP TextEdit operation.</summary>
    internal sealed record TextEditInfo(int StartLine, int StartChar, int EndLine, int EndChar, string NewText);

    /// <summary>A file-level operation (create/rename/delete).</summary>
    private sealed record FileOperationInfo(string Kind, string TargetPath, string? SourcePath = null);
}
