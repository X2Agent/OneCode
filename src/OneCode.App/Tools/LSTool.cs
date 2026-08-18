using System.ComponentModel;
using OneCode.Infrastructure;

namespace OneCode.App.Tools;

/// <summary>
/// List directory contents.
/// </summary>
public sealed class LSTool
{
    private readonly IWorkingDirectoryAccessor _wd;

    public LSTool(IWorkingDirectoryAccessor wd) => _wd = wd;

    [Description("List directory contents, returning entries with size, last-modified time, and a trailing '/' for directories. " +
                 "Use this to explore directory structure when you need a quick overview; use Glob when you need to find files by pattern across many directories. " +
                 "Default filters: dotfiles (entries starting with '.'), Windows hidden attributes, and generated/build directories (bin, obj, node_modules) are hidden unless all=true. " +
                 "Generated file extensions (.dll, .exe, .pdb, .pyc, etc.) are also filtered by default to reduce noise. " +
                 "Path safety: must resolve within the working directory or additional directories. " +
                 "Output is a plain-text table sorted alphabetically; entries that fail stat (e.g. due to permissions) appear with a '?' prefix.")]
    public Task<ToolResult> ListAsync(
        [Description("Directory to list. Omit or pass '.' for the working directory. Relative paths resolve against the working directory. Must exist and be within the working directory.")] string? path = null,
        [Description("Show hidden and generated files. Default false (filters dotfiles, hidden attrs, bin/obj/node_modules, and generated extensions). Set true to see everything.")] bool all = false,
        CancellationToken ct = default)
    {
        var resolveResult = PathsHelper.SafeResolve(path ?? ".", _wd.WorkingDirectory, _wd.AdditionalDirectories);
        if (!resolveResult.IsSuccess)
            return Task.FromResult(ToolResult.Error($"Error: {resolveResult.Error}"));
        var fullPath = resolveResult.Value;

        if (!Directory.Exists(fullPath))
            return Task.FromResult(ToolResult.Error($"Directory not found: {path}"));

        try
        {
            var files = Directory.GetFileSystemEntries(fullPath);
            List<string> lines = [];

            foreach (var entry in files.OrderBy(f => f))
            {
                var name = Path.GetFileName(entry);
                if (!all)
                {
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (OperatingSystem.IsWindows())
                    {
                        try
                        {
                            if ((File.GetAttributes(entry) & FileAttributes.Hidden) != 0)
                                continue;
                        }
                        catch (IOException ex) { System.Diagnostics.Debug.WriteLine($"LSTool failed to check Hidden attribute: {ex.Message}"); }
                    }
                    if (Directory.Exists(entry) && GeneratedFilesDetector.IsGeneratedDirectory(name))
                        continue;
                    if (File.Exists(entry) && GeneratedFilesDetector.IsGeneratedExtension(Path.GetExtension(name)))
                        continue;
                }

                try
                {
                    var isDir = Directory.Exists(entry);
                    var info = new FileInfo(entry);
                    var size = isDir ? 0 : info.Length;
                    var modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    var dirMarker = isDir ? "/" : "";
                    lines.Add($"{modified,19} {size,10}  {name}{dirMarker}");
                }
                catch
                {
                    lines.Add($"?  {Path.GetFileName(entry)}");
                }
            }

            var header = all ? $"Total: {lines.Count} entries (including hidden)\n" : $"Total: {lines.Count} entries\n";
            return Task.FromResult(ToolResult.Success(header + string.Join("\n", lines)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"Error listing directory: {ex.Message}"));
        }
    }
}
