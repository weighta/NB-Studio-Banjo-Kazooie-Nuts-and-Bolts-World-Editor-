using System.Diagnostics;

namespace NB.Core.Diagnostics;

/// <summary>
/// Opt-in timing of loading stages (NB_STUDIO_PROFILE=1): sections are summed per name (thread-safe) until
/// <see cref="Report"/>. Costs one branch when off. NB Studio's Prof report includes these.
/// </summary>
public static class Timing
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
            Add(_name, (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency);
        }
    }

    public static Scope Time(string name) => new(On ? name : null);

    public static void Add(string name, double ms)
    {
        if (!On) return;
        lock (_s) { var v = _s.GetValueOrDefault(name); _s[name] = (v.N + 1, v.Total + ms, Math.Max(v.Max, ms)); }
    }

    /// <summary>The sums since the last report (longest total first); empties them.</summary>
    public static List<(string Name, int N, double Total, double Max)> Take()
    {
        lock (_s)
        {
            var r = _s.OrderByDescending(kv => kv.Value.Total).Select(kv => (kv.Key, kv.Value.N, kv.Value.Total, kv.Value.Max)).ToList();
            _s.Clear();
            return r;
        }
    }
}
