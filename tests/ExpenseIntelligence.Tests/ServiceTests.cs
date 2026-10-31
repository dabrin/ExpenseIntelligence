using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Tests;

public class ValidationTests
{
    private static (Sandbox, ExpenseService) Make(out ExpenseIntelligence.Infrastructure.Storage.JsonExpenseStore store)
    { var sb = new Sandbox(); store = sb.OpenStore(); return (sb, new ExpenseService(store, Sandbox.Clock)); }

    private static IReadOnlyDictionary<string, string[]> Errors(ExpenseService svc, ExpenseInput i) =>
        Assert.Throws<ValidationFailedException>(() => svc.Validate(i)).Errors;

    [Theory]
    [InlineData(0)] [InlineData(-5)] [InlineData(1_000_000_001)] [InlineData(10.123)]
    public void Invalid_amounts_are_rejected(double monto)
    {
        var (sb, svc) = Make(out var st); using (sb) using (st)
            Assert.Contains("monto", Errors(svc, Sandbox.Input(monto: (decimal)monto)).Keys);
    }

    [Fact]
    public void Required_fields_future_dates_and_unknown_currency_are_rejected()
    {
        var (sb, svc) = Make(out var st); using (sb) using (st)
        {
            var e = Errors(svc, new ExpenseInput("", null, "XXX", " ", null, "", DateOnly.Parse("2030-01-01"), false, ""));
            foreach (var k in new[] { "descripcion", "monto", "moneda", "categoria", "departamento", "metodoPago", "fecha", "codigoFactura" }) Assert.Contains(k, e.Keys);
        }
    }

    [Fact]
    public void Today_is_allowed_tomorrow_is_not()
    {
        var (sb, svc) = Make(out var st); using (sb) using (st)
        {
            svc.Validate(Sandbox.Input(fecha: "2026-10-30"));
            Assert.Contains("fecha", Errors(svc, Sandbox.Input(fecha: "2026-10-31")).Keys);
        }
    }

    [Fact]
    public void Text_is_trimmed_and_canonicalised_to_the_existing_catalogue_spelling()
    {
        var (sb, svc) = Make(out var st); using (sb) using (st)
        {
            var v = svc.Validate(new ExpenseInput("  hola mundo ", 10.456m == 10.456m ? 10.45m : 0, "usd", "  hardware", "TECNOLOGIA ", "paypal", DateOnly.Parse("2026-01-01"), false, " fac-1234 "));
            Assert.Equal("hola mundo", v.Descripcion); Assert.Equal("USD", v.Moneda); Assert.Equal("Hardware", v.Categoria);
            Assert.Equal("Tecnología", v.Departamento); Assert.Equal("PayPal", v.MetodoPago); Assert.Equal("FAC-1234", v.CodigoFactura);
        }
    }

    [Fact]
    public async Task Update_of_a_missing_or_deleted_record_is_not_found()
    {
        var (sb, svc) = Make(out var st); using (sb) using (st)
        {
            await Assert.ThrowsAsync<NotFoundException>(() => svc.UpdateAsync(9999, new ExpenseUpdateInput(Sandbox.Input(), 1), "t"));
            await svc.DeleteAsync(2, 1, "t");
            await Assert.ThrowsAsync<NotFoundException>(() => svc.UpdateAsync(2, new ExpenseUpdateInput(Sandbox.Input(), 2), "t"));
        }
    }
}

public class QueryAndAnalyticsTests : IDisposable
{
    private readonly Sandbox _sb;
    private readonly ExpenseIntelligence.Infrastructure.Storage.JsonExpenseStore _store;
    private readonly ExpenseQueryService _q;
    private readonly AnalyticsService _a;

    public QueryAndAnalyticsTests()
    {
        // 6 hand-made records: easy to verify by hand.
        _sb = new Sandbox(Sandbox.Json([
            Sandbox.Rec(1, "USD", "Hardware", "Tecnología", "PayPal", "2026-01-10", true, 100m),
            Sandbox.Rec(2, "USD", "Hardware", "Ventas", "PayPal", "2026-02-10", false, 300m),
            Sandbox.Rec(3, "EUR", "Viáticos", "Ventas", "Caja Chica", "2026-02-15", true, 50m),
            Sandbox.Rec(4, "CRC", "Servidores", "Finanzas", "Caja Chica", "2026-03-01", false, 5000m),
            Sandbox.Rec(5, "USD", "Servidores", "Tecnología", "Tarjeta", "2026-03-20", true, 200m, "FAC-77777"),
            Sandbox.Rec(6, "USD", "Servidores", "Tecnología", "Tarjeta", "2026-03-21", true, 200m, "FAC-77777"),
        ]));
        _store = _sb.OpenStore(); _q = new ExpenseQueryService(_store); _a = new AnalyticsService(_store);
    }
    public void Dispose() { _store.Dispose(); _sb.Dispose(); }

    private PagedResult<Expense> Q(ExpenseFilter f, string sort = "id", bool desc = false, int page = 1, int size = 25) => _q.Query(new ExpensePageQuery(f, sort, desc, page, size));
    private static int[] Ids(PagedResult<Expense> r) => r.Items.Select(i => i.Id).ToArray();

    [Fact] public void No_filter_returns_everything() => Assert.Equal(6, Q(ExpenseFilter.None).Total);

    [Fact] public void Date_range_is_inclusive() => Assert.Equal([2, 3], Ids(Q(new(From: DateOnly.Parse("2026-02-10"), To: DateOnly.Parse("2026-02-15")))));
    [Fact] public void Multiple_departments_are_ORed() => Assert.Equal([1, 2, 3, 5, 6], Ids(Q(new(Departamentos: ["Tecnología", "Ventas"]))));
    [Fact] public void Multiple_categories_are_ORed() => Assert.Equal([3, 4, 5, 6], Ids(Q(new(Categorias: ["Viáticos", "Servidores"]))));
    [Fact] public void Currency_filter() => Assert.Equal([4], Ids(Q(new(Monedas: ["CRC"]))));
    [Fact] public void Payment_method_filter() => Assert.Equal([3, 4], Ids(Q(new(MetodosPago: ["Caja Chica"]))));
    [Fact] public void Approval_filter() { Assert.Equal([2, 4], Ids(Q(new(Aprobado: false)))); Assert.Equal(4, Q(new(Aprobado: true)).Total); }
    [Fact] public void Search_matches_description_invoice_and_exact_id() { Assert.Equal([5, 6], Ids(Q(new(Search: "fac-77777")))); Assert.Equal([4], Ids(Q(new(Search: "4")))); }

    [Fact]
    public void Timeline_has_one_frame_per_month_and_never_mixes_currencies()
    {
        var t = _a.Timeline(ExpenseFilter.None);
        Assert.Equal(["2026-01", "2026-02", "2026-03"], t.Frames.Select(f => f.Period).ToArray());
        Assert.Equal(["CRC", "EUR", "USD"], t.Currencies);
        var feb = t.Frames[1];
        Assert.Equal(300m, feb.Totals.Single(x => x.Moneda == "USD").Total);
        Assert.Equal(50m, feb.Totals.Single(x => x.Moneda == "EUR").Total);
        Assert.Equal(400m, t.Frames[2].Totals.Single(x => x.Moneda == "USD").Total);
        Assert.Equal(1, t.Frames[2].Totals.Single(x => x.Moneda == "CRC").Count);
    }

    [Fact]
    public void Timeline_fills_empty_months_and_respects_filters()
    {
        var t = _a.Timeline(new(Departamentos: ["Tecnología"]));
        Assert.Equal(["2026-01", "2026-02", "2026-03"], t.Frames.Select(f => f.Period).ToArray());
        Assert.Empty(t.Frames[1].Totals);
        Assert.Empty(t.Frames[1].ByDepartment);
        Assert.Equal(["Tecnología"], t.Departments);
    }

    [Fact] public void Timeline_of_an_empty_selection_has_no_frames() => Assert.Empty(_a.Timeline(new(Monedas: ["GBP"])).Frames);

    [Fact]
    public void All_filters_combine_with_AND()
    {
        var f = new ExpenseFilter(DateOnly.Parse("2026-01-01"), DateOnly.Parse("2026-03-31"), ["Tecnología"], ["Servidores", "Hardware"], ["USD"], ["Tarjeta"], true, "FAC-77777");
        Assert.Equal([5, 6], Ids(Q(f)));
        Assert.Empty(Q(f with { Aprobado = false }).Items);
    }

    [Fact]
    public void Pagination_sort_and_clamping()
    {
        var p1 = Q(ExpenseFilter.None, "monto", true, 1, 4); var p2 = Q(ExpenseFilter.None, "monto", true, 2, 4);
        Assert.Equal([4, 2, 5, 6], Ids(p1)); Assert.Equal([1, 3], Ids(p2)); Assert.Equal(2, p1.TotalPages); Assert.Equal(6, p1.Total);
        Assert.Equal(2, Q(ExpenseFilter.None, page: 99, size: 4).Page); // page beyond the end is clamped
        Assert.Equal(6, Q(ExpenseFilter.None, size: 100000).PageSize == ExpenseQueryService.MaxPageSize ? 6 : 0);
    }

    [Fact]
    public void Totals_are_per_currency_and_never_mixed()
    {
        var t = _a.Summary(ExpenseFilter.None).Totals.ToDictionary(x => x.Moneda);
        Assert.Equal(3, t.Count);
        Assert.Equal(800m, t["USD"].Total); Assert.Equal(4, t["USD"].Count); Assert.Equal(200m, t["USD"].Average);
        Assert.Equal(50m, t["EUR"].Total); Assert.Equal(5000m, t["CRC"].Total);
        Assert.Equal(300m, t["USD"].PendingTotal); Assert.Equal(500m, t["USD"].ApprovedTotal);
    }

    [Fact]
    public void Approval_percentages_and_filtered_metrics_follow_the_filter()
    {
        var all = _a.Summary(ExpenseFilter.None).Approval;
        Assert.Equal(4, all.Approved); Assert.Equal(2, all.Pending); Assert.Equal(66.67, all.ApprovedPct);
        var s = _a.Summary(new ExpenseFilter(Monedas: ["USD"], Departamentos: ["Tecnología"]));
        Assert.Equal(3, s.TotalRecords); Assert.Single(s.Totals); Assert.Equal(500m, s.Totals[0].Total);
        Assert.Equal(100.0, s.Approval.ApprovedPct);
    }

    [Fact]
    public void Department_average_top_categories_payment_methods_and_series()
    {
        var s = _a.Summary(ExpenseFilter.None);
        var tec = s.ByDepartment.Single(d => d.Key == "Tecnología" && d.Moneda == "USD");
        Assert.Equal(3, tec.Count); Assert.Equal(500m, tec.Total); Assert.Equal(166.67m, tec.Average);
        Assert.Equal(400m, s.ByCategory.First(c => c.Moneda == "USD").Total); // Hardware and Servidores tie at 400; the first row is a top category
        Assert.Equal(2, s.ByPaymentMethod.Count(p => p.Moneda == "USD"));
        var usd = s.Series.Where(p => p.Moneda == "USD").ToList();
        Assert.Equal(["2026-01", "2026-02", "2026-03"], usd.Select(p => p.Period).ToArray());
        Assert.Equal([100m, 300m, 400m], usd.Select(p => p.Total).ToArray());
    }

    [Fact]
    public void Weekly_granularity_buckets_by_monday()
    {
        var s = _a.Summary(new ExpenseFilter(Monedas: ["USD"], From: DateOnly.Parse("2026-03-01")), "week");
        Assert.Equal(["2026-03-16"], s.Series.Select(p => p.Period).ToArray()); // 20th (Fri) and 21st (Sat) -> week of Mon 16th
    }

    [Fact]
    public void Doctor_detects_duplicate_invoices_and_possible_duplicate_transactions()
    {
        var d = new DataDoctorService(_store, Sandbox.Clock).Analyze();
        var inv = d.Anomalies.Single(a => a.Code == "DUP_INVOICE");
        Assert.Equal([5, 6], inv.RecordIds.Order().ToArray());
        Assert.Contains(d.Anomalies, a => a.Code == "POSSIBLE_DUP_TX");
    }

    [Fact]
    public async Task Doctor_fixes_semantic_duplicates_and_whitespace_safely_and_audits_them()
    {
        var sb = new Sandbox(Sandbox.Json([
            Sandbox.Rec(1, dep: "Tecnología"), Sandbox.Rec(2, dep: "Tecnología"), Sandbox.Rec(3, dep: "Tecnologia"), Sandbox.Rec(4, cat: "Hardware  "), Sandbox.Rec(5)]));
        using var _ = sb; using var st = sb.OpenStore();
        var doc = new DataDoctorService(st, Sandbox.Clock);
        Assert.Contains(doc.Analyze().Anomalies, a => a.Code == "SEMANTIC_DUP_DEPARTAMENTO" && a.AutoFixable);
        Assert.Equal(1, await doc.FixAsync("SEMANTIC_DUP_DEPARTAMENTO", "t"));
        Assert.Equal("Tecnología", st.Current.ById[3].Departamento);
        Assert.Equal(1, await doc.FixAsync("WHITESPACE", "t"));
        Assert.Equal("Hardware", st.Current.ById[4].Categoria);
        Assert.DoesNotContain(doc.Analyze().Anomalies, a => a.Code is "WHITESPACE" or "SEMANTIC_DUP_DEPARTAMENTO");
        Assert.Equal(2, st.ReadAudit(10).Count(a => a.Action.StartsWith("doctor-fix")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => doc.FixAsync("DUP_INVOICE", "t")); // not safely fixable
    }

    [Fact]
    public void Insights_are_deterministic_and_assistant_answers_from_real_numbers()
    {
        var (_, ins) = new InsightsService(_store).Generate(ExpenseFilter.None);
        Assert.Contains(ins, i => i.Id == "duplicates"); Assert.Contains(ins, i => i.Type == "concentration");
        var assistant = new AssistantService(_store, _a, Sandbox.Clock);
        var a = assistant.Ask("¿Qué gastos siguen pendientes?");
        Assert.Equal("pending", a.Intent); Assert.Contains("2 gastos pendientes", a.Answer);
        Assert.Equal("totals", assistant.Ask("¿Cuál es el total gastado?").Intent);
        Assert.Equal("help", assistant.Ask("blah blah").Intent);
        Assert.Equal("greeting", assistant.Ask("hola").Intent);
        // entities + follow-up: "gastos de Ventas en EUR" is scoped; "¿y en USD?" reuses the previous intent
        var scoped = assistant.Ask("¿Cuánto gastó Ventas en EUR?");
        Assert.Equal("totals", scoped.Intent); Assert.Contains("Departamento: Ventas", scoped.Scope); Assert.Contains("Moneda: EUR", scoped.Scope);
        Assert.Equal("50.00", scoped.Rows.Single()[1].Replace(",", ""));
        var follow = assistant.Ask("¿y en USD?", "totals", "¿Cuánto gastó Ventas en EUR?");
        Assert.Equal("totals", follow.Intent); Assert.Contains("Moneda: USD", follow.Scope); Assert.Contains("Departamento: Ventas", follow.Scope); Assert.DoesNotContain("Moneda: EUR", follow.Scope);
        Assert.Equal("approval", assistant.Ask("aprobados en febrero 2026").Intent);
        Assert.Equal("febrero 2026", assistant.Ask("total en febrero").Period);
    }
}
