namespace OneCode.Core.IO;

/// <summary>
/// Platform-aware path comparer (Fix-4/F-09): Windows file systems are
/// case-insensitive, Unix ones are not. Using a fixed OrdinalIgnoreCase
/// everywhere merges distinct files on Linux (for example <c>A.cs</c> and
/// <c>a.cs</c>), letting change-scope checks miss out-of-scope writes.
/// </summary>
public static class PathComparer
{
    /// <summary>Whether the current OS has a case-insensitive file system.</summary>
    public static bool IsCaseInsensitive { get; } =
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);

    /// <summary>Default <see cref="StringComparer"/> for path sets and lookups.</summary>
    public static StringComparer Default { get; } =
        IsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Default <see cref="StringComparison"/> for path equality checks.</summary>
    public static StringComparison Sensitivity { get; } =
        IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}