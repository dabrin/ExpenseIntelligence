using System.Globalization;
using System.Text;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

public sealed record ExpenseFilter(
    DateOnly? From = null, DateOnly? To = null,
    string[]? Departamentos = null, string[]? Categorias = null, string[]? Monedas = null, string[]? MetodosPago = null,
    bool? Aprobado = null, string? Search = null, string? Estado = null)
{
    public static readonly ExpenseFilter None = new();

    /// <summary>Applies every active criterion (AND across fields, OR within a multi-value field).</summary>
    public IEnumerable<Expense> Apply(IEnumerable<Expense> source)
    {
        var dep = ToSet(Departamentos); var cat = ToSet(Categorias); var mon = ToSet(Monedas); var pay = ToSet(MetodosPago);
        var q = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        var estado = ExpenseStates.IsValid(Estado?.Trim().ToLowerInvariant()) ? Estado!.Trim().ToLowerInvariant() : null;
        return source.Where(e =>
            (From is null || e.Fecha >= From) && (To is null || e.Fecha <= To) &&
            (dep is null || dep.Contains(e.Departamento)) && (cat is null || cat.Contains(e.Categoria)) &&
            (mon is null || mon.Contains(e.Moneda)) && (pay is null || pay.Contains(e.MetodoPago)) &&
            (Aprobado is null || e.Aprobado == Aprobado) &&
            (estado is null || e.Estado == estado) &&
            (q is null || Matches(e, q)));
    }

    private static bool Matches(Expense e, string q) =>
        e.Descripcion.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        e.CodigoFactura.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        e.Id.ToString(CultureInfo.InvariantCulture) == q;

    private static HashSet<string>? ToSet(string[]? v)
    {
        var items = v?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        return items is { Length: > 0 } ? new HashSet<string>(items, StringComparer.OrdinalIgnoreCase) : null;
    }
}

public sealed record ExpensePageQuery(ExpenseFilter Filter, string? Sort = "fecha", bool Desc = true, int Page = 1, int PageSize = 25, bool Deleted = false);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize, int TotalPages, long Revision);

public sealed record Facets(string[] Departamentos, string[] Categorias, string[] Monedas, string[] MetodosPago, DateOnly? MinFecha, DateOnly? MaxFecha, long Revision, int DeletedCount = 0);

public sealed class ExpenseQueryService(IExpenseStore store)
{
    public const int MaxPageSize = 200;

    public PagedResult<Expense> Query(ExpensePageQuery q)
    {
        var snap = store.Current;
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var filtered = q.Filter.Apply(q.Deleted ? snap.All.Where(e => e.Eliminado) : snap.Active); // trash view lists logically deleted records
        var sorted = Sort(filtered, q.Sort, q.Desc).ToList();
        var total = sorted.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        page = Math.Min(page, totalPages);
        return new PagedResult<Expense>(sorted.Skip((page - 1) * size).Take(size).ToList(), total, page, size, totalPages, snap.Revision);
    }

    public Expense Get(int id)
    {
        var snap = store.Current;
        return snap.ById.TryGetValue(id, out var e) && !e.Eliminado ? e : throw new NotFoundException(id);
    }

    public Facets GetFacets()
    {
        var snap = store.Current;
        string[] D(Func<Expense, string> f) => snap.Active.Select(f).Distinct().Order(StringComparer.Create(new CultureInfo("es"), true)).ToArray();
        return new Facets(D(e => e.Departamento), D(e => e.Categoria), D(e => e.Moneda), D(e => e.MetodoPago),
            snap.Active.Count == 0 ? null : snap.Active.Min(e => e.Fecha), snap.Active.Count == 0 ? null : snap.Active.Max(e => e.Fecha), snap.Revision, snap.All.Count - snap.Active.Count);
    }

    private static IEnumerable<Expense> Sort(IEnumerable<Expense> src, string? sort, bool desc)
    {
        Func<Expense, object> key = (sort ?? "fecha").ToLowerInvariant() switch
        {
            "id" => e => e.Id,
            "monto" => e => e.Monto,
            "departamento" => e => e.Departamento,
            "categoria" => e => e.Categoria,
            "moneda" => e => e.Moneda,
            "metodopago" => e => e.MetodoPago,
            "aprobado" => e => e.Aprobado,
            "descripcion" => e => e.Descripcion,
            "factura" or "codigofactura" => e => e.CodigoFactura,
            _ => e => e.Fecha,
        };
        var cmp = Comparer<object>.Default;
        var ordered = desc ? src.OrderByDescending(key, cmp) : src.OrderBy(key, cmp);
        return ordered.ThenBy(e => e.Id);
    }
}

public static class TextNorm
{
    /// <summary>Case/accent/whitespace-insensitive key used to detect semantic duplicates ("Tecnologia " == "Tecnología").</summary>
    public static string Key(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
