using System.Globalization;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

/// <summary>Category: alertas | tendencias | distribucion. Priority: higher = more relevant (warnings first); the UI only sorts by it.</summary>
public sealed record Insight(string Id, string Type, string Severity, string Title, string Detail, Dictionary<string, object?> Data, string Category = "distribucion", int Priority = 0);

/// <summary>Smart Insights: every conclusion is a deterministic calculation over the live snapshot and exposes the numbers behind it.</summary>
public sealed class InsightsService(IExpenseStore store)
{
    /// <summary>Spanish number format (decimal comma, non-breaking space for thousands) without depending on ICU data being present on the host.</summary>
    private static readonly CultureInfo Es = BuildEs();
    private static CultureInfo BuildEs()
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.NumberFormat.NumberDecimalSeparator = ","; c.NumberFormat.NumberGroupSeparator = "\u00A0";
        c.NumberFormat.PercentDecimalSeparator = ","; c.NumberFormat.PercentGroupSeparator = "\u00A0";
        return c;
    }
    private static string T(FormattableString s) => s.ToString(Es);
    private static string N(decimal v) => v.ToString("N2", Es);

    private static (string Category, int Priority) Classify(Insight i)
    {
        var warn = i.Severity == "warning";
        var magnitude = i.Data.TryGetValue("cambioPct", out var c) && c is double d ? (int)Math.Min(Math.Abs(d), 50) : 0;
        return i.Type switch
        {
            "backlog" => ("alertas", warn ? 100 : 60),
            "outlier" => ("alertas", warn ? 95 : 20),
            "duplicates" => ("alertas", warn ? 90 : 15),
            "period-change" => warn ? ("alertas", 80 + magnitude / 5) : ("tendencias", 50),
            "growth" => ("tendencias", warn ? 75 : 45), // department growth is a trend even when it is large
            _ => ("distribucion", warn ? 55 : 30), // concentration
        };
    }

    public (long Revision, List<Insight> Insights) Generate(ExpenseFilter filter)
    {
        var snap = store.Current;
        var rows = filter.Apply(snap.Active).ToList();
        var list = new List<Insight>();
        if (rows.Count == 0) return (snap.Revision, list);
        var anchor = rows.Max(e => e.Fecha);

        DepartmentGrowth(rows, anchor, list);
        MonthOverMonth(rows, anchor, list);
        Concentration(rows, list);
        Outliers(rows, list);
        PossibleDuplicates(rows, list);
        PendingBacklog(rows, anchor, list);
        // Trends that are merely informative stay under "tendencias"; any warning is promoted to "alertas".
        var classified = list.Select(i => { var (cat, pr) = Classify(i); return i with { Category = cat, Priority = pr }; })
            .OrderByDescending(i => i.Priority).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        return (snap.Revision, classified);
    }

    // Last 90 days of data vs the 90 days before, anchored on the newest record (robust to partial months).
    private static void DepartmentGrowth(List<Expense> rows, DateOnly anchor, List<Insight> list)
    {
        var curFrom = anchor.AddDays(-89); var prevFrom = curFrom.AddDays(-90);
        foreach (var cur in rows.Select(r => r.Moneda).Distinct().Order())
        {
            var inCur = rows.Where(r => r.Moneda == cur).ToList();
            var stats = inCur.Select(r => r.Departamento).Distinct().Select(d =>
            {
                var c = inCur.Where(r => r.Departamento == d && r.Fecha >= curFrom).Sum(r => r.Monto);
                var p = inCur.Where(r => r.Departamento == d && r.Fecha >= prevFrom && r.Fecha < curFrom).Sum(r => r.Monto);
                return (Dep: d, Cur: c, Prev: p, Pct: p > 0 ? (double)((c - p) / p * 100m) : double.NaN);
            }).Where(s => !double.IsNaN(s.Pct)).OrderByDescending(s => s.Pct).ToList();
            if (stats.Count == 0) continue;
            var top = stats[0];
            list.Add(new Insight($"growth-{cur}", "growth", top.Pct > 15 ? "warning" : "info",
                $"{top.Dep} lidera el crecimiento en {cur}",
                T($"Gasto de los últimos 90 días ({curFrom:yyyy-MM-dd} → {anchor:yyyy-MM-dd}): {N(top.Cur)} {cur} frente a {N(top.Prev)} {cur} en los 90 días previos ({top.Pct:+0.0;-0.0} %)."),
                new() { ["moneda"] = cur, ["dimension"] = "departamento", ["desde"] = curFrom.ToString("yyyy-MM-dd"), ["hasta"] = anchor.ToString("yyyy-MM-dd"), ["ranking"] = stats.Select(s => new { departamento = s.Dep, actual = s.Cur, previo = s.Prev, cambioPct = Math.Round(s.Pct, 2) }).ToList() }));
        }
    }

    private static void MonthOverMonth(List<Expense> rows, DateOnly anchor, List<Insight> list)
    {
        var m1 = new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(-1); // last fully elapsed month
        var m2 = m1.AddMonths(-1);
        foreach (var cur in rows.Select(r => r.Moneda).Distinct().Order())
        {
            decimal Sum(DateOnly m) => rows.Where(r => r.Moneda == cur && r.Fecha.Year == m.Year && r.Fecha.Month == m.Month).Sum(r => r.Monto);
            var a = Sum(m1); var b = Sum(m2);
            if (b <= 0) continue;
            var pct = (double)((a - b) / b * 100m);
            list.Add(new Insight($"mom-{cur}", "period-change", Math.Abs(pct) > 20 ? "warning" : "info",
                $"{cur}: {m1:yyyy-MM} vs {m2:yyyy-MM}",
                T($"Total {N(a)} {cur} en {m1:yyyy-MM} frente a {N(b)} {cur} en {m2:yyyy-MM} ({pct:+0.0;-0.0} %)."),
                new() { ["moneda"] = cur, ["desde"] = m1.ToString("yyyy-MM-dd"), ["hasta"] = m1.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"), ["mesActual"] = m1.ToString("yyyy-MM"), ["mesPrevio"] = m2.ToString("yyyy-MM"), ["total"] = a, ["totalPrevio"] = b, ["cambioPct"] = Math.Round(pct, 2) }));
        }
    }

    private static void Concentration(List<Expense> rows, List<Insight> list)
    {
        foreach (var cur in rows.Select(r => r.Moneda).Distinct().Order())
        {
            var inCur = rows.Where(r => r.Moneda == cur).ToList();
            var total = inCur.Sum(r => r.Monto);
            if (total <= 0) continue;
            foreach (var (label, key) in new (string, Func<Expense, string>)[] { ("categoría", r => r.Categoria), ("departamento", r => r.Departamento) })
            {
                var shares = inCur.GroupBy(key).Select(g => (K: g.Key, S: (double)(g.Sum(r => r.Monto) / total))).OrderByDescending(x => x.S).ToList();
                var hhi = Math.Round(shares.Sum(x => x.S * x.S) * 10000);
                list.Add(new Insight($"conc-{label}-{cur}", "concentration", shares[0].S > 0.25 ? "warning" : "info",
                    $"Concentración por {label} ({cur})",
                    T($"{shares[0].K} concentra {shares[0].S:P1} del gasto en {cur}. Índice HHI = {hhi:N0} (≈{10000.0 / shares.Count:N0} sería un reparto perfectamente uniforme)."),
                    new() { ["moneda"] = cur, ["dimension"] = label, ["hhi"] = hhi, ["shares"] = shares.Select(s => new { clave = s.K, porcentaje = Math.Round(s.S * 100, 2) }).ToList() }));
            }
        }
    }

    // Statistical outliers: |z| > 3 within (categoría, moneda).
    private static void Outliers(List<Expense> rows, List<Insight> list)
    {
        var found = new List<(Expense E, double Z)>();
        foreach (var g in rows.GroupBy(r => (r.Categoria, r.Moneda)).Where(g => g.Count() >= 30))
        {
            var mean = g.Average(r => (double)r.Monto);
            var sd = Math.Sqrt(g.Average(r => Math.Pow((double)r.Monto - mean, 2)));
            if (sd <= 0) continue;
            found.AddRange(g.Select(r => (E: r, Z: ((double)r.Monto - mean) / sd)).Where(x => Math.Abs(x.Z) > 3));
        }
        found = found.OrderByDescending(x => Math.Abs(x.Z)).ToList();
        list.Add(found.Count == 0
            ? new Insight("outliers", "outlier", "info", "Sin montos atípicos",
                "Ningún gasto se desvía más de 3 desviaciones estándar de la media de su categoría y moneda.", new() { ["count"] = 0 })
            : new Insight("outliers", "outlier", "warning", $"{found.Count} montos atípicos",
                T($"Gastos a más de 3σ de la media de su categoría/moneda. El más extremo: #{found[0].E.Id} ({N(found[0].E.Monto)} {found[0].E.Moneda}, z={found[0].Z:0.0})."),
                new() { ["count"] = found.Count, ["recordIds"] = found.Take(25).Select(x => x.E.Id).ToList() }));
    }

    public static List<List<Expense>> FindPossibleDuplicates(IEnumerable<Expense> rows, int windowDays = 3)
    {
        var groups = new List<List<Expense>>();
        foreach (var g in rows.GroupBy(r => (r.Departamento, r.Categoria, r.Moneda, r.Monto)).Where(g => g.Count() > 1))
        {
            var ordered = g.OrderBy(r => r.Fecha).ToList();
            var cluster = new List<Expense> { ordered[0] };
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Fecha.DayNumber - cluster[^1].Fecha.DayNumber <= windowDays) cluster.Add(ordered[i]);
                else { if (cluster.Count > 1) groups.Add(cluster); cluster = [ordered[i]]; }
            }
            if (cluster.Count > 1) groups.Add(cluster);
        }
        return groups;
    }

    private static void PossibleDuplicates(List<Expense> rows, List<Insight> list)
    {
        var dups = FindPossibleDuplicates(rows);
        list.Add(dups.Count == 0
            ? new Insight("duplicates", "duplicates", "info", "Sin transacciones duplicadas probables",
                "No hay gastos con mismo departamento, categoría, moneda y monto exacto en una ventana de 3 días.", new() { ["groups"] = 0 })
            : new Insight("duplicates", "duplicates", "warning", $"{dups.Count} grupos de posibles duplicados",
                "Mismo departamento, categoría, moneda y monto exacto en una ventana de 3 días.",
                new() { ["groups"] = dups.Count, ["examples"] = dups.Take(10).Select(d => d.Select(x => x.Id).ToList()).ToList() }));
    }

    private void PendingBacklog(List<Expense> rows, DateOnly anchor, List<Insight> list)
    {
        var cutoff = anchor.AddDays(-60);
        foreach (var cur in rows.Select(r => r.Moneda).Distinct().Order())
        {
            var old = rows.Where(r => r.Moneda == cur && r.Estado == ExpenseStates.Pending && r.Fecha < cutoff).ToList();
            if (old.Count == 0) continue;
            list.Add(new Insight($"backlog-{cur}", "backlog", "warning", $"Pendientes con más de 60 días ({cur})",
                T($"{old.Count} gastos sin aprobar anteriores a {cutoff:yyyy-MM-dd} suman {N(old.Sum(r => r.Monto))} {cur}."),
                new() { ["moneda"] = cur, ["hasta"] = cutoff.AddDays(-1).ToString("yyyy-MM-dd"), ["count"] = old.Count, ["total"] = old.Sum(r => r.Monto), ["oldestIds"] = old.OrderBy(r => r.Fecha).Take(10).Select(r => r.Id).ToList() }));
        }
    }
}
