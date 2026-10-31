using System.Collections.Concurrent;
using System.Diagnostics;

namespace ExpenseIntelligence.Api;

public sealed record RouteTiming(string Route, long Count, double P50Ms, double P95Ms, double MaxMs, double AvgMs);

/// <summary>Measured (not estimated) response times: ring buffer of the last 500 samples per route.</summary>
public sealed class RequestMetrics
{
    private const int Cap = 500;
    private sealed class Ring { public readonly double[] Buf = new double[Cap]; public long N; public readonly object L = new(); }
    private readonly ConcurrentDictionary<string, Ring> _rings = new();
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void Record(string route, double ms)
    {
        var r = _rings.GetOrAdd(route, _ => new Ring());
        lock (r.L) { r.Buf[r.N % Cap] = ms; r.N++; }
    }

    public List<RouteTiming> Snapshot() => _rings.Select(kv =>
    {
        double[] data; long n;
        lock (kv.Value.L) { n = kv.Value.N; data = kv.Value.Buf.Take((int)Math.Min(n, Cap)).Order().ToArray(); }
        double P(double q) => data[Math.Min(data.Length - 1, (int)Math.Ceiling(q * data.Length) - 1)];
        return new RouteTiming(kv.Key, n, Math.Round(P(0.5), 2), Math.Round(P(0.95), 2), Math.Round(data[^1], 2), Math.Round(data.Average(), 2));
    }).OrderBy(t => t.Route).ToList();

    public static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}
