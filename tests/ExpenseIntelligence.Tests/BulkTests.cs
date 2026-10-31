using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;
using ExpenseIntelligence.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ExpenseIntelligence.Tests;

public class BulkTests
{
    // Gen(50): even ids are approved, odd ids are pending.
    private static BulkService Bulk(JsonExpenseStore s, int max = 5000) => new(s, new BulkOptions { MaxItems = max });
    private static BulkRequest Ids(JsonExpenseStore s, IEnumerable<int> ids, string? reason = null) =>
        new(s.ActiveDatasetId, ids.Select(i => new BulkItem(i, s.Current.ById.TryGetValue(i, out var e) ? e.Version : 1)).ToList(), null, null, reason);
    private static int[] Odd(int n) => Enumerable.Range(0, n).Select(i => 1 + 2 * i).ToArray();

    [Fact]
    public async Task Approving_several_pending_expenses_changes_only_those_and_uses_one_write()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var b = Bulk(st);
        var writes0 = st.GetStatus().Writes; var rev0 = st.Current.Revision;
        var res = await b.RunAsync("approve", Ids(st, Odd(20)), "ana");

        Assert.Equal(20, res.Applied); Assert.Equal(0, res.Skipped);
        Assert.Equal(writes0 + 1, st.GetStatus().Writes);                 // ONE durable write for 20 records
        Assert.Equal(rev0 + 1, st.Current.Revision);                      // ONE snapshot swap
        foreach (var i in Odd(20)) { var e = st.Current.ById[i]; Assert.True(e.Aprobado); Assert.False(e.Rechazado); Assert.Equal(2, e.Version); }
        Assert.Equal(1, st.Current.ById[41].Version);                     // untouched neighbour
        Assert.False(st.Current.ById[41].Aprobado);
        Assert.All(res.AppliedTotals, t => Assert.Contains(t.Moneda, Catalog.Currencies)); // grouped by currency, never mixed
        Assert.Equal(20, res.AppliedTotals.Sum(t => t.Count));
    }

    [Fact]
    public async Task Rejecting_several_stores_the_reason_and_keeps_aprobado_false()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        var res = await Bulk(st).RunAsync("reject", Ids(st, [1, 3, 5], "  Factura ilegible  "), "ana");
        Assert.Equal(3, res.Applied);
        foreach (var i in new[] { 1, 3, 5 }) { var e = st.Current.ById[i]; Assert.True(e.Rechazado); Assert.False(e.Aprobado); Assert.Equal("Factura ilegible", e.MotivoRechazo); Assert.Equal("rechazado", e.Estado); }
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("  ab ")]
    public async Task Rejecting_requires_a_reason(string? reason)
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Bulk(st).RunAsync("reject", Ids(st, [1], reason), "a"));
        Assert.Contains("motivo", ex.Errors.Keys);
        Assert.False(st.Current.ById[1].Rechazado);
    }

    [Fact]
    public async Task Already_processed_and_deleted_records_are_reported_not_touched()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var b = Bulk(st);
        await b.RunAsync("reject", Ids(st, [3], "no corresponde"), "a");                  // 3 rejected
        await new ExpenseService(st, Sandbox.Clock).DeleteAsync(5, st.Current.ById[5].Version, "a"); // 5 deleted
        var v2 = st.Current.ById[2].Version;                                               // 2 approved
        var writes = st.GetStatus().Writes;

        var res = await b.RunAsync("approve", Ids(st, [1, 2, 3, 5]), "a");
        Assert.Equal(1, res.Applied); Assert.Equal(3, res.Skipped);
        Assert.Equal(1, res.SkippedByReason["already-approved"]); Assert.Equal(1, res.SkippedByReason["already-rejected"]); Assert.Equal(1, res.SkippedByReason["deleted"]);
        Assert.Equal(v2, st.Current.ById[2].Version);                                      // approved one not rewritten
        Assert.True(st.Current.ById[3].Rechazado);                                         // rejected one stays rejected
        Assert.Equal(writes + 1, st.GetStatus().Writes);

        // approved expenses cannot be rejected in bulk either
        var rej = await b.RunAsync("reject", Ids(st, [1, 2], "x y z"), "a");
        Assert.Equal(0, rej.Applied); Assert.Equal(2, rej.Skipped);
        Assert.True(st.Current.ById[1].Aprobado); Assert.False(st.Current.ById[1].Rechazado);
    }

    [Fact]
    public async Task Nothing_eligible_means_no_write_and_no_revision_bump()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        var w = st.GetStatus().Writes; var r = st.Current.Revision;
        var res = await Bulk(st).RunAsync("approve", Ids(st, [2, 4, 6]), "a");
        Assert.Equal(0, res.Applied); Assert.Equal(3, res.Skipped);
        Assert.Equal(w, st.GetStatus().Writes); Assert.Equal(r, st.Current.Revision); Assert.Equal(r, res.Revision);
    }

    [Fact]
    public async Task A_stale_version_aborts_the_whole_batch_and_writes_nothing()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var svc = new ExpenseService(st, Sandbox.Clock);
        var req = Ids(st, Odd(10));
        await svc.UpdateAsync(7, new(Sandbox.Input("Cambiado por otro", 12m), 1), "otro");   // someone edits #7 after the selection
        var w = st.GetStatus().Writes; var rev = st.Current.Revision;

        var ex = await Assert.ThrowsAsync<BulkConflictException>(() => Bulk(st).RunAsync("approve", req, "a"));
        var c = Assert.Single(ex.Conflicts);
        Assert.Equal(7, c.Id); Assert.Equal("version", c.Kind); Assert.Equal(1, c.ExpectedVersion); Assert.Equal(2, c.CurrentVersion);
        Assert.Equal(w, st.GetStatus().Writes); Assert.Equal(rev, st.Current.Revision);
        Assert.All(Odd(10).Where(i => i != 7), i => Assert.False(st.Current.ById[i].Aprobado)); // all-or-nothing
    }

    [Fact]
    public async Task Unknown_ids_abort_and_duplicate_ids_are_counted_once()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var b = Bulk(st);
        var bad = new BulkRequest(st.ActiveDatasetId, [new(1, 1), new(9999, 1)], null, null, null);
        var ex = await Assert.ThrowsAsync<BulkConflictException>(() => b.RunAsync("approve", bad, "a"));
        Assert.Equal("missing", Assert.Single(ex.Conflicts).Kind); Assert.False(st.Current.ById[1].Aprobado);

        var dup = new BulkRequest(st.ActiveDatasetId, [new(1, 1), new(1, 1), new(3, 1), new(1, 1)], null, null, null);
        var res = await b.RunAsync("approve", dup, "a");
        Assert.Equal(2, res.Applied); Assert.Equal(2, res.Duplicates); Assert.Equal(2, st.Current.ById[1].Version); // bumped once, not three times
    }

    [Fact]
    public async Task Empty_oversized_and_ambiguous_requests_are_rejected()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        await Assert.ThrowsAsync<ValidationFailedException>(() => Bulk(st).RunAsync("approve", new(st.ActiveDatasetId, [], null, null, null), "a"));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Bulk(st).RunAsync("approve", Ids(st, Odd(6)) with { Filter = ExpenseFilter.None, ExpectedCount = 50 }, "a"));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Bulk(st, max: 5).RunAsync("approve", Ids(st, Odd(6)), "a"));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Bulk(st).RunAsync("approve", Ids(st, [1]) with { DatasetId = null }, "a"));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Bulk(st).RunAsync("archive", Ids(st, [1]), "a"));
        Assert.False(st.Current.ById[1].Aprobado);
    }

    [Fact]
    public async Task All_filtered_is_resolved_on_the_server_and_checks_the_count()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var b = Bulk(st);
        var filter = new ExpenseFilter(Monedas: ["USD"], Estado: "pendiente");
        var expected = filter.Apply(st.Current.Active).Count(); Assert.True(expected > 3);

        var stale = new BulkRequest(st.ActiveDatasetId, null, filter, expected + 1, null);
        var ex = await Assert.ThrowsAsync<BulkConflictException>(() => b.RunAsync("approve", stale, "a"));
        Assert.Equal("selection-changed", ex.Conflicts[0].Kind);
        Assert.Equal(expected, filter.Apply(st.Current.Active).Count());                 // nothing written

        var preview = b.Preview("approve", new(st.ActiveDatasetId, null, filter, expected, null));
        Assert.Equal(expected, preview.Eligible);
        var res = await b.RunAsync("approve", new(st.ActiveDatasetId, null, filter, expected, null), "a");
        Assert.Equal(expected, res.Applied);
        Assert.Empty(filter.Apply(st.Current.Active));                                    // no pending USD left
        Assert.Equal(25 - expected, st.Current.Active.Count(e => e.Estado == "pendiente" && e.Moneda == "USD") + (25 - expected));
        Assert.True(st.Current.Active.Where(e => e.Moneda != "USD").Any(e => !e.Aprobado));   // other currencies untouched
    }

    [Fact]
    public async Task Preview_reports_totals_per_currency_without_writing()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var w = st.GetStatus().Writes;
        var p = Bulk(st).Preview("approve", Ids(st, Odd(12).Append(2)));
        Assert.Equal(12, p.Eligible); Assert.Equal(1, p.Skipped);
        Assert.Equal(p.Eligible, p.EligibleTotals.Sum(t => t.Count));
        Assert.Equal(p.EligibleTotals.Count, p.EligibleTotals.Select(t => t.Moneda).Distinct().Count());
        Assert.Equal(w, st.GetStatus().Writes);
        Assert.All(Odd(12), i => Assert.False(st.Current.ById[i].Aprobado));
    }

    [Fact]
    public async Task Persists_across_restart_and_keeps_the_original_file_format()
    {
        using var sb = new Sandbox();
        using (var s1 = sb.OpenStore())
        {
            await Bulk(s1).RunAsync("approve", Ids(s1, [1, 3]), "a");
            await Bulk(s1).RunAsync("reject", Ids(s1, [5], "Duplicado"), "a");
        }
        using var s2 = sb.OpenStore();
        Assert.True(s2.Current.ById[1].Aprobado); Assert.True(s2.Current.ById[3].Aprobado);
        Assert.True(s2.Current.ById[5].Rechazado); Assert.Equal("Duplicado", s2.Current.ById[5].MotivoRechazo); Assert.False(s2.Current.ById[5].Aprobado);
        var json = File.ReadAllText(Path.Combine(sb.Work, "expenses.json"));
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("id").GetInt32());
        Assert.False(rows[7].TryGetProperty("rechazado", out _));                       // untouched rows have no new fields
        Assert.True(rows[5].GetProperty("rechazado").GetBoolean()); Assert.Equal("Duplicado", rows[5].GetProperty("motivo_rechazo").GetString());
        Assert.False(rows[5].GetProperty("aprobado").GetBoolean());
        Assert.True((await s2.VerifyIntegrityAsync()).Ok);
    }

    [Fact]
    public async Task Legacy_pending_rows_are_never_converted_to_rejected()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        Assert.All(st.Current.Active.Where(e => !e.Aprobado), e => { Assert.False(e.Rechazado); Assert.Equal("pendiente", e.Estado); });
        var a = ExpenseDiff.Fields(st.Current.ById[1], st.Current.ById[1] with { Aprobado = false, Rechazado = true, MotivoRechazo = "x" });
        Assert.Equal("Rechazado", Assert.Single(a).After);
    }

    [Fact]
    public async Task Audit_has_one_entry_per_record_sharing_one_operation_id_and_history_groups_them()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        var res = await Bulk(st).RunAsync("reject", Ids(st, [1, 3, 5, 7], "Sin soporte"), "ana");
        var entries = st.ReadAudit(10).Where(a => a.Action == "bulk-reject").ToList();
        Assert.Equal(4, entries.Count);
        Assert.All(entries, a => { Assert.Contains(res.OperationId, a.Note); Assert.Contains("Sin soporte", a.Note); Assert.Equal("ana", a.Actor); Assert.Equal(res.Revision, a.Revision); });
        Assert.Equal([1, 3, 5, 7], entries.Select(a => a.ExpenseId!.Value).OrderBy(x => x).ToArray());
        Assert.Contains("\"aprobado\":false", entries[0].Before); Assert.Contains("\"rechazado\":true", entries[0].After);

        var item = Assert.Single(new HistoryService(st).Get(10, "bulk"));
        Assert.Equal(4, item.Count); Assert.Contains("Rechazo masivo", item.Title);
    }

    [Fact]
    public async Task Approved_amount_lock_still_applies_and_approving_by_hand_clears_a_rejection()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var svc = new ExpenseService(st, Sandbox.Clock);
        await Bulk(st).RunAsync("approve", Ids(st, [1]), "a");
        var e = st.Current.ById[1];
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => svc.UpdateAsync(1, new(Sandbox.Input("Monto alterado", e.Monto + 1) with { Aprobado = true, Moneda = e.Moneda }, e.Version), "a"));
        Assert.Contains("monto", ex.Errors.Keys);

        await Bulk(st).RunAsync("reject", Ids(st, [3], "monto mal"), "a");
        var r = st.Current.ById[3];
        var kept = await svc.UpdateAsync(3, new(Sandbox.Input("Sigue igual", r.Monto) with { Moneda = r.Moneda }, r.Version), "a");   // saved still unapproved
        Assert.True(kept.Rechazado); Assert.Equal("monto mal", kept.MotivoRechazo);
        var open = await svc.UpdateAsync(3, new(Sandbox.Input("Ahora aprobado", r.Monto) with { Moneda = r.Moneda, Aprobado = true }, kept.Version), "a");
        Assert.True(open.Aprobado); Assert.False(open.Rechazado); Assert.Null(open.MotivoRechazo);
    }

    [Fact]
    public async Task Bulk_and_individual_writes_running_together_never_lose_an_update()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var svc = new ExpenseService(st, Sandbox.Clock); var b = Bulk(st);
        var req = Ids(st, Odd(25));
        // individual edits on APPROVED (even) records run concurrently with the bulk approval of the odd ones
        var edits = Enumerable.Range(1, 12).Select(i => Task.Run(() => svc.UpdateAsync(i * 2, new(Sandbox.Input($"Edit {i}", st.Current.ById[i * 2].Monto) with { Moneda = st.Current.ById[i * 2].Moneda, Aprobado = true }, 1), "u")));
        var bulk = Task.Run(() => b.RunAsync("approve", req, "a"));
        await Task.WhenAll(edits.Append((Task)bulk));
        Assert.Equal(25, (await bulk).Applied);
        Assert.All(Odd(25), i => Assert.True(st.Current.ById[i].Aprobado));
        Assert.All(Enumerable.Range(1, 12), i => Assert.Equal($"Edit {i}", st.Current.ById[i * 2].Descripcion));
        Assert.True((await st.VerifyIntegrityAsync()).Ok);                                // memory == disk after the race
    }

    [Fact]
    public async Task A_racing_edit_and_bulk_on_the_same_record_has_exactly_one_winner()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var svc = new ExpenseService(st, Sandbox.Clock); var b = Bulk(st);
        var req = Ids(st, [1, 3, 5]);
        var edit = Task.Run(async () => { try { await svc.UpdateAsync(1, new(Sandbox.Input("Carrera", 10m), 1), "u"); return true; } catch (ConcurrencyConflictException) { return false; } });
        var bulk = Task.Run(async () => { try { await b.RunAsync("approve", req, "a"); return true; } catch (BulkConflictException) { return false; } });
        var (editWon, bulkWon) = (await edit, await bulk);
        Assert.True(editWon ^ bulkWon);                                                    // exactly one of them wins, never both, never neither
        if (bulkWon) { Assert.All(new[] { 1, 3, 5 }, i => Assert.True(st.Current.ById[i].Aprobado)); }
        else { Assert.True(editWon); Assert.Equal("Carrera", st.Current.ById[1].Descripcion); Assert.False(st.Current.ById[3].Aprobado); Assert.False(st.Current.ById[5].Aprobado); }
        Assert.True((await st.VerifyIntegrityAsync()).Ok);
    }

    [Fact]
    public async Task A_selection_from_another_file_cannot_modify_the_active_one()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        var seedReq = Ids(st, [1, 3]);                                                        // selected while file "seed" was active
        var imp = new ImportService(st, new DatasetService(st), new DataDoctorService(st, Sandbox.Clock), Options.Create(new StorageOptions { SeedPath = sb.Seed, WorkingDirectory = sb.Work }), Sandbox.Clock, NullLogger<ImportService>.Instance);
        var json = Sandbox.Json(Enumerable.Range(1, 5).Select(i => Sandbox.Rec(i, "EUR", aprobado: false)));  // same ids, different file
        var p = await imp.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), "otro.json");
        await imp.CommitAsync(p.Token, "new", null, "t");
        Assert.NotEqual("seed", st.ActiveDatasetId);

        var ex = await Assert.ThrowsAsync<BulkConflictException>(() => Bulk(st).RunAsync("approve", seedReq, "a"));
        Assert.Equal("dataset", ex.Conflicts[0].Kind);
        Assert.All(st.Current.Active, e => Assert.False(e.Aprobado));
        await st.ActivateAsync("seed", false, "t");
        Assert.False(st.Current.ById[1].Aprobado);                                            // the seed file was not touched either
    }

    [Fact]
    public async Task A_ten_thousand_row_file_approves_five_thousand_with_a_single_write()
    {
        using var sb = new Sandbox(Sandbox.Json(Sandbox.Gen(10_000))); using var st = sb.OpenStore(); var b = Bulk(st);
        var filter = new ExpenseFilter(Estado: "pendiente");
        var expected = filter.Apply(st.Current.Active).Count(); Assert.Equal(5000, expected);
        var w = st.GetStatus().Writes;
        var res = await b.RunAsync("approve", new(st.ActiveDatasetId, null, filter, expected, null), "a");
        Assert.Equal(5000, res.Applied);
        Assert.Equal(w + 1, st.GetStatus().Writes);                  // one rewrite of the file, not 5000
        Assert.True(res.ElapsedMs < 3000, $"bulk of 5000 took {res.ElapsedMs} ms");
        Assert.Equal(10_000, st.Current.Active.Count(e => e.Aprobado));
        Assert.Equal(5000, File.ReadLines(Path.Combine(sb.Work, "audit.jsonl")).Count(l => l.Contains("\"action\":\"bulk-approve\"")));  // one audit line per record
    }

    [Fact]
    public async Task Dashboard_counts_pending_and_rejected_separately()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        await Bulk(st).RunAsync("reject", Ids(st, [1, 3], "no procede"), "a");
        var a = new AnalyticsService(st).Summary(ExpenseFilter.None).Approval;
        Assert.Equal(2, a.Rejected); Assert.Equal(23, a.Pending); Assert.Equal(25, a.Approved);
        Assert.Equal(50, a.Approved + a.Pending + a.Rejected);
        Assert.Equal(2, ExpenseFilter.None with { Estado = "rechazado" } is var f ? f.Apply(st.Current.Active).Count() : 0);
    }
}

public class BulkApiTests : IDisposable
{
    private readonly Sandbox _sb = new(); private ApiFactory? _f;
    private HttpClient Client() => (_f ??= new ApiFactory(_sb)).CreateClient();
    public void Dispose() { _f?.Dispose(); _sb.Dispose(); }

    [Fact]
    public async Task Endpoints_preview_apply_and_report_conflicts_with_409()
    {
        var c = Client();
        var lim = await c.GetFromJsonAsync<JsonElement>("/api/expenses/bulk/limits");
        var ds = lim.GetProperty("datasetId").GetString();
        var body = new { datasetId = ds, items = new[] { new { id = 1, version = 1 }, new { id = 3, version = 1 } } };

        var prev = await c.PostAsJsonAsync("/api/expenses/bulk/approve/preview", body);
        Assert.Equal(HttpStatusCode.OK, prev.StatusCode);
        Assert.Equal(2, (await prev.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eligible").GetInt32());

        var rej = await c.PostAsJsonAsync("/api/expenses/bulk/reject", body);                    // no reason
        Assert.Equal(HttpStatusCode.BadRequest, rej.StatusCode);

        var ok = await c.PostAsJsonAsync("/api/expenses/bulk/reject", new { datasetId = ds, items = body.items, reason = "Sin comprobante" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var e1 = await c.GetFromJsonAsync<JsonElement>("/api/expenses/1");
        Assert.Equal("rechazado", e1.GetProperty("estado").GetString()); Assert.Equal("Sin comprobante", e1.GetProperty("motivoRechazo").GetString());

        var stale = await c.PostAsJsonAsync("/api/expenses/bulk/approve", body);                 // versions are now 2
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conflicts")[0].GetProperty("id").GetInt32() == 1 ? 1 : 0);

        var list = await c.GetFromJsonAsync<JsonElement>("/api/expenses?estado=rechazado&pageSize=10");
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var wrong = await c.PostAsJsonAsync("/api/expenses/bulk/approve", new { datasetId = "otro", items = body.items });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
    }
}
