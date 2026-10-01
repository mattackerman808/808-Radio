using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Radio808.App;

/// <summary>Paint timings, overall and per section, for the --bench mode. UI thread only.</summary>
internal sealed class PaintTiming
{
    private readonly List<double> _frames = new();
    private readonly Dictionary<string, double> _sections = new();

    public void Reset() { _frames.Clear(); _sections.Clear(); }

    public void Frame(long startTicks) => _frames.Add(Ms(startTicks));

    /// <summary>Adds the time since <paramref name="startTicks"/> to a section, and returns now (to chain sections).</summary>
    public long Section(string name, long startTicks)
    {
        long now = Stopwatch.GetTimestamp();
        _sections[name] = _sections.GetValueOrDefault(name) + (now - startTicks) * 1000.0 / Stopwatch.Frequency;
        return now;
    }

    private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    public string Report(double seconds)
    {
        var sb = new StringBuilder();
        int n = _frames.Count;
        if (n == 0) return "no frames\n";
        var sorted = _frames.OrderBy(x => x).ToList();
        sb.AppendLine($"frames {n} in {seconds:0} s = {n / seconds:0.0} fps");
        sb.AppendLine($"paint ms: avg {_frames.Average():0.0}  p50 {sorted[n / 2]:0.0}  p95 {sorted[(int)(n * 0.95)]:0.0}  max {sorted[^1]:0.0}");
        sb.AppendLine($"UI thread busy painting: {_frames.Sum() / (seconds * 10):0}%");
        foreach (var (k, v) in _sections.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {k,-14} {v / n,6:0.00} ms/frame");
        sb.AppendLine($"  {"rest",-14} {(_frames.Sum() - _sections.Values.Sum()) / n,6:0.00} ms/frame (double buffer setup and copy to screen)");
        return sb.ToString();
    }
}
