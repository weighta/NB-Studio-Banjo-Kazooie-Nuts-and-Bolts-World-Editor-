using System.Diagnostics;

namespace NB.Studio.Viewport;

/// <summary>
/// Optional timing of the 3D view's interactive work (NB_STUDIO_PROFILE=1): sections are summed per name and written to
/// the log through <see cref="Report"/> (count, total, average, worst in ms). Costs nothing when off.
/// </summary>
public static class Prof
{
    public static readonly bool On = Environment.GetEnvironmentVariable("NB_STUDIO_PROFILE") == "1";
    static readonly Dictionary<string, (int N, double Total, double Max)> _s = new();

    public readonly struct Scope : IDisposable
    {
        readonly string? _name; readonly long _t0;
        public Scope(string? name) { _name = name; _t0 = name != null ? Stopwatch.GetTimestamp() : 0; }
        public void Dispose()
        {
            if (_name == null) return;
            double ms = (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency;
            lock (_s) { var v = _s.GetValueOrDefault(_name); _s[_name] = (v.N + 1, v.Total + ms, Math.Max(v.Max, ms)); }
        }
    }

    public static Scope Time(string name) => new(On ? name : null);

    /// <summary>Appends <see cref="Report"/> with a label to the file named by NB_STUDIO_PROFILE_LOG (tests).</summary>
    public static void Flush(string label)
    {
        if (!On || Environment.GetEnvironmentVariable("NB_STUDIO_PROFILE_LOG") is not { Length: > 0 } f) return;
        try { File.AppendAllText(f, $"{label}: {Report()}{Environment.NewLine}"); } catch (IOException) { }
    }

    /// <summary>The sums since the last report, one line per section (longest total first); empties them.</summary>
    public static string Report()
    {
        lock (_s)
        {
            var r = string.Join("; ", _s.OrderByDescending(kv => kv.Value.Total).Select(kv => $"{kv.Key} {kv.Value.N}× avg {kv.Value.Total / Math.Max(1, kv.Value.N):0.00} max {kv.Value.Max:0.0} ms"));
            _s.Clear();
            return r;
        }
    }
}
