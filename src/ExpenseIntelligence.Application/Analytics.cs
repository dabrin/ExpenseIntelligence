using System.Globalization;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

public sealed record CurrencyTotal(string Moneda, decimal Total, int Count, decimal Average, decimal Max, decimal ApprovedTotal, decimal PendingTotal);
public sealed record DimensionStat(string Key, string Moneda, int Count, decimal Total, decimal Average);
/// <summary>One bucket of the time series. Total = approved + pending + rejected (all in the same currency). Start/End are the calendar bounds of the bucket;
/// Partial is true when the data (or the active date filter) covers only part of it, so a low bar must not be read as a real drop.</summary>
public sealed record TimePoint(string Period, string Moneda, int Count, decimal Total, decimal ApprovedTotal, decimal PendingTotal,
    int ApprovedCount = 0, int PendingCount = 0, int RejectedCount = 0, decimal RejectedTotal = 0, string? Start = null, string? End = null, bool Partial = false);
public sealed record ApprovalByCurrency(string Moneda, int Approved, int Pending, decimal ApprovedTotal, decimal PendingTotal, int Rejected = 0, decimal RejectedTotal = 0);
public sealed record ApprovalStat(int Approved, int Pending, double ApprovedPct, double PendingPct, List<ApprovalByCurrency> ByCurrency, int Rejected = 0, double RejectedPct = 0);

public sealed record DashboardSummary(
    long Revision, int TotalRecords, List<CurrencyTotal> Totals, List<DimensionStat> ByDepartment, List<DimensionStat> ByCategory,
    List<DimensionStat> ByPaymentMethod, List<TimePoint> Series, string Granularity, ApprovalStat Approval);

public sealed record MonthCurrency(string Moneda, int Count, decimal Total, int Approved, decimal ApprovedTotal);
public sealed record MonthFrame(string Period, List<MonthCurrency> Totals, List<DimensionStat> ByDepartment, List<DimensionStat> ByCategory);
public sealed record Timeline(long Revision, List<string> Currencies, List<string> Departments, List<string> Categories, List<MonthFrame> Frames);

/// <summary>Deterministic aggregations. Amounts are NEVER summed across currencies: every figure is keyed by currency.</summary>
public sealed class AnalyticsService(IExpenseStore store)
{
    public DashboardSummary Summary(ExpenseFilter filter, string granularity = "month")
    {
        var snap = store.Current;
        var rows = filter.Apply(snap.Active).ToList();
        granularity = granularity is "day" or "week" or "month" ? granularity : "month";

        var totals = rows.GroupBy(e => e.Moneda).Select(g => new CurrencyTotal(
            g.Key, g.Sum(e => e.Monto), g.Count(), Math.Round(g.Average(e => e.Monto), 2), g.Max(e => e.Monto),
            g.Where(e => e.Aprobado).Sum(e => e.Monto), g.Where(e => e.Estado == ExpenseStates.Pending).Sum(e => e.Monto)))
            .OrderBy(t => t.Moneda).ToList();

        var approved = rows.Count(e => e.Aprobado);
        var rejected = rows.Count(e => e.Rechazado);
        var pending = rows.Count - approved - rejected;
        double Pct(int n) => rows.Count == 0 ? 0 : Math.Round(100.0 * n / rows.Count, 2);
        var approval = new ApprovalStat(approved, pending, Pct(approved), Pct(pending),
            rows.GroupBy(e => e.Moneda).OrderBy(g => g.Key).Select(g => new ApprovalByCurrency(g.Key,
                g.Count(e => e.Aprobado), g.Count(e => e.Estado == ExpenseStates.Pending),
                g.Where(e => e.Aprobado).Sum(e => e.Monto), g.Where(e => e.Estado == ExpenseStates.Pending).Sum(e => e.Monto),
                g.Count(e => e.Rechazado), g.Where(e => e.Rechazado).Sum(e => e.Monto))).ToList(),
            rejected, Pct(rejected));

        // Coverage = the days the data can actually speak for: the dataset's own range narrowed by the active date filter.
        DateOnly? covFrom = null, covTo = null;
        if (snap.Active.Count > 0)
        {
            covFrom = snap.Active.Min(e => e.Fecha); covTo = snap.Active.Max(e => e.Fecha);
            if (filter.From is { } f && f > covFrom) covFrom = f;
            if (filter.To is { } t && t < covTo) covTo = t;
        }
        var series = rows.GroupBy(e => (P: Period(e.Fecha, granularity), e.Moneda)).OrderBy(g => g.Key.P, StringComparer.Ordinal).ThenBy(g => g.Key.Moneda)
            .Select(g =>
            {
                var (start, end) = Bounds(g.Key.P, granularity);
                return new TimePoint(g.Key.P, g.Key.Moneda, g.Count(), g.Sum(e => e.Monto),
                    g.Where(e => e.Aprobado).Sum(e => e.Monto), g.Where(e => e.Estado == ExpenseStates.Pending).Sum(e => e.Monto),
                    g.Count(e => e.Aprobado), g.Count(e => e.Estado == ExpenseStates.Pending), g.Count(e => e.Rechazado), g.Where(e => e.Rechazado).Sum(e => e.Monto),
                    start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    covFrom is { } cf && covTo is { } ct && (start < cf || end > ct));
            }).ToList();

        return new DashboardSummary(snap.Revision, rows.Count, totals,
            By(rows, e => e.Departamento), By(rows, e => e.Categoria), By(rows, e => e.MetodoPago), series, granularity, approval);
    }

    /// <summary>One frame per calendar month (gaps filled) so the UI can replay spending month by month. Still keyed by currency.</summary>
    public Timeline Timeline(ExpenseFilter filter)
    {
        var snap = store.Current;
        var rows = filter.Apply(snap.Active).ToList();
        var byMonth = rows.GroupBy(e => Period(e.Fecha, "month")).ToDictionary(g => g.Key, g => g.ToList());
        var months = new List<string>();
        if (rows.Count > 0)
        {
            var first = rows.Min(e => e.Fecha); var last = rows.Max(e => e.Fecha);
            var span = (last.Year - first.Year) * 12 + last.Month - first.Month + 1;
            if (span <= 600)
                for (var d = new DateOnly(first.Year, first.Month, 1); d <= last; d = d.AddMonths(1)) months.Add(Period(d, "month"));
            else months.AddRange(byMonth.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }
        var frames = months.Select(m =>
        {
            var r = byMonth.TryGetValue(m, out var list) ? list : [];
            var totals = r.GroupBy(e => e.Moneda).OrderBy(g => g.Key).Select(g => new MonthCurrency(g.Key, g.Count(), g.Sum(e => e.Monto),
                g.Count(e => e.Aprobado), g.Where(e => e.Aprobado).Sum(e => e.Monto))).ToList();
            return new MonthFrame(m, totals, By(r, e => e.Departamento), By(r, e => e.Categoria));
        }).ToList();
        return new Timeline(snap.Revision, rows.Select(e => e.Moneda).Distinct().OrderBy(x => x).ToList(),
            rows.Select(e => e.Departamento).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
            rows.Select(e => e.Categoria).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(), frames);
    }

    internal static List<DimensionStat> By(IEnumerable<Expense> rows, Func<Expense, string> key) =>
        rows.GroupBy(e => (K: key(e), e.Moneda))
            .Select(g => new DimensionStat(g.Key.K, g.Key.Moneda, g.Count(), g.Sum(e => e.Monto), Math.Round(g.Average(e => e.Monto), 2)))
            .OrderBy(s => s.Moneda).ThenByDescending(s => s.Total).ToList();

    /// <summary>First and last calendar day of a bucket produced by <see cref="Period"/>.</summary>
    internal static (DateOnly Start, DateOnly End) Bounds(string period, string granularity)
    {
        var start = DateOnly.ParseExact(granularity == "month" ? period + "-01" : period, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (start, granularity switch { "day" => start, "week" => start.AddDays(6), _ => start.AddMonths(1).AddDays(-1) });
    }

    internal static string Period(DateOnly d, string granularity) => granularity switch
    {
        "day" => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "week" => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), // ISO week start (Monday)
        _ => d.ToString("yyyy-MM", CultureInfo.InvariantCulture),
    };
}
