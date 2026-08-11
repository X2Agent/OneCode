namespace OneCode.App.Commands;

public sealed class GcStatsCommand : Command
{
    public override string Name => "gc-stats";
    public override string Description => "Show .NET GC and memory statistics";
    public override CommandCategory Category => CommandCategory.Diagnostic;
    public override bool IsHidden => true;

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var info = GC.GetGCMemoryInfo();
        var heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024);
        var availableMb = info.TotalAvailableMemoryBytes / (1024.0 * 1024);
        var committedMb = info.TotalCommittedBytes / (1024.0 * 1024);
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        return Task.FromResult(CommandResult.Text($"""
            GC & Memory Statistics:
              Heap size:         {heapMb:F1} MB
              Committed:         {committedMb:F1} MB
              Total available:   {availableMb:F0} MB
              GC collections:    Gen0={gen0}, Gen1={gen1}, Gen2={gen2}
              GC compacted:      {info.Compacted}
              Pinned objects:    {info.PinnedObjectsCount}
              Pause time (last): {info.PauseTimePercentage:F1}%
            """));
    }
}
