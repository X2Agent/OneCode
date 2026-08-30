using OneCode.Core.Build;

namespace OneCode.App.Query;

/// <summary>
/// Outcome carrier for the Build gate preamble: async iterators cannot return values,
/// so the preamble writes its result here while yielding gate events.
/// </summary>
internal sealed class BuildPreambleState
{
    public BuildRun? BuildRun { get; set; }

    public bool EarlyDone { get; set; }
}
