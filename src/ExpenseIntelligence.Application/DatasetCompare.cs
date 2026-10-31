using System.Globalization;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

public sealed record DiffRow(string Kind, string Key, Expense? A, Expense? B, List<FieldChange> Changes);
public sealed record CurrencyStat(string Moneda, int Count, decimal Total);
public sealed record SideSummary(string Label, int Records, double ApprovedPct, List<CurrencyStat> ByCurrency);
public sealed record CompareCounts(int Added, int Removed, int Changed, int Same, int IgnoredDuplicates, Dictionary<string, int> FieldChanges);
public sealed record CompareTotals(string Moneda, int CountA, decimal TotalA, int CountB, decimal TotalB);

/// <summary>Record-level comparison of two sets of expenses. Records are paired by id (default) or by invoice code; currencies are never summed together.</summary>
public static class DatasetComparer
{
    public static (List<DiffRow> Rows, CompareCounts Counts) Diff(IReadOnlyList<Expense> a, IReadOnlyList<Expense> b, string key)
    {
        Func<Expense, string> k = key == "factura"
            ? e => string.IsNullOrWhiteSpace(e.CodigoFactura) ? "∅#" + e.Id.ToString(CultureInfo.InvariantCulture) : e.CodigoFactura.Trim().ToUpperInvariant()
            : e => e.Id.ToString(CultureInfo.InvariantCulture);

        int ignored = 0;
        Dictionary<string, Expense> Index(IReadOnlyList<Expense> src)
        {
            var d = new Dictionary<string, Expense>(src.Count, StringComparer.Ordinal);
            foreach (var e in src) if (!d.TryAdd(k(e), e)) ignored++; // first one wins; repeated keys cannot be paired reliably
            return d;
        }
        var ia = Index(a); var ib = Index(b);

        var rows = new List<DiffRow>();
        var fieldCounts = new Dictionary<string, int>();
        int same = 0, changed = 0, added = 0, removed = 0;
        foreach (var (kk, ea) in ia)
        {
            if (!ib.TryGetValue(kk, out var eb)) { removed++; rows.Add(new DiffRow("removed", kk, ea, null, [])); continue; }
            var ch = ExpenseDiff.Fields(ea, eb);
            if (ch.Count == 0) { same++; continue; }
            changed++;
            foreach (var c in ch) fieldCounts[c.Label] = fieldCounts.GetValueOrDefault(c.Label) + 1;
            rows.Add(new DiffRow("changed", kk, ea, eb, ch));
        }
        foreach (var (kk, eb) in ib)
            if (!ia.ContainsKey(kk)) { added++; rows.Add(new DiffRow("added", kk, null, eb, [])); }

        static int Order(string kind) => kind switch { "changed" => 0, "added" => 1, _ => 2 };
        rows.Sort((x, y) => Order(x.Kind) != Order(y.Kind) ? Order(x.Kind).CompareTo(Order(y.Kind)) : string.CompareOrdinal(x.Key.PadLeft(12), y.Key.PadLeft(12)));
        return (rows, new CompareCounts(added, removed, changed, same, ignored, fieldCounts.OrderByDescending(f => f.Value).ToDictionary(f => f.Key, f => f.Value)));
    }

    public static SideSummary Summarize(string label, IReadOnlyList<Expense> rows) => new(label, rows.Count,
        rows.Count == 0 ? 0 : Math.Round(100.0 * rows.Count(e => e.Aprobado) / rows.Count, 1),
        rows.GroupBy(e => e.Moneda).OrderBy(g => g.Key).Select(g => new CurrencyStat(g.Key, g.Count(), g.Sum(e => e.Monto))).ToList());

    public static List<CompareTotals> Totals(SideSummary a, SideSummary b) =>
        a.ByCurrency.Select(x => x.Moneda).Union(b.ByCurrency.Select(x => x.Moneda)).OrderBy(m => m, StringComparer.Ordinal).Select(m =>
        {
            var x = a.ByCurrency.FirstOrDefault(c => c.Moneda == m); var y = b.ByCurrency.FirstOrDefault(c => c.Moneda == m);
            return new CompareTotals(m, x?.Count ?? 0, x?.Total ?? 0, y?.Count ?? 0, y?.Total ?? 0);
        }).ToList();
}
