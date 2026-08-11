namespace OneCode.App;

internal sealed class StartupTimer
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly List<(string Phase, double Ms)> _phases = [];

    public void Mark(string phase)
    {
        _phases.Add((phase, _sw.Elapsed.TotalMilliseconds));
    }

    public IReadOnlyList<(string Phase, double Ms)> GetPhases() => _phases;

    public double TotalMs => _sw.Elapsed.TotalMilliseconds;

    public string FormatSummary()
    {
        if (_phases.Count == 0) return "No startup phases recorded.";

        var sb = new System.Text.StringBuilder();
        var prev = 0.0;
        foreach (var (phase, ms) in _phases)
        {
            var delta = ms - prev;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {phase,-35} {delta,8:F1}ms  (total: {ms:F1}ms)");
            prev = ms;
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {"TOTAL",-35} {_sw.Elapsed.TotalMilliseconds,8:F1}ms");
        return sb.ToString();
    }
}
