namespace OneCode.App.Commands;

/// <summary>
/// 进程内存与 GC 观测口：驻留判据（各代尺寸、LOH、累计分配）比单个堆总量更能说明问题，
/// 是「无基准项目」条件下做手工回归的量化依据。
/// </summary>
public sealed class GcStatsCommand : Command
{
    public override string Name => "gc-stats";
    public override string Description => "Show .NET GC and memory statistics";
    public override CommandCategory Category => CommandCategory.Diagnostic;
    public override bool IsHidden => true;

    private const double BytesPerMb = 1024.0 * 1024.0;

    /// <summary>上次查询时的累计分配字节数：两次查询的差值即这段时间的分配压力。</summary>
    private static long s_lastAllocatedBytes;

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var info = GC.GetGCMemoryInfo();
        var heapMb = GC.GetTotalMemory(false) / BytesPerMb;
        var availableMb = info.TotalAvailableMemoryBytes / BytesPerMb;
        var committedMb = info.TotalCommittedBytes / BytesPerMb;
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        var generations = info.GenerationInfo;
        var gen0SizeMb = GenerationSizeMb(generations, 0);
        var gen1SizeMb = GenerationSizeMb(generations, 1);
        var gen2SizeMb = GenerationSizeMb(generations, 2);
        var lohSizeMb = GenerationSizeMb(generations, 3);
        var pohSizeMb = GenerationSizeMb(generations, 4);

        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        var allocatedSinceLastQueried = allocated - s_lastAllocatedBytes;
        s_lastAllocatedBytes = allocated;

        return Task.FromResult(CommandResult.Text($"""
            GC & Memory Statistics:
              Heap size:         {heapMb:F1} MB
              Committed:         {committedMb:F1} MB
              Total available:   {availableMb:F0} MB
              Gen0 / Gen1 / Gen2: {gen0SizeMb:F2} / {gen1SizeMb:F2} / {gen2SizeMb:F2} MB
              LOH / POH:         {lohSizeMb:F2} / {pohSizeMb:F2} MB
              GC collections:    Gen0={gen0}, Gen1={gen1}, Gen2={gen2}
              Allocated total:   {allocated / BytesPerMb:F1} MB
              Allocated since last: {allocatedSinceLastQueried / BytesPerMb:F1} MB
              GC compacted:      {info.Compacted}
              Pinned objects:    {info.PinnedObjectsCount}
              Pause time (last): {info.PauseTimePercentage:F1}%
            """));
    }

    private static double GenerationSizeMb(ReadOnlySpan<GCGenerationInfo> generations, int index)
        => index < generations.Length ? generations[index].SizeAfterBytes / BytesPerMb : 0;
}
