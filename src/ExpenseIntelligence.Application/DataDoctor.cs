using System.Text.RegularExpressions;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

public sealed record Anomaly(string Code, string Severity, string Title, string Cause, string Recommendation, int AffectedCount,
    List<int> RecordIds, bool AutoFixable, string Scope);

public sealed record DoctorReport(long Revision, int Records, int AffectedRecords, double QualityScore, List<Anomaly> Anomalies, LoadReport Ingest);

/// <summary>Data quality centre: read-only detection rules over the live snapshot + explicit, audited safe fixes.</summary>
public sealed partial class DataDoctorService(IExpenseStore store, TimeProvider clock)
{
    [GeneratedRegex(@"^Gasto operativo #(\d+) - (.+) \((.+)\)$")]
    private static partial Regex DescRegex();

    public DoctorReport Analyze() { var snap = store.Current; return Analyze(snap.Active, store.GetStatus().LoadReport, snap.Revision); }

    /// <summary>Analyses any set of rows (the live dataset or an uploaded file that has not been imported yet).</summary>
    public DoctorReport Analyze(IReadOnlyList<Expense> rows, LoadReport lr, long revision)
    {
        var list = new List<Anomaly>();
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

        // --- ingest findings (historical, from the file as received) ---
        if (lr.Repaired > 0)
            list.Add(new("INGEST_REPAIRED", "warning", "Registros reparados al ingerir el archivo",
                string.Join(" | ", lr.Repairs.Select(r => $"línea {r.Line}: {r.Description}")),
                "Reparación determinista y auditada (comillas de clave faltantes). El archivo original se conserva intacto; revise el registro afectado.",
                lr.Repaired, lr.Repairs.Where(r => r.RecordId.HasValue).Select(r => r.RecordId!.Value).ToList(), false, "ingest"));
        if (lr.Quarantined > 0)
            list.Add(new("INGEST_QUARANTINED", "error", "Registros en cuarentena", string.Join(" | ", lr.Issues.Take(5).Select(i => $"línea {i.Line}: {i.Reason}")),
                "Revise ingest-report.json / quarantine.json y corrija manualmente; no se descartó nada en silencio.", lr.Quarantined, [], false, "ingest"));
        if (lr.Truncated)
            list.Add(new("INGEST_TRUNCATED", "error", "Archivo truncado", "El archivo termina antes de cerrar el arreglo JSON.",
                "Solicite una exportación completa; el fragmento final se guardó en cuarentena.", 1, [], false, "ingest"));

        // --- live data rules ---
        Add(list, "NONPOSITIVE_AMOUNT", "error", "Montos no positivos", "Monto <= 0.", "Corregir el monto o eliminar el registro tras revisión.",
            rows.Where(e => e.Monto <= 0), false);
        Add(list, "UNKNOWN_CURRENCY", "error", "Moneda desconocida", $"La moneda no es una de {string.Join("/", Catalog.Currencies)}.", "Normalizar la moneda.",
            rows.Where(e => !Catalog.Currencies.Contains(e.Moneda)), false);
        Add(list, "FUTURE_DATE", "error", "Fechas futuras", $"Fecha posterior a hoy ({today:yyyy-MM-dd}).", "Verificar la fecha con el responsable del gasto.",
            rows.Where(e => e.Fecha > today), false);
        Add(list, "OLD_DATE", "warning", "Fechas anteriores al año 2000", "Fecha improbable para un gasto corporativo.", "Verificar el origen.",
            rows.Where(e => e.Fecha < ExpenseService.MinDate), false);

        foreach (var g in new (string F, Func<Expense, string> Sel)[] { ("departamento", e => e.Departamento), ("categoría", e => e.Categoria), ("método de pago", e => e.MetodoPago) })
        {
            var variants = rows.GroupBy(e => TextNorm.Key(g.Sel(e))).Where(k => k.Select(g.Sel).Distinct().Count() > 1).ToList();
            if (variants.Count == 0) continue;
            var affected = variants.SelectMany(v => v).ToList();
            list.Add(new($"SEMANTIC_DUP_{g.F.ToUpperInvariant().Replace(' ', '_').Replace("Í", "I")}", "warning", $"Duplicados semánticos en {g.F}",
                string.Join("; ", variants.Take(5).Select(v => string.Join(" ≈ ", v.Select(g.Sel).Distinct()))),
                "Unificar a la grafía más frecuente (corrección segura disponible).", affected.Count, Ids(affected), true, "data"));
        }

        Add(list, "WHITESPACE", "warning", "Espacios sobrantes", "Texto con espacios al inicio, al final o dobles.", "Recortar y colapsar espacios (corrección segura disponible).",
            rows.Where(HasSloppyWhitespace), true);

        var dupInvoice = rows.GroupBy(e => e.CodigoFactura, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        if (dupInvoice.Count > 0)
        {
            var all = dupInvoice.SelectMany(g => g).ToList();
            list.Add(new("DUP_INVOICE", "warning", "Códigos de factura repetidos",
                $"{dupInvoice.Count} códigos aparecen en más de un gasto con datos distintos (p. ej. {dupInvoice[0].Key} en #{string.Join(", #", dupInvoice[0].Select(e => e.Id))}). Son códigos de 5 dígitos: las colisiones por azar son esperables.",
                "Confirmar con Contabilidad si es un error de captura o un reuso legítimo. No se corrige automáticamente.", all.Count, Ids(all), false, "data"));
        }

        var dupTx = InsightsService.FindPossibleDuplicates(rows);
        if (dupTx.Count > 0)
            list.Add(new("POSSIBLE_DUP_TX", "warning", "Posibles transacciones duplicadas", "Mismo departamento, categoría, moneda y monto en ≤3 días.",
                "Revisar cada grupo antes de eliminar.", dupTx.Sum(g => g.Count), Ids(dupTx.SelectMany(g => g)), false, "data"));

        var mism = rows.Where(e => DescRegex().Match(e.Descripcion) is { Success: true } m &&
                                   (m.Groups[1].Value != e.Id.ToString() || m.Groups[2].Value != e.Categoria || m.Groups[3].Value != e.Departamento)).ToList();
        Add(list, "DESC_MISMATCH", "info", "Descripción inconsistente con los campos", "El texto «Gasto operativo #id - categoría (departamento)» no coincide con los campos reales.",
            "Revisar y regenerar la descripción.", mism, false);

        // Scale check: CRC amounts statistically indistinguishable from USD/EUR.
        decimal Median(string c) { var v = rows.Where(e => e.Moneda == c).Select(e => e.Monto).Order().ToArray(); return v.Length == 0 ? 0 : v[v.Length / 2]; }
        var mCrc = Median("CRC"); var mUsd = Median("USD");
        if (mCrc > 0 && mUsd > 0 && mCrc / mUsd < 10)
        {
            var crc = rows.Where(e => e.Moneda == "CRC").ToList();
            list.Add(new("CRC_SCALE", "info", "Montos en CRC con magnitud de USD/EUR",
                $"Mediana CRC = {mCrc:N2} vs USD = {mUsd:N2}. Con un tipo de cambio ≈500 CRC/USD se esperaría una mediana CRC ~100x mayor; la moneda podría estar mal etiquetada.",
                "Confirmar con el equipo de ingesta. No se convierte ni se altera ningún monto; los totales se muestran siempre por moneda.", crc.Count, [], false, "data"));
        }

        var affectedIds = list.Where(a => a.Severity != "info" && a.Scope == "data").SelectMany(a => a.RecordIds).ToHashSet();
        var score = rows.Count == 0 ? 100 : Math.Round(100.0 * (1 - (double)affectedIds.Count / rows.Count), 1);
        var order = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1, ["info"] = 2 };
        return new DoctorReport(revision, rows.Count, affectedIds.Count, score, list.OrderBy(a => order[a.Severity]).ThenBy(a => a.Code).ToList(), lr);
    }

    /// <summary>Applies a safe, deterministic fix. Only whitelisted codes are allowed; every change is audited per record.</summary>
    public async Task<int> FixAsync(string code, string actor, CancellationToken ct = default)
    {
        var rows = store.Current.Active;
        if (code == "WHITESPACE")
            return await store.BulkUpdateAsync(e => HasSloppyWhitespace(e) ? Clean(e) : null, "doctor-fix:WHITESPACE", actor, ct);

        if (code.StartsWith("SEMANTIC_DUP_", StringComparison.Ordinal))
        {
            // canonical spelling = most frequent variant per normalised key
            Dictionary<string, string> Canon(Func<Expense, string> sel) => rows.GroupBy(e => TextNorm.Key(sel(e)))
                .ToDictionary(g => g.Key, g => g.GroupBy(sel).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key);
            var dep = Canon(e => e.Departamento); var cat = Canon(e => e.Categoria); var pay = Canon(e => e.MetodoPago);
            var which = code["SEMANTIC_DUP_".Length..];
            return await store.BulkUpdateAsync(e =>
            {
                var n = e with
                {
                    Departamento = which.StartsWith("DEPARTAMENTO") ? dep[TextNorm.Key(e.Departamento)] : e.Departamento,
                    Categoria = which.StartsWith("CATEGOR") ? cat[TextNorm.Key(e.Categoria)] : e.Categoria,
                    MetodoPago = which.StartsWith("M") ? pay[TextNorm.Key(e.MetodoPago)] : e.MetodoPago,
                };
                return n.Departamento != e.Departamento || n.Categoria != e.Categoria || n.MetodoPago != e.MetodoPago ? n : null;
            }, $"doctor-fix:{code}", actor, ct);
        }
        throw new ValidationFailedException(new Dictionary<string, string[]> { ["code"] = [$"'{code}' no tiene una corrección segura automática."] });
    }

    private static bool HasSloppyWhitespace(Expense e) => new[] { e.Descripcion, e.Categoria, e.Departamento, e.MetodoPago, e.CodigoFactura, e.Moneda }.Any(s => Squash(s) != s);
    private static string Squash(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static Expense Clean(Expense e) => e with
    {
        Descripcion = Squash(e.Descripcion), Categoria = Squash(e.Categoria), Departamento = Squash(e.Departamento),
        MetodoPago = Squash(e.MetodoPago), CodigoFactura = Squash(e.CodigoFactura), Moneda = Squash(e.Moneda),
    };

    private static List<int> Ids(IEnumerable<Expense> e) => e.Select(x => x.Id).Distinct().Take(500).ToList();

    private static void Add(List<Anomaly> list, string code, string sev, string title, string cause, string rec, IEnumerable<Expense> hits, bool fix)
    {
        var l = hits.ToList();
        if (l.Count > 0) list.Add(new(code, sev, title, cause, rec, l.Count, Ids(l), fix, "data"));
    }
}
