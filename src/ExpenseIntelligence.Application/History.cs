using System.Globalization;
using System.Text.Json;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

public sealed record FieldChange(string Field, string Label, string? Before, string? After);

/// <summary>Field-by-field comparison of two versions of an expense (bookkeeping fields such as version/timestamps are ignored).</summary>
public static class ExpenseDiff
{
    public static List<FieldChange> Fields(Expense a, Expense b)
    {
        var list = new List<FieldChange>();
        void Add(string f, string label, string x, string y) { if (!string.Equals(x, y, StringComparison.Ordinal)) list.Add(new FieldChange(f, label, x, y)); }
        Add("descripcion", "Descripción", a.Descripcion, b.Descripcion);
        Add("monto", "Monto", Money(a.Monto), Money(b.Monto));
        Add("moneda", "Moneda", a.Moneda, b.Moneda);
        Add("categoria", "Categoría", a.Categoria, b.Categoria);
        Add("departamento", "Departamento", a.Departamento, b.Departamento);
        Add("metodoPago", "Método de pago", a.MetodoPago, b.MetodoPago);
        Add("fecha", "Fecha", a.Fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), b.Fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("aprobado", "Estado", StateLabel(a), StateLabel(b));
        if (a.Rechazado && b.Rechazado) Add("motivoRechazo", "Motivo del rechazo", a.MotivoRechazo ?? "", b.MotivoRechazo ?? "");
        Add("codigoFactura", "Código de factura", a.CodigoFactura, b.CodigoFactura);
        return list;
    }

    private static string StateLabel(Expense e) => e.Aprobado ? "Aprobado" : e.Rechazado ? "Rechazado" : "Pendiente";
    private static string Money(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

public sealed record HistoryItem(
    DateTimeOffset At, string Action, string Group, int? ExpenseId, string Actor, long Revision, string Title, string? Detail, decimal? Amount, string? Currency,
    List<FieldChange> Changes, bool CanRestore, int Count, string? Note);

/// <summary>Turns the raw audit log into something a person can read: what changed, who, and whether a deletion can still be undone.</summary>
public sealed class HistoryService(IExpenseStore store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="group">all | edits | creates | deletes | restores | files (null = all)</param>
    public IReadOnlyList<HistoryItem> Get(int take, string? group)
    {
        var snap = store.Current;
        var items = new List<HistoryItem>();
        foreach (var a in store.ReadAudit(500)) // newest first
        {
            var g = GroupOf(a.Action);
            if (items.Count > 0 && g == "bulk" && items[^1] is { Group: "bulk" } prev && prev.Revision == a.Revision && prev.Action == a.Action)
            {
                items[^1] = prev with { Count = prev.Count + 1, Title = $"{Label(a.Action)}: {prev.Count + 1} registros" };
                continue;
            }
            items.Add(Map(a, g, snap));
        }
        var wanted = string.IsNullOrWhiteSpace(group) || group == "all" ? items : items.Where(i => i.Group == group || (group == "edits" && i.Group == "bulk"));
        return wanted.Take(Math.Clamp(take, 1, 500)).ToList();
    }

    private static HistoryItem Map(AuditEntry a, string g, DatasetSnapshot snap)
    {
        Expense? before = Parse(a.Before), after = Parse(a.After);
        var id = a.ExpenseId;
        var changes = before is not null && after is not null ? ExpenseDiff.Fields(before, after) : [];
        var subject = after ?? before;
        var title = a.Action switch
        {
            "create" => $"Gasto #{id} creado",
            "update" => $"Gasto #{id} editado",
            "delete" => $"Gasto #{id} eliminado",
            "restore" => $"Gasto #{id} restaurado",
            "approve" => $"Gasto #{id} aprobado", "reject" => $"Gasto #{id} rechazado", "reopen" => $"Gasto #{id} vuelto a pendiente",
            _ => Label(a.Action),
        };
        string? detail = subject is null ? a.Note : subject.Descripcion;
        var canRestore = a.Action == "delete" && id is { } i && snap.ById.TryGetValue(i, out var cur) && cur.Eliminado;
        if (a.Action.StartsWith("doctor", StringComparison.Ordinal) || a.Action.StartsWith("bulk-", StringComparison.Ordinal)) title = $"{Label(a.Action)}: 1 registro";
        return new HistoryItem(a.At, a.Action, g, id, a.Actor, a.Revision, title, detail, subject?.Monto, subject?.Moneda, changes, canRestore, 1, subject is null ? null : a.Note ?? (a.Action.StartsWith("import", StringComparison.Ordinal) ? $"{a.Before} → {a.After}" : null));
    }

    internal static string GroupOf(string action) => action switch
    {
        "create" => "creates", "update" or "approve" or "reject" or "reopen" => "edits", "delete" => "deletes", "restore" => "restores",
        _ when action.StartsWith("import", StringComparison.Ordinal) || action.StartsWith("dataset", StringComparison.Ordinal) || action == "factory-reset" => "files",
        _ => "bulk",
    };

    private static string Label(string action) => action switch
    {
        "import" => "Archivo cargado", "import-merge" => "Registros agregados desde un archivo",
        "dataset-switch" => "Archivo activo cambiado", "dataset-reset" => "Archivo restablecido a su original",
        "bulk-approve" => "Aprobación masiva", "bulk-reject" => "Rechazo masivo",
        "dataset-delete" => "Archivo eliminado del sistema", "factory-reset" => "Sistema dejado como nuevo",
        _ when action.StartsWith("doctor", StringComparison.Ordinal) => "Corrección automática (Data Doctor)",
        _ => action,
    };

    private static Expense? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json[0] != '{') return null;
        try { return JsonSerializer.Deserialize<Expense>(json, Json); } catch (JsonException) { return null; }
    }
}
