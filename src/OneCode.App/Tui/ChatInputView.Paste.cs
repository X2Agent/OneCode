namespace OneCode.App.Tui;

/// <summary>
/// Paste / image-attachment handling for <see cref="ChatInputView"/>:
/// clipboard reads, large-paste collapsing, image detection and path expansion.
/// Partial of the view because it shares the paste/attachment state.
/// </summary>
public sealed partial class ChatInputView
{
    /// <summary>
    /// Invoked when the user triggers a paste via KeybindingResolver (Ctrl+V).
    /// Reads the system clipboard and routes through HandlePastedText for multi-line
    /// folding. Uses _app.Invoke to defer HandlePastedText to the next UI cycle —
    /// this ensures Editor has finished any internal paste processing before we
    /// overwrite _input.Text with the collapsed summary. Without this deferral,
    /// Editor's internal paste (which runs synchronously during KeyDown handling)
    /// would insert raw text AFTER HandlePastedText, overriding the collapse.
    /// </summary>
    private void OnPasteRequested()
    {
        if (_clipboard is null) return;

        var pasteTask = Task.Run(async () =>
        {
            try
            {
                // 1. Check for raw image data in clipboard first
                var imagePath = await _clipboard.GetImageAsync().ConfigureAwait(false);
                if (imagePath is not null)
                {
                    _app.Invoke(() => AddImage(imagePath));
                    return;
                }

                // 2. Check for file list (Explorer/Finder copy)
                var files = await _clipboard.GetFilesAsync().ConfigureAwait(false);
                if (files.Count > 0)
                {
                    var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
                    _app.Invoke(() =>
                    {
                        foreach (var f in files)
                        {
                            if (imageExts.Contains(Path.GetExtension(f)))
                                AddImage(f);
                            else
                                _input.InsertTextAtCursor($"@{f} ");
                        }
                    });
                    return;
                }

                // 3. Fall back to text clipboard — route through HandlePastedText
                var text = await _clipboard.GetTextAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(text)) return;
                _app.Invoke(() => HandlePastedText(text.Trim(), isFullText: false));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Paste clipboard read failed: {ex.Message}");
            }
        });

        // 纵深防御：观察任何逃逸内部 catch 的异常（确保不触发 UnobservedTaskException）。
        // 内部 catch 已捕获预期失败；此延续仅作安全网，延续本身为 fire-and-forget 日志。
#pragma warning disable CS4014
        pasteTask.ContinueWith(
            t => System.Diagnostics.Debug.WriteLine($"Paste task faulted: {t.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
#pragma warning restore CS4014
    }

    /// <summary>
    /// Intercepts bracketed paste events from the terminal. Terminal.Gui v2 delivers
    /// pasted text via <see cref="IApplication.Paste"/> instead of individual key events.
    /// We inspect the payload: if it looks like image file paths, insert [Image #N] tags;
    /// otherwise route through <see cref="HandlePastedText"/> for multi-line folding.
    /// </summary>
    private void OnApplicationPaste(object? sender, PasteEventArgs e)
    {
        // IApplication.Paste is application-wide rather than scoped to the
        // focused view. Leave the event untouched when an overlay owns focus;
        // otherwise this handler would insert the payload into the hidden
        // chat editor before TextField/other overlay controls can consume it.
        if (!_input.HasFocus)
            return;

        if (_isBusy || _interactionSuspended)
            return;

        if (_pastedText != null)
        {
            e.Handled = true;
            return;
        }

        var text = e.Text;
        if (string.IsNullOrEmpty(text))
            return;

        // Check if the pasted text contains image file paths (one per line).
        // Terminal bracketed-paste delivers file paths from Explorer/Finder copies
        // as plain text, so we detect them here rather than relying on
        // GetFilesFromClipboardAsync (which queries the OS clipboard directly).
        var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var imagePaths = new List<string>();
        var otherContent = new List<string>();

        foreach (var line in lines)
        {
            var candidate = line.Trim().Trim('"');
            if (!string.IsNullOrEmpty(candidate) &&
                File.Exists(candidate) &&
                imageExts.Contains(Path.GetExtension(candidate)))
            {
                imagePaths.Add(candidate);
            }
            else
            {
                otherContent.Add(line);
            }
        }

        foreach (var ip in imagePaths)
            AddImage(ip);

        // Route remaining text (non-image) through HandlePastedText for folding.
        // NOTE: Do NOT filter empty lines — filtering reduces line count and
        // breaks the >MaxVisibleLines folding threshold detection.
        var remaining = string.Join("\n", otherContent);
        if (!string.IsNullOrEmpty(remaining))
            HandlePastedText(remaining, isFullText: false);

        e.Handled = true;
    }

    /// <summary>
    /// Handles pasted text: if the content exceeds <see cref="ChatTextEditor.MaxVisibleLines"/>
    /// lines, collapse it into a one-line summary and store the real content for submission.
    /// </summary>
    /// <param name="text">The pasted text. When <paramref name="isFullText"/> is true,
    /// this is the entire document text (from LargeTextPasted). When false, this is
    /// only the pasted content (from OnApplicationPaste / OnPasteRequested).</param>
    /// <param name="isFullText">True when text includes any pre-existing input content.</param>
    private void HandlePastedText(string text, bool isFullText = false)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lineCount = 1;
        foreach (var c in normalized)
            if (c == '\n') lineCount++;

        if (lineCount > ChatTextEditor.MaxVisibleLines)
        {
            _pasteCount++;
            _pastedText = normalized;
            // Restore Editor's read-only state BEFORE setting Text — the
            // LargeTextPasted detection sets ReadOnly=true to block further
            // raw-text insertion, but we need it cleared to set the summary.
            _input.ResumeAfterPaste();
            _suppressCompletion = true;
            _input.Text = $"[Pasted text #{_pasteCount} +{lineCount} lines]";
            _input.InsertionPoint = (_input.Text?.Length ?? 0);
            _suppressCompletion = false;
        }
        else
        {
            _pastedText = null;
            if (isFullText)
            {
                // LargeTextPasted path: text is the full document — replace.
                SetInputText(normalized);
            }
            else
            {
                // Clipboard paste path: insert at cursor to preserve existing text.
                _suppressCompletion = true;
                _input.InsertTextAtCursor(normalized);
                _suppressCompletion = false;
            }
        }
    }

    /// <summary>
    /// Adds a pasted image from a file path. Stores the original path immediately,
    /// then processes through ImagePipeline asynchronously (compression, resize)
    /// and replaces the entry with the processed path when done.
    /// </summary>
    private void AddImage(string imagePath)
    {
        if (IsMultimodalSupported?.Invoke() == false)
        {
            ImagePasteRejected?.Invoke();
            return;
        }

        _imageCount++;
        var index = _imageCount;
        _pendingImages[index] = imagePath;

        var tag = $"[Image #{index}]";
        _suppressCompletion = true;
        _input.InsertTextAtCursor(tag);
        _suppressCompletion = false;

        if (ImagePipeline is not null)
            _ = ProcessImageAsync(index, imagePath);
    }

    /// <summary>
    /// Adds a pasted image from raw bytes (OSC 52 terminal clipboard paste).
    /// Writes bytes to a temp file in the ImagePipeline directory, stores the path,
    /// then processes through ImagePipeline asynchronously.
    /// </summary>
    internal void AddImageBytes(byte[] data)
    {
        if (IsMultimodalSupported?.Invoke() == false)
        {
            ImagePasteRejected?.Invoke();
            return;
        }

        // Write to ImagePipeline temp dir so cleanup is centralized
        var tempDir = Path.Combine(Path.GetTempPath(), "OneCode-images");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"osc52_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempPath, data);

        AddImage(tempPath);
    }

    /// <summary>
    /// Processes an image through ImagePipeline (resize, compress, re-encode)
    /// and replaces the pending path with the processed result.
    /// </summary>
    private async Task ProcessImageAsync(int index, string originalPath)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(originalPath).ConfigureAwait(false);
            var result = await ImagePipeline!.ProcessAsync(bytes).ConfigureAwait(false);
            _pendingImages[index] = result.FilePath;
        }
        catch
        {
            // Keep original path — already stored as fallback
        }
    }

    /// <summary>
    /// Returns and clears all pending image paths. Called on submit to retrieve
    /// images for the multimodal message. Keys are the image numbers.
    /// </summary>
    public IReadOnlyList<string> TakePendingImages()
    {
        if (_pendingImages.Count == 0)
            return [];
        var paths = _pendingImages.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        _pendingImages.Clear();
        return paths;
    }

    /// <summary>
    /// Resets the image counter. Call when a new session starts.
    /// </summary>
    public void ResetImageCounter()
    {
        _imageCount = 0;
        _pendingImages.Clear();
    }
}
