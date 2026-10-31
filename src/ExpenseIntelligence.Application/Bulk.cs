using System.Diagnostics;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

public sealed class BulkOptions
{
    public const string Section = "Bulk";
    /// <summary>Most records one bulk operation may touch (explicit or "all filtered").</summary>
    public int MaxItems { get; set; } = 5000;
}

public sealed record BulkItem(int Id, int Version);

/// <summary>Either <see cref="Items"/> (explicit ids with the version the user saw) or <see cref="Filter"/> + <see cref="ExpectedCount"/> ("all results of this filter", resolved on the server).</summary>
public sealed record BulkRequest(string? DatasetId, List<BulkItem>? Items, ExpenseFilter? Filter, int? ExpectedCount, string? Reason);

public sealed record BulkTotal(string Moneda, int Count, decimal Total);
public sealed record BulkSkip(int Id, string Code, string Message);

public sealed record BulkPreview(
    string Operation, string DatasetId, int Requested, int Eligible, int Skipped, int Duplicates,
    List<BulkTotal> EligibleTotals, Dictionary<string, int> SkippedByReason, List<BulkSkip> SkippedSample, int MaxItems, long Revision);

public sealed record BulkResult(
    string OperationId, string Operation, int Requested, int Applied, int Skipped, int Duplicates,
    List<BulkTotal> AppliedTotals, Dictionary<string, int> SkippedByReason, List<BulkSkip> SkippedSample, long Revision, double ElapsedMs);

/// <summary>
/// Bulk approve / reject. All validation and the decision of what changes happen inside the store's writer lock against one snapshot,
/// and the result is persisted with one file write. Any conflict aborts the whole batch.
/// </summary>
public sealed class BulkService(IExpenseStore store, BulkOptions options)
{
    public const string Approve = "approve", Reject = "reject";
    public const int MinReason = 3, MaxReason = 500, SampleSize = 100;

    public int MaxItems => options.MaxItems;
    public string DatasetId => store.ActiveDatasetId;

    public BulkPreview Preview(string op, BulkRequest r)
    {
        RequireOp(op);
        var s = store.Current;
        var plan = Plan(s, store.ActiveDatasetId, op, r);
        return new BulkPreview(op, store.ActiveDatasetId, plan.Requested, plan.Eligible.Count, plan.Skipped.Count, plan.Duplicates,
            Totals(plan.Eligible), Reasons(plan.Skipped), plan.Skipped.Take(SampleSize).ToList(), options.MaxItems, s.Revision);
    }

    public async Task<BulkResult> RunAsync(string op, BulkRequest r, string actor, CancellationToken ct = default)
    {
        RequireOp(op);
        var reason = op == Reject ? CleanReason(r.Reason) : null;
        var opId = Guid.NewGuid().ToString("N")[..12];
        var sw = Stopwatch.StartNew();
        var result = await store.ApplyBatchAsync(s =>
        {
            var plan = Plan(s, store.ActiveDatasetId, op, r);
            var changes = plan.Eligible.ToDictionary(e => e.Id, e => op == Approve
                ? e with { Aprobado = true, Rechazado = false, MotivoRechazo = null }
                : e with { Aprobado = false, Rechazado = true, MotivoRechazo = reason });
            var res = new BulkResult(opId, op, plan.Requested, plan.Eligible.Count, plan.Skipped.Count, plan.Duplicates,
                Totals(plan.Eligible), Reasons(plan.Skipped), plan.Skipped.Take(SampleSize).ToList(),
                changes.Count > 0 ? s.Revision + 1 : s.Revision, 0);
            return new BatchPlan<BulkResult>(changes, res, reason is null ? $"op={opId}" : $"op={opId}; motivo={reason}");
        }, "bulk-" + op, actor, ct);
        return result with { ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1) };
    }

    private sealed record Planned(int Requested, int Duplicates, List<Expense> Eligible, List<BulkSkip> Skipped);

    private Planned Plan(DatasetSnapshot s, string activeDataset, string op, BulkRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.DatasetId))
            throw Invalid("datasetId", "Falta el archivo (dataset) al que pertenece la selección.");
        if (!string.Equals(r.DatasetId, activeDataset, StringComparison.Ordinal))
            throw new BulkConflictException("La selección pertenece a otro archivo distinto al que está activo. No se modificó nada.", 1,
                [new BulkConflict(null, "dataset", null, null, "El archivo activo cambió desde que seleccionaste.")]);

        var explicitMode = r.Items is { Count: > 0 };
        if (explicitMode == (r.Filter is not null))
            throw Invalid("seleccion", "Envía una lista de gastos o un filtro con la cantidad esperada, no ambos ni ninguno.");

        var picked = new List<Expense>(); var skipped = new List<BulkSkip>(); var conflicts = new List<BulkConflict>();
        int requested, duplicates = 0;

        if (explicitMode)
        {
            requested = r.Items!.Count;
            var seen = new HashSet<int>();
            foreach (var it in r.Items)
            {
                if (!seen.Add(it.Id)) { duplicates++; continue; }
                if (seen.Count > options.MaxItems) throw TooMany();
                if (!s.ById.TryGetValue(it.Id, out var e))
                    conflicts.Add(new BulkConflict(it.Id, "missing", it.Version, null, $"El gasto #{it.Id} no existe en este archivo."));
                else if (e.Version != it.Version)
                    conflicts.Add(new BulkConflict(it.Id, "version", it.Version, e.Version, $"El gasto #{it.Id} cambió desde que lo seleccionaste (versión {it.Version} → {e.Version})."));
                else picked.Add(e);
            }
            requested -= duplicates; // distinct records actually considered
        }
        else
        {
            var found = r.Filter!.Apply(s.Active).ToList();
            if (r.ExpectedCount is not { } expected)
                throw Invalid("expectedCount", "Falta la cantidad de resultados que viste al seleccionar.");
            if (found.Count != expected)
                throw new BulkConflictException($"Los resultados del filtro cambiaron (viste {expected}, ahora hay {found.Count}). No se modificó nada: revisa y vuelve a seleccionar.", 1,
                    [new BulkConflict(null, "selection-changed", expected, found.Count, "La cantidad de resultados ya no coincide.")]);
            if (found.Count > options.MaxItems) throw TooMany();
            requested = found.Count; picked = found;
        }

        if (conflicts.Count > 0)
            throw new BulkConflictException(
                $"{conflicts.Count} de {requested} gastos cambiaron o ya no existen. No se modificó nada: recarga la lista y vuelve a seleccionar.", conflicts.Count, conflicts.Take(SampleSize).ToList());

        var eligible = new List<Expense>(picked.Count);
        foreach (var e in picked)
        {
            if (e.Eliminado) skipped.Add(new BulkSkip(e.Id, "deleted", $"#{e.Id} está eliminado."));
            else if (e.Aprobado) skipped.Add(new BulkSkip(e.Id, "already-approved", op == Approve ? $"#{e.Id} ya estaba aprobado." : $"#{e.Id} está aprobado: solo se rechazan gastos pendientes."));
            else if (e.Rechazado) skipped.Add(new BulkSkip(e.Id, "already-rejected", $"#{e.Id} ya estaba rechazado."));
            else eligible.Add(e);
        }
        return new Planned(requested, duplicates, eligible, skipped);
    }

    private static string CleanReason(string? reason)
    {
        var t = reason?.Trim() ?? "";
        if (t.Length < MinReason) throw Invalid("motivo", $"El motivo del rechazo es obligatorio (mínimo {MinReason} caracteres).");
        if (t.Length > MaxReason) throw Invalid("motivo", $"El motivo no puede superar {MaxReason} caracteres.");
        return t;
    }

    private static void RequireOp(string op)
    {
        if (op is not (Approve or Reject)) throw Invalid("operation", "Operación desconocida: usa approve o reject.");
    }

    private ValidationFailedException TooMany() => Invalid("items", $"Una operación masiva admite hasta {options.MaxItems} gastos. Acota el filtro y repite.");
    private static ValidationFailedException Invalid(string field, string msg) => new(new Dictionary<string, string[]> { [field] = [msg] });

    private static List<BulkTotal> Totals(List<Expense> rows) => rows.GroupBy(e => e.Moneda).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new BulkTotal(g.Key, g.Count(), g.Sum(e => e.Monto))).ToList();

    private static Dictionary<string, int> Reasons(List<BulkSkip> skipped) => skipped.GroupBy(x => x.Code).ToDictionary(g => g.Key, g => g.Count());
}
