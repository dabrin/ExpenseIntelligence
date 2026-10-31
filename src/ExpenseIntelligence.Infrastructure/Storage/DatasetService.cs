using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Infrastructure.Storage;

public sealed record DatasetSummary(
    string Id, string Name, string Sha256, long Bytes, DateTimeOffset AddedAt, bool IsActive, bool IsSeed,
    int OriginalRecords, int Records, int Changed, int Added, int Removed, bool HasEdits);

public sealed record CompareResponse(
    string LabelA, string LabelB, SideSummary A, SideSummary B, List<CompareTotals> Totals, CompareCounts Counts, PagedResult<DiffRow> Rows);

/// <summary>Read-side view of the uploaded files: what exists, how far each one has drifted from its original, and side-by-side comparisons.</summary>
public sealed class DatasetService(JsonExpenseStore store)
{
    private readonly object _gate = new();
    private (string Key, List<DiffRow> Rows, CompareCounts Counts, SideSummary A, SideSummary B, string LA, string LB)? _last;

    public List<DatasetSummary> List() => store.Catalog.Items.OrderByDescending(d => d.AddedAt).Select(Summarize).ToList();

    public DatasetSummary Summarize(DatasetInfo ds)
    {
        var original = store.LoadRows(ds.Id, RowVariant.Original);
        var current = store.LoadRows(ds.Id, RowVariant.Actual);
        var (_, c) = DatasetComparer.Diff(original, current, "id");
        return new DatasetSummary(ds.Id, ds.Name, ds.Sha256, ds.Bytes, ds.AddedAt, ds.Id == store.Catalog.ActiveId, ds.IsSeed,
            ds.OriginalRecords, current.Count, c.Changed, c.Added, c.Removed, c.Changed + c.Added + c.Removed > 0);
    }

    public CompareResponse Compare(string a, string aVariant, string b, string bVariant, string? key, string? kind, string? q, int page, int pageSize)
    {
        aVariant = Variant(aVariant); bVariant = Variant(bVariant);
        key = key == "factura" ? "factura" : "id";
        if (a == b && aVariant == bVariant) throw new DatasetRuleException("Elige dos versiones distintas para comparar.");
        var da = store.FindDataset(a) ?? throw new DatasetNotFoundException(a);
        var db = store.FindDataset(b) ?? throw new DatasetNotFoundException(b);

        var cacheKey = $"{store.Current.Revision}|{a}|{aVariant}|{b}|{bVariant}|{key}";
        List<DiffRow> rows; CompareCounts counts; SideSummary sa, sb; string la, lb;
        lock (_gate)
        {
            if (_last is { } l && l.Key == cacheKey) (rows, counts, sa, sb, la, lb) = (l.Rows, l.Counts, l.A, l.B, l.LA, l.LB);
            else
            {
                var ra = store.LoadRows(a, aVariant); var rb = store.LoadRows(b, bVariant);
                (rows, counts) = DatasetComparer.Diff(ra, rb, key);
                la = Label(da, aVariant, store.Catalog.Items.Count(d => d.Name == da.Name) > 1);
                lb = Label(db, bVariant, store.Catalog.Items.Count(d => d.Name == db.Name) > 1);
                sa = DatasetComparer.Summarize(la, ra); sb = DatasetComparer.Summarize(lb, rb);
                _last = (cacheKey, rows, counts, sa, sb, la, lb);
            }
        }

        IEnumerable<DiffRow> filtered = rows;
        if (kind is "added" or "removed" or "changed") filtered = filtered.Where(r => r.Kind == kind);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            filtered = filtered.Where(r => Hit(r.A, t) || Hit(r.B, t) || r.Key.Contains(t, StringComparison.OrdinalIgnoreCase));
        }
        var list = filtered.ToList();
        var size = Math.Clamp(pageSize, 5, 100);
        var pages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)size));
        var p = Math.Clamp(page, 1, pages);
        return new CompareResponse(la, lb, sa, sb, DatasetComparer.Totals(sa, sb), counts,
            new PagedResult<DiffRow>(list.Skip((p - 1) * size).Take(size).ToList(), list.Count, p, size, pages, store.Current.Revision));
    }

    private static bool Hit(Expense? e, string t) => e is not null &&
        (e.Descripcion.Contains(t, StringComparison.OrdinalIgnoreCase) || e.CodigoFactura.Contains(t, StringComparison.OrdinalIgnoreCase) || e.Departamento.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static string Variant(string v) => v == RowVariant.Original ? RowVariant.Original : RowVariant.Actual;

    private static string Label(DatasetInfo d, string variant, bool ambiguous) =>
        $"{d.Name}{(ambiguous ? $" ({d.AddedAt.ToLocalTime():dd/MM HH:mm})" : "")} · {(variant == RowVariant.Original ? "original" : "con cambios")}";
}
