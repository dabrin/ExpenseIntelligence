using ExpenseIntelligence.Application;

namespace ExpenseIntelligence.Tests;

public class TimeSeriesTests
{
    private static string Rejected(string rec) => rec.Replace("\"aprobado\": false", "\"aprobado\": false, \"rechazado\": true");

    private static (Sandbox, AnalyticsService, InsightsService) Make(params string[] records)
    {
        var sb = new Sandbox(Sandbox.Json(records)); var st = sb.OpenStore();
        return (sb, new AnalyticsService(st), new InsightsService(st));
    }

    [Fact]
    public void Series_splits_states_keeps_rejected_apart_and_never_mixes_currencies()
    {
        var (sb, a, _) = Make(
            Sandbox.Rec(1, fecha: "2026-01-05", monto: 100m), Sandbox.Rec(2, fecha: "2026-01-10", aprobado: false, monto: 40m),
            Rejected(Sandbox.Rec(3, fecha: "2026-01-20", aprobado: false, monto: 25m)), Sandbox.Rec(4, "EUR", fecha: "2026-01-21", monto: 7m));
        using var _ = sb;
        var jan = a.Summary(ExpenseFilter.None).Series;
        var usd = jan.Single(p => p.Moneda == "USD");
        Assert.Equal(165m, usd.Total); Assert.Equal(100m, usd.ApprovedTotal); Assert.Equal(40m, usd.PendingTotal); Assert.Equal(25m, usd.RejectedTotal);
        Assert.Equal((1, 1, 1), (usd.ApprovedCount, usd.PendingCount, usd.RejectedCount));
        Assert.Equal(7m, jan.Single(p => p.Moneda == "EUR").Total);
    }

    [Fact]
    public void Edge_periods_are_flagged_partial_and_inner_ones_are_not()
    {
        var (sb, a, _) = Make(Sandbox.Rec(1, fecha: "2026-01-15"), Sandbox.Rec(2, fecha: "2026-02-10"), Sandbox.Rec(3, fecha: "2026-03-12"));
        using var _ = sb;
        var s = a.Summary(ExpenseFilter.None).Series;
        Assert.Equal([true, false, true], s.Select(p => p.Partial).ToArray()); // Jan starts mid-month, Mar ends before the 31st
        Assert.Equal(("2026-02-01", "2026-02-28"), (s[1].Start, s[1].End));
        // A date filter that cuts a month in half makes that month partial as well
        var cut = a.Summary(new ExpenseFilter(From: DateOnly.Parse("2026-02-01"), To: DateOnly.Parse("2026-02-15"))).Series.Single();
        Assert.True(cut.Partial);
    }

    [Fact]
    public void Weekly_bucket_bounds_are_monday_to_sunday()
    {
        var (sb, a, _) = Make(Sandbox.Rec(1, fecha: "2026-03-04"), Sandbox.Rec(2, fecha: "2026-03-18"));
        using var _ = sb;
        var w = a.Summary(ExpenseFilter.None, "week").Series.First();
        Assert.Equal(("2026-03-02", "2026-03-08"), (w.Start, w.End));
    }

    [Fact]
    public void Insights_are_categorised_ranked_and_use_spanish_number_format()
    {
        var recs = Enumerable.Range(1, 12).Select(i => Sandbox.Rec(i, fecha: "2026-03-01", aprobado: i > 3, monto: 1000.5m)).ToList();
        recs.Add(Sandbox.Rec(99, fecha: "2026-03-28", cat: "Software", dep: "Ventas", monto: 12m));
        var (sb, _, ins) = Make(recs.ToArray());
        using var _ = sb;
        var list = ins.Generate(ExpenseFilter.None).Insights;
        Assert.All(list, i => Assert.Contains(i.Category, new[] { "alertas", "tendencias", "distribucion" }));
        Assert.Equal(list.Select(i => i.Priority).OrderByDescending(p => p).ToArray(), list.Select(i => i.Priority).ToArray());
        Assert.Equal("alertas", list[0].Category);
        Assert.All(list.Where(i => i.Type == "concentration"), i => Assert.Equal("distribucion", i.Category));
        Assert.DoesNotContain(list, i => i.Detail.Contains("1,000.50") || i.Detail.Contains("%)."));
        Assert.Contains(list, i => i.Detail.Contains("1 000,50") || i.Detail.Contains(" %"));
    }

    [Fact]
    public void Insights_on_empty_filter_are_empty()
    {
        var (sb, _, ins) = Make(Sandbox.Rec(1));
        using var _ = sb;
        Assert.Empty(ins.Generate(new ExpenseFilter(Monedas: ["EUR"])).Insights);
    }
}
