namespace OneCode.Core.Tools;

public interface IWorkingDirectoryAccessor
{
    string WorkingDirectory { get; }

    /// <summary>
    /// Additional directories accessible to file-access tools in addition to
    /// <see cref="WorkingDirectory"/>. Typically populated by <c>/add-dir</c>
    /// or <c>--add-dir</c>. Paths that resolve within any of these directories
    /// pass the scope check performed by <c>PathsHelper.SafeResolve</c>.
    /// Default implementation returns an empty collection so consumers can omit it.
    /// </summary>
    IReadOnlyList<string> AdditionalDirectories => Array.Empty<string>();
}
