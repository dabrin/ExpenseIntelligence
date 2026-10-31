using System.Globalization;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

public sealed record AssistantAnswer(string Mode, string Intent, string Answer, string[] Columns, List<string[]> Rows, string Period, string Source, string[] Suggestions, string[] Scope);

/// <summary>
/// Conversational assistant WITHOUT an LLM. It understands intents plus entities (department, category, currency, payment method, period),
/// remembers the previous intent for follow-ups ("¿y en EUR?") and only calls the same read-only queries the dashboard uses.
/// Every number comes from the live snapshot.
/// </summary>
public sealed class AssistantService(IExpenseStore store, AnalyticsService analytics, TimeProvider clock)
{
    public static readonly string[] Examples =
    [
        "¿Qué departamento gastó más este mes?", "¿Cuáles son las categorías más costosas?", "¿Qué gastos siguen pendientes?",
        "¿Existen transacciones potencialmente duplicadas?", "¿Cuál es el total gastado por moneda?", "¿Qué porcentaje está aprobado?", "¿Qué método de pago se usa más?",
    ];

    private static readonly string[] Months = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];
    private static string N(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
    private static bool Has(string q, params string[] words) => words.Any(q.Contains);
    private static string Pick(string seed, params string[] options) => options[Math.Abs(seed.GetHashCode(StringComparison.Ordinal) % options.Length)];

    public AssistantAnswer Ask(string question, string? lastIntent = null, string? lastQuestion = null)
    {
        var q = TextNorm.Key(question);
        var snap = store.Current;

        // --- small talk ---
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 3 && Has(q, "hola", "buenas", "buenos dias", "buen dia", "hey", "saludos", "que tal"))
            return Say("greeting", "¡Hola! Soy el asistente de Insta Expenses. Puedo contarte en qué se gasta, quién gasta más, qué sigue pendiente o si hay algo raro en los datos. ¿Por dónde empezamos?", Examples.Take(4).ToArray());
        if (Has(q, "gracias", "genial", "perfecto", "excelente", "buenisimo"))
            return Say("thanks", "¡Con gusto! Si quieres, puedo profundizar en algo más.", ["¿Qué gastos siguen pendientes?", "¿Existen transacciones potencialmente duplicadas?"]);
        if (Has(q, "ayuda", "que puedes", "que sabes", "que haces", "help", "como funcionas"))
            return Say("help", "Puedo responder sobre totales, promedios, rankings de departamentos/categorías/métodos de pago, gastos pendientes o aprobados, los gastos más grandes y posibles duplicados. Puedes acotar por departamento, categoría, moneda, mes o año; por ejemplo: «gastos de Ventas en USD en marzo». Aquí no hay IA generativa: solo consulto tus datos, así que no invento cifras.", Examples.Take(5).ToArray());
        if (snap.Active.Count == 0)
            return Say("empty", "Todavía no hay datos cargados. Ve a «Importar archivo» y sube un JSON para empezar.", []);

        // --- entities ---
        var e = Entities(q, snap);
        var intent = DetectIntent(q);
        var hasEntities = e.Dep.Length + e.Cat.Length + e.Pay.Length + e.Cur.Length > 0 || e.From is not null;
        if (intent is null && hasEntities && lastIntent is not (null or "help" or "greeting" or "thanks" or "empty"))
        {
            intent = lastIntent; // follow-up ("¿y en EUR?"): keep the intent and inherit whatever scope the new question does not override
            if (lastQuestion is not null)
            {
                var p = Entities(TextNorm.Key(lastQuestion), snap);
                e = new(e.From is not null ? e.From : p.From, e.From is not null ? e.To : p.To, e.From is not null ? e.Period : p.Period,
                    e.Dep.Length > 0 ? e.Dep : p.Dep, e.Cat.Length > 0 ? e.Cat : p.Cat, e.Pay.Length > 0 ? e.Pay : p.Pay, e.Cur.Length > 0 ? e.Cur : p.Cur);
            }
        }
        var (from, to, period, dep, cat, pay, cur) = e;
        var filter = new ExpenseFilter(from, to, dep, cat, cur, pay);
        var scope = new List<string>();
        if (dep.Length > 0) scope.Add("Departamento: " + string.Join(", ", dep));
        if (cat.Length > 0) scope.Add("Categoría: " + string.Join(", ", cat));
        if (pay.Length > 0) scope.Add("Pago: " + string.Join(", ", pay));
        if (cur.Length > 0) scope.Add("Moneda: " + string.Join(", ", cur));
        hasEntities = scope.Count > 0 || from is not null;
        intent ??= hasEntities || Has(q, "gast", "cuanto", "total", "cuantos") ? "totals" : null;

        var rows = filter.Apply(snap.Active).ToList();
        if (intent is null)
            return Say("help", "Mmm, no logré entender esa pregunta. Pregúntame por totales, rankings, pendientes, aprobados, gastos más grandes o duplicados, y si quieres acota por departamento, categoría, moneda o mes. Prueba con alguna de estas:", Examples.Take(5).ToArray());
        if (rows.Count == 0)
            return new("rules", "empty", $"No encontré gastos con ese criterio ({Describe(scope, period)}). ¿Probamos con otro periodo o sin filtros?", [], [], period, "snapshot",
                ["¿Cuál es el total gastado por moneda?", "¿Qué gastos siguen pendientes?"], scope.ToArray());

        var a = Answer(intent, q, rows, filter, period, scope);
        return a with { Scope = scope.ToArray() };
    }

    private sealed record Ents(DateOnly? From, DateOnly? To, string Period, string[] Dep, string[] Cat, string[] Pay, string[] Cur);

    private Ents Entities(string q, DatasetSnapshot snap)
    {
        var (from, to, period) = ResolvePeriod(q, snap);
        return new(from, to, period, Match(q, snap.Active.Select(x => x.Departamento)), Match(q, snap.Active.Select(x => x.Categoria)), Match(q, snap.Active.Select(x => x.MetodoPago)), Currencies(q));
    }

    private static string? DetectIntent(string q)
    {
        if (Has(q, "duplic")) return "duplicates";
        if (Has(q, "pendiente", "pending", "sin aprobar", "por aprobar")) return "pending";
        if (Has(q, "aprobad", "approv")) return "approval";
        if (Has(q, "mayor gasto", "gasto mas grande", "gastos mas grandes", "mas caro", "gastos mas altos", "top gastos", "gasto mas alto", "gasto individual", "mas grande")) return "top";
        if (Has(q, "promedio", "media", "average")) return "average";
        if (Has(q, "cuantos", "cantidad de gastos", "numero de gastos")) return "count";
        if (Has(q, "departamento", "department", "area", "quien gasto", "quien gasta")) return "department";
        if (Has(q, "categor")) return "category";
        if (Has(q, "metodo", "forma de pago", "payment", "como se paga", "como pagan")) return "payment";
        if (Has(q, "total", "cuanto", "gastado", "spent")) return "totals";
        return null;
    }

    private AssistantAnswer Answer(string intent, string q, List<Expense> rows, ExpenseFilter filter, string period, List<string> scope)
    {
        var s = analytics.Summary(filter);
        var where = Describe(scope, period);
        switch (intent)
        {
            case "duplicates":
            {
                var g = InsightsService.FindPossibleDuplicates(rows);
                return g.Count == 0
                    ? Mk("duplicates", $"Buenas noticias: no veo transacciones duplicadas probables ({where}). Busco mismo departamento, categoría, moneda y monto en una ventana de 3 días.", period, "GET /api/insights", ["¿Qué gastos siguen pendientes?", "¿Cuál es el total gastado por moneda?"])
                    : Mk("duplicates", $"Ojo: encontré {g.Count} grupos de posibles duplicados ({where}), con mismo departamento, categoría, moneda y monto en ≤3 días. Revisa los primeros:", period, "GET /api/insights",
                        ["¿Qué gastos siguen pendientes?", "Abre el Data Doctor para ver más hallazgos"], ["IDs", "Departamento", "Categoría", "Monto", "Fechas"],
                        g.Take(15).Select(x => new[] { string.Join(", ", x.Select(y => "#" + y.Id)), x[0].Departamento, x[0].Categoria, $"{N(x[0].Monto)} {x[0].Moneda}", string.Join(", ", x.Select(y => y.Fecha.ToString("yyyy-MM-dd"))) }).ToList());
            }
            case "pending":
            {
                var pend = rows.Where(e => e.Estado == ExpenseStates.Pending).ToList();
                var byCur = string.Join("; ", pend.GroupBy(e => e.Moneda).OrderBy(g => g.Key).Select(g => $"{g.Count()} por {N(g.Sum(e => e.Monto))} {g.Key}"));
                return Mk("pending", pend.Count == 0 ? $"Todo al día: no hay gastos pendientes ({where})." : $"Hay {pend.Count} gastos pendientes de aprobación ({where}): {byCur}. Te dejo los 10 más antiguos, que son los que más urge revisar.",
                    period, "GET /api/expenses?estado=pendiente", ["¿Qué porcentaje está aprobado?", "¿Qué departamento tiene más pendientes?"], ["ID", "Fecha", "Departamento", "Categoría", "Monto"],
                    pend.OrderBy(e => e.Fecha).ThenBy(e => e.Id).Take(10).Select(e => new[] { "#" + e.Id, e.Fecha.ToString("yyyy-MM-dd"), e.Departamento, e.Categoria, $"{N(e.Monto)} {e.Moneda}" }).ToList());
            }
            case "approval":
            {
                var ap = s.Approval;
                return Mk("approval", $"{ap.ApprovedPct:0.0}% de los gastos está aprobado ({ap.Approved}) y {ap.PendingPct:0.0}% sigue pendiente ({ap.Pending}) ({where}).", period, "GET /api/analytics/summary",
                    ["¿Qué gastos siguen pendientes?", "¿Cuál es el total gastado por moneda?"], ["Moneda", "Aprobados", "Pendientes", "Monto aprobado", "Monto pendiente"],
                    ap.ByCurrency.Select(c => new[] { c.Moneda, c.Approved.ToString(), c.Pending.ToString(), N(c.ApprovedTotal), N(c.PendingTotal) }).ToList());
            }
            case "top":
            {
                var top = rows.GroupBy(e => e.Moneda).OrderBy(g => g.Key).SelectMany(g => g.OrderByDescending(e => e.Monto).Take(3)).ToList();
                return Mk("top", $"Estos son los gastos individuales más grandes por moneda ({where}). No comparo entre monedas porque no hay tipo de cambio.", period, "GET /api/expenses?sort=monto",
                    ["¿Qué departamento gastó más?", "¿Existen transacciones potencialmente duplicadas?"], ["ID", "Fecha", "Departamento", "Categoría", "Monto"],
                    top.Select(e => new[] { "#" + e.Id, e.Fecha.ToString("yyyy-MM-dd"), e.Departamento, e.Categoria, $"{N(e.Monto)} {e.Moneda}" }).ToList());
            }
            case "average" when Has(q, "categor", "departamento", "area", "metodo", "pago"):
            {
                var (stats, noun, col) = Has(q, "categor") ? (s.ByCategory, "categoría", "Categoría") : Has(q, "metodo", "pago") ? (s.ByPaymentMethod, "método de pago", "Método de pago") : (s.ByDepartment, "departamento", "Departamento");
                return new("rules", "average", $"Gasto promedio por transacción según {noun} ({where}). Ordenado de mayor a menor promedio, por moneda.", [col, "Moneda", "Promedio", "Gastos", "Total"],
                    stats.GroupBy(x => x.Moneda).OrderBy(g => g.Key).SelectMany(g => g.OrderByDescending(x => x.Average).Take(5)).Select(x => new[] { x.Key, x.Moneda, N(x.Average), x.Count.ToString(), N(x.Total) }).ToList(),
                    period, "GET /api/analytics/summary", ["¿Cuáles son las categorías más costosas?", "¿Qué departamento gastó más?"], []);
            }
            case "average":
                return Mk("average", $"El gasto promedio por transacción ({where}): " + string.Join("; ", s.Totals.Select(t => $"{N(t.Average)} {t.Moneda}")) + ".", period, "GET /api/analytics/summary",
                    ["¿Qué departamento gastó más?", "¿Cuáles son las categorías más costosas?"], ["Moneda", "Promedio", "Máximo", "Gastos"],
                    s.Totals.Select(t => new[] { t.Moneda, N(t.Average), N(t.Max), t.Count.ToString() }).ToList());
            case "count":
                return Mk("count", $"Son {s.TotalRecords} gastos ({where}).", period, "GET /api/analytics/summary", ["¿Cuál es el total gastado por moneda?", "¿Qué porcentaje está aprobado?"], ["Moneda", "Gastos"], s.Totals.Select(t => new[] { t.Moneda, t.Count.ToString() }).ToList());
            case "department": return Ranking("department", "departamento", s.ByDepartment, period, where, "Departamento", ["¿Cuáles son las categorías más costosas?", "¿Qué método de pago se usa más?"]);
            case "category": return Ranking("category", "categoría", s.ByCategory, period, where, "Categoría", ["¿Qué departamento gastó más?", "¿Qué método de pago se usa más?"]);
            case "payment": return Ranking("payment", "método de pago", s.ByPaymentMethod, period, where, "Método de pago", ["¿Cuáles son las categorías más costosas?", "¿Qué porcentaje está aprobado?"]);
            default:
                return Mk("totals", $"Esto es lo que se ha gastado ({where}). Las monedas no se suman entre sí.", period, "GET /api/analytics/summary",
                    ["¿Qué departamento gastó más?", "¿Qué porcentaje está aprobado?", "¿Qué gastos siguen pendientes?"], ["Moneda", "Total", "Gastos", "Promedio"],
                    s.Totals.Select(c => new[] { c.Moneda, N(c.Total), c.Count.ToString(), N(c.Average) }).ToList());
        }
    }

    private static AssistantAnswer Ranking(string intent, string noun, List<DimensionStat> stats, string period, string where, string col, string[] next)
    {
        var tops = stats.GroupBy(x => x.Moneda).OrderBy(g => g.Key).Select(g => g.OrderByDescending(x => x.Total).First()).ToList();
        var lead = Pick(noun + period, "Mira:", "Según los datos,", "Te cuento:");
        var answer = $"{lead} el {noun} con más gasto ({where}) es " + string.Join("; ", tops.Select(t => $"{t.Key} en {t.Moneda} ({N(t.Total)})")) + ". Abajo va el ranking por moneda.";
        return new("rules", intent, answer, [col, "Moneda", "Total", "Gastos", "Promedio"],
            stats.GroupBy(x => x.Moneda).OrderBy(g => g.Key).SelectMany(g => g.OrderByDescending(x => x.Total).Take(5)).Select(x => new[] { x.Key, x.Moneda, N(x.Total), x.Count.ToString(), N(x.Average) }).ToList(),
            period, "GET /api/analytics/summary", next, []);
    }

    private static AssistantAnswer Mk(string intent, string answer, string period, string source, string[] next, string[]? cols = null, List<string[]>? rows = null) =>
        new("rules", intent, answer, cols ?? [], rows ?? [], period, source, next, []);

    private static AssistantAnswer Say(string intent, string text, string[] suggestions) => new("rules", intent, text, [], [], "—", "n/a", suggestions, []);

    private static string Describe(List<string> scope, string period) => scope.Count == 0 ? period : $"{period} · {string.Join(" · ", scope)}";

    private static string[] Match(string q, IEnumerable<string> values) =>
        values.Distinct().Where(v => { var k = TextNorm.Key(v); return k.Length > 2 && q.Contains(k); }).ToArray();

    private static string[] Currencies(string q)
    {
        var l = new List<string>();
        if (Has(q, "usd", "dolar", "dollar")) l.Add("USD");
        if (Has(q, " eur", "euro") || q.EndsWith("eur") || q.StartsWith("eur")) l.Add("EUR");
        if (Has(q, "crc", "colon", "colones")) l.Add("CRC");
        return l.ToArray();
    }

    private (DateOnly? From, DateOnly? To, string Label) ResolvePeriod(string q, DatasetSnapshot snap)
    {
        if (snap.Active.Count == 0) return (null, null, "todo el histórico");
        var latest = snap.Active.Max(e => e.Fecha);
        var now = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        static DateOnly M(DateOnly d) => new(d.Year, d.Month, 1);
        static (DateOnly, DateOnly) Month(DateOnly first) => (first, first.AddMonths(1).AddDays(-1));

        if (Has(q, "este mes", "this month"))
        {
            var m = M(now);
            if (!snap.Active.Any(e => e.Fecha >= m && e.Fecha <= m.AddMonths(1).AddDays(-1))) m = M(latest);
            return (m, m.AddMonths(1).AddDays(-1), m.ToString("yyyy-MM") + (m == M(now) ? " (mes actual)" : " (último mes con datos)"));
        }
        if (Has(q, "mes pasado", "last month")) { var m = M(now).AddMonths(-1); var (f, t) = Month(m); return (f, t, m.ToString("yyyy-MM")); }
        if (Has(q, "este ano", "this year")) return (new DateOnly(latest.Year, 1, 1), new DateOnly(latest.Year, 12, 31), $"año {latest.Year}");

        var days = System.Text.RegularExpressions.Regex.Match(q, @"ultimos (\d{1,3}) dias");
        if (days.Success) { var n = int.Parse(days.Groups[1].Value); return (latest.AddDays(-(n - 1)), latest, $"últimos {n} días (hasta {latest:yyyy-MM-dd})"); }

        var year = System.Text.RegularExpressions.Regex.Match(q, @"\b(20\d{2})\b");
        var mi = Array.FindIndex(Months, mo => q.Contains(mo) || (mo == "septiembre" && q.Contains("setiembre")));
        if (mi >= 0)
        {
            var y = year.Success ? int.Parse(year.Groups[1].Value)
                  : snap.Active.Where(e => e.Fecha.Month == mi + 1).Select(e => e.Fecha.Year).DefaultIfEmpty(latest.Year).Max();
            var (f, t) = Month(new DateOnly(y, mi + 1, 1));
            return (f, t, $"{Months[mi]} {y}");
        }
        if (year.Success) { var y = int.Parse(year.Groups[1].Value); return (new DateOnly(y, 1, 1), new DateOnly(y, 12, 31), $"año {y}"); }
        return (null, null, "todo el histórico");
    }
}
