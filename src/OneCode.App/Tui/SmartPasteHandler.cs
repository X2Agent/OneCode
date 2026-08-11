namespace OneCode.App.Tui;

/// <summary>
/// Smart paste from system clipboard: detects text content, file paths, and image data.
/// Extracted from ChatInputView to isolate clipboard and paste processing.
/// <para>
/// Priority order:
///   1. Clipboard image (raw bitmap data) → save to temp and insert @path
///   2. Clipboard file list (Explorer/Finder copy) → insert as @file references
///   3. Text that looks like file paths → insert as @file references
///   4. Plain text → insert into input (supports multi-line)
/// </para>
/// </summary>
internal sealed class SmartPasteHandler
{
    private readonly IApplication _app;
    private readonly OneCode.Core.IO.IClipboardService? _clipboard;

    public SmartPasteHandler(IApplication app, OneCode.Core.IO.IClipboardService? clipboard = null)
    {
        _app = app;
        _clipboard = clipboard;
    }

    /// <summary>Fires when the pasted content contains or references images.</summary>
    public event Action? ImagePasteRequested;

    /// <summary>
    /// Perform smart paste. The callbacks give the caller control over
    /// how the result is inserted into the input field.
    /// </summary>
    /// <param name="getCurrentText">Returns the current input text.</param>
    /// <param name="setText">Sets the input text and moves the cursor to the end.</param>
    /// <param name="addImage">When provided, called with the image file path instead of inserting @path text.</param>
    public async Task PasteAsync(Func<string> getCurrentText, Action<string> setText, Action<string>? addImage = null)
    {
        try
        {
            // 1. Check for raw image data in clipboard
            var imagePath = _clipboard is not null
                ? await _clipboard.GetImageAsync().ConfigureAwait(false)
                : null;
            if (imagePath is not null)
            {
                _app.Invoke(() =>
                {
                    if (addImage is not null)
                    {
                        // Image already handled — insert [Image #N] tag via callback.
                        // No need to fire ImagePasteRequested (which would show a
                        // misleading "Paste an image..." prompt to the user).
                        addImage(imagePath);
                    }
                    else
                    {
                        // No image callback — insert @path text reference and notify.
                        var current = getCurrentText();
                        var reference = $"@{imagePath}";
                        setText(string.IsNullOrEmpty(current) ? reference : $"{current} {reference}");
                        ImagePasteRequested?.Invoke();
                    }
                });
                return;
            }

            // 2. Check for file list in clipboard (e.g., copied from Explorer/Finder)
            var clipboardFiles = _clipboard is not null
                ? await _clipboard.GetFilesAsync().ConfigureAwait(false)
                : new List<string>();
            if (clipboardFiles.Count > 0)
            {
                var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
                var imageFiles = clipboardFiles.Where(p => imageExts.Contains(Path.GetExtension(p))).ToList();
                var otherFiles = clipboardFiles.Where(p => !imageExts.Contains(Path.GetExtension(p))).ToList();

                _app.Invoke(() =>
                {
                    // Image files → insert [Image #N] tags via callback (when available)
                    // so they participate in the multimodal message pipeline.
                    // Otherwise fall back to @path text references.
                    foreach (var ip in imageFiles)
                    {
                        if (addImage is not null)
                            addImage(ip);
                        else
                        {
                            var current = getCurrentText();
                            var reference = $"@{ip}";
                            setText(string.IsNullOrEmpty(current) ? reference : $"{current} {reference}");
                        }
                    }

                    // Non-image files always become @path references.
                    if (otherFiles.Count > 0)
                    {
                        var otherRefs = string.Join(" ", otherFiles.Select(p => $"@{p}"));
                        var current = getCurrentText();
                        setText(string.IsNullOrEmpty(current) ? otherRefs : $"{current} {otherRefs}");
                    }
                });
                return;
            }

            // 3. Fall back to text clipboard
            var text = _clipboard is not null
                ? await _clipboard.GetTextAsync().ConfigureAwait(false)
                : null;
            if (string.IsNullOrWhiteSpace(text))
                return;

            var trimmed = text.Trim();

            // Check if text content looks like file/image paths (one per line)
            var lines = trimmed.Replace("\r\n", "\n").Split('\n');
            var filePaths = new List<string>();
            var imagePaths = new List<string>();

            foreach (var line in lines)
            {
                var candidate = line.Trim().Trim('"');
                if (string.IsNullOrEmpty(candidate)) continue;

                if (File.Exists(candidate))
                {
                    var ext = Path.GetExtension(candidate).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
                        imagePaths.Add(candidate);
                    else
                        filePaths.Add(candidate);
                }
                else if (Directory.Exists(candidate))
                {
                    filePaths.Add(candidate);
                }
            }

            if (imagePaths.Count > 0)
            {
                _app.Invoke(() =>
                {
                    if (addImage is not null)
                    {
                        // Images already handled via callback.
                        foreach (var ip in imagePaths)
                            addImage(ip);
                    }
                    else
                    {
                        // No image callback — insert @path references and notify.
                        var refs = string.Join(" ", imagePaths.Select(p => $"@{p}"));
                        var current = getCurrentText();
                        setText(string.IsNullOrEmpty(current) ? refs : $"{current} {refs}");
                        ImagePasteRequested?.Invoke();
                    }
                });
                return;
            }

            if (filePaths.Count > 0 && filePaths.Count == lines.Length)
            {
                _app.Invoke(() =>
                {
                    var refs = string.Join(" ", filePaths.Select(p => $"@{p}"));
                    var current = getCurrentText();
                    setText(string.IsNullOrEmpty(current) ? refs : $"{current} {refs}");
                });
                return;
            }

            // Default: paste as text (supports multi-line).
            // ChatTextEditor handles '\n' natively, so multi-line paste just
            // flows into the input box and the bar grows to show all lines.
            _app.Invoke(() => setText(trimmed));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SmartPasteHandler paste failed: {ex.Message}");
        }
    }
}
