using System.Diagnostics;
using ExpenseIntelligence.Application;
using Xunit.Abstractions;

namespace ExpenseIntelligence.Tests;

/// <summary>Runs on a COPY of the real dataset inside a sandbox. Numbers are printed (see test output) and asserted only against generous ceilings.</summary>
public class PerformanceTests(ITestOutputHelper output)
{
    private static double Time(Action a, int n = 20)
    {
        a(); var s = new double[n];
        for (var i = 0; i < n; i++) { var t = Stopwatch.GetTimestamp(); a(); s[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds; }
        Array.Sort(s); return s[(int)(n * 0.95) - 1];
    }

    [Fact]
    public async Task Real_dataset_load_queries_and_concurrent_writes_stay_fast()
    {
        var original = RecoveryReaderTests.FindOriginal();
        if (original is null) { output.WriteLine("dataset not present, skipped"); return; }
        using var sb = new Sandbox(File.ReadAllText(original));

        var sw = Stopwatch.StartNew();
        using var store = sb.OpenStore();
        output.WriteLine($"cold load + working-copy creation: {sw.ElapsedMilliseconds} ms for {store.Current.All.Count} records");
        Assert.Equal(10_000, store.Current.All.Count);
        Assert.True(sw.ElapsedMilliseconds < 5000);

        var q = new ExpenseQueryService(store); var an = new AnalyticsService(store);
        var f = new ExpenseFilter(Monedas: ["USD"], Aprobado: true, From: DateOnly.Parse("2025-03-01"));
        var pQuery = Time(() => q.Query(new ExpensePageQuery(f, "monto", true, 3, 25)));
        var pSummary = Time(() => an.Summary(ExpenseFilter.None));
        var pDoctor = Time(() => new DataDoctorService(store, Sandbox.Clock).Analyze(), 10);
        output.WriteLine($"p95 query page: {pQuery:F2} ms | p95 summary (10k rows): {pSummary:F2} ms | p95 data doctor: {pDoctor:F2} ms");
        Assert.True(pQuery < 100); Assert.True(pSummary < 250); Assert.True(pDoctor < 1000);

        var svc = new ExpenseService(store, Sandbox.Clock);
        var gc0 = GC.CollectionCount(2);
        sw.Restart();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => svc.CreateAsync(Sandbox.Input($"carga {i}", 1 + i), "perf")));
        output.WriteLine($"100 concurrent durable writes on 10k records: {sw.ElapsedMilliseconds} ms total, {sw.ElapsedMilliseconds / 100.0:F1} ms/write, gen2 GCs during run: {GC.CollectionCount(2) - gc0}");
        Assert.Equal(10_100, store.Current.All.Count);
        Assert.True((await store.VerifyIntegrityAsync()).Ok);
    }
}
