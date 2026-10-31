using System.Text;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;
using ExpenseIntelligence.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ExpenseIntelligence.Tests;

public class DatasetTests
{
    private static ImportService Imp(Sandbox sb, JsonExpenseStore st) =>
        new(st, new DatasetService(st), new DataDoctorService(st, Sandbox.Clock), Options.Create(new StorageOptions { SeedPath = sb.Seed, WorkingDirectory = sb.Work }), Sandbox.Clock, NullLogger<ImportService>.Instance);

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));
    private static string FileB(params int[] ids) => Sandbox.Json(ids.Select(i => Sandbox.Rec(i, "EUR", aprobado: false, monto: 10m + i)));
    private static ExpenseService Svc(JsonExpenseStore s) => new(s, Sandbox.Clock);

    private static async Task<ImportResult> Load(ImportService imp, string name, string json, string mode = "new", string? id = null)
    {
        var p = await imp.PreviewAsync(Bytes(json), name);
        return await imp.CommitAsync(p.Token, mode, id, "t");
    }

    [Fact]
    public async Task Each_file_keeps_its_own_edits_when_you_switch_between_them()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var imp = Imp(sb, st); var svc = Svc(st);
        var a = st.Catalog.ActiveId; Assert.Equal("seed", a);
        await svc.UpdateAsync(3, new(Sandbox.Input("Editado en A", 77m), st.Current.ById[3].Version), "t");   // edit A (record 3 is pending, so its amount is editable)
        await Load(imp, "b.json", FileB(2001, 2002));
        Assert.Equal(2, st.Current.Active.Count);
        var b = st.Catalog.ActiveId; Assert.NotEqual("seed", b);

        await st.ActivateAsync("seed", false, "t");                                        // back to A: its edit is still there
        Assert.Equal(50, st.Current.Active.Count); Assert.Equal("Editado en A", st.Current.ById[3].Descripcion);
        await svc.DeleteAsync(5, st.Current.ById[5].Version, "t");                        // edit A again, then go to B and back
        await st.ActivateAsync(b, false, "t");
        Assert.Equal(2, st.Current.Active.Count);
        await st.ActivateAsync("seed", false, "t");
        Assert.True(st.Current.ById[5].Eliminado); Assert.Equal("Editado en A", st.Current.ById[3].Descripcion);
        Assert.True((await st.VerifyIntegrityAsync()).Ok);
    }

    [Fact]
    public async Task Reset_discards_the_edits_and_returns_the_file_to_its_original()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var svc = Svc(st);
        var original = st.Current.ById[3].Descripcion;
        await svc.UpdateAsync(3, new(Sandbox.Input("Cambiado", 77m), st.Current.ById[3].Version), "t");
        await st.ActivateAsync("seed", reset: true, "t");
        Assert.Equal(original, st.Current.ById[3].Descripcion);
        Assert.Equal("dataset-reset", st.ReadAudit(1)[0].Action);
    }

    [Fact]
    public async Task Uploading_a_file_that_already_exists_is_detected_and_can_go_back_to_it()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var imp = Imp(sb, st);
        await Load(imp, "b.json", FileB(2001));
        var again = await imp.PreviewAsync(Bytes(FileB(2001)), "renombrado.json");           // same bytes, different name
        Assert.NotNull(again.Duplicate); Assert.Equal("b.json", again.Duplicate!.Name); Assert.True(again.Duplicate.IsActive);

        var seedBytes = File.ReadAllBytes(sb.Seed);
        var pSeed = await imp.PreviewAsync(new MemoryStream(seedBytes), "otro-nombre.json");
        Assert.True(pSeed.Duplicate!.IsSeed);
        var before = st.Catalog.Items.Count;
        await imp.CommitAsync(pSeed.Token, "switch", pSeed.Duplicate.Id, "t");              // go back instead of loading a copy
        Assert.Equal("seed", st.Catalog.ActiveId); Assert.Equal(before, st.Catalog.Items.Count);
        Assert.Equal(50, st.Current.Active.Count);
    }

    [Fact]
    public async Task Same_name_with_different_content_is_reported_as_a_previous_version()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var imp = Imp(sb, st);
        await Load(imp, "versiones.json", FileB(2001));
        var p = await imp.PreviewAsync(Bytes(FileB(2001, 2002)), "versiones.json");
        Assert.Null(p.Duplicate); Assert.Single(p.SameName); Assert.Equal("versiones.json", p.SameName[0].Name);
    }

    [Fact]
    public async Task The_same_content_cannot_be_registered_twice()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var imp = Imp(sb, st);
        await Load(imp, "b.json", FileB(2001));
        var p = await imp.PreviewAsync(Bytes(FileB(2001)), "b2.json");
        await Assert.ThrowsAsync<DatasetRuleException>(() => imp.CommitAsync(p.Token, "new", null, "t"));
        Assert.Equal(2, st.Catalog.Items.Count);
    }

    [Fact]
    public async Task Only_inactive_non_seed_files_can_be_deleted()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var imp = Imp(sb, st);
        var r = await Load(imp, "b.json", FileB(2001));
        var b = st.Catalog.ActiveId;
        await Assert.ThrowsAsync<DatasetRuleException>(() => st.DeleteDatasetAsync(b, "t"));       // active
        await Assert.ThrowsAsync<DatasetRuleException>(() => st.DeleteDatasetAsync("seed", "t"));  // seed
        await st.ActivateAsync("seed", false, "t");
        await st.DeleteDatasetAsync(b, "t");
        Assert.Single(st.Catalog.Items); Assert.False(File.Exists(r.SavedAs));
        await Assert.ThrowsAsync<DatasetNotFoundException>(() => st.ActivateAsync(b, false, "t"));
    }

    [Fact]
    public async Task Factory_reset_leaves_the_system_as_if_nothing_had_ever_been_uploaded()
    {
        using var sb = new Sandbox();
        var seedBytes = File.ReadAllBytes(sb.Seed);
        string saved;
        using (var st = sb.OpenStore())
        {
            var imp = Imp(sb, st);
            saved = (await Load(imp, "b.json", FileB(2001, 2002))).SavedAs;
            await Svc(st).CreateAsync(Sandbox.Input("Algo", 5m), "t");
            await st.FactoryResetAsync("t");
            Assert.Equal("seed", st.Catalog.ActiveId); Assert.Single(st.Catalog.Items);
            Assert.Equal(50, st.Current.Active.Count);
            Assert.False(File.Exists(saved)); Assert.False(Directory.Exists(Path.Combine(sb.Work, "imports")));
            Assert.Equal(["factory-reset"], st.ReadAudit(50).Select(a => a.Action).ToArray());
            Assert.True((await st.VerifyIntegrityAsync()).Ok);
        }
        Assert.Equal(seedBytes, File.ReadAllBytes(sb.Seed));
        using var again = sb.OpenStore();                                                        // restart is clean too
        Assert.Equal(50, again.Current.Active.Count); Assert.Equal("seed", again.Catalog.ActiveId); Assert.Single(again.Catalog.Items);
    }

    [Fact]
    public async Task Catalogue_and_active_file_survive_a_restart()
    {
        using var sb = new Sandbox(); string b;
        using (var st = sb.OpenStore()) { await Load(Imp(sb, st), "b.json", FileB(2001)); b = st.Catalog.ActiveId; }
        using var again = sb.OpenStore();
        Assert.Equal(b, again.Catalog.ActiveId); Assert.Equal(2, again.Catalog.Items.Count); Assert.Equal(1, again.Current.Active.Count);
        await again.ActivateAsync("seed", false, "t"); Assert.Equal(50, again.Current.Active.Count);
    }

    [Fact]
    public async Task Compare_two_files_and_a_file_against_its_own_original()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var imp = Imp(sb, st); var ds = new DatasetService(st);
        await st.ActivateAsync("seed", false, "t");
        await Svc(st).UpdateAsync(3, new(Sandbox.Input("Cambiado", 999m), 1), "t");  // A: 1 edit
        await Svc(st).DeleteAsync(5, 1, "t");                                                                // A: 1 deletion
        await Load(imp, "b.json", Sandbox.Json([Sandbox.Rec(3, "EUR", aprobado: false, monto: 5m), Sandbox.Rec(900)]));
        var b = st.Catalog.ActiveId;

        var self = ds.Compare("seed", "original", "seed", "actual", "id", null, null, 1, 25);      // what did I change in A?
        Assert.Equal(1, self.Counts.Changed); Assert.Equal(1, self.Counts.Removed); Assert.Equal(0, self.Counts.Added);
        Assert.Equal(49, self.B.Records); Assert.Equal(50, self.A.Records);
        Assert.Contains("Monto", self.Counts.FieldChanges.Keys);

        var ab = ds.Compare("seed", "actual", b, "actual", "id", "changed", null, 1, 25);          // A vs B
        Assert.Equal(1, ab.Counts.Changed);                                                         // id 3 exists in both and differs
        Assert.Equal(1, ab.Counts.Added); Assert.Equal(48, ab.Counts.Removed);                      // 900 only in B; the rest only in A
        Assert.Single(ab.Rows.Items); Assert.Contains(ab.Rows.Items[0].Changes, c => c.Field == "moneda");
        Assert.All(ab.Totals, t => Assert.Contains(t.Moneda, Catalog.Currencies));                  // currencies stay separate
        Assert.Throws<DatasetRuleException>(() => ds.Compare("seed", "actual", "seed", "actual", "id", null, null, 1, 25));
    }

    [Fact]
    public void Comparer_can_pair_records_by_invoice_code_instead_of_id()
    {
        Expense E(int id, string fac, decimal m) => new() { Id = id, Descripcion = "d", Monto = m, Moneda = "USD", Categoria = "c", Departamento = "x", MetodoPago = "p", Fecha = new DateOnly(2026, 1, 1), Aprobado = true, CodigoFactura = fac };
        var (rows, c) = DatasetComparer.Diff([E(1, "FAC-1", 10), E(2, "FAC-2", 20)], [E(100, "fac-1", 11), E(101, "FAC-3", 5)], "factura");
        Assert.Equal(1, c.Changed); Assert.Equal(1, c.Added); Assert.Equal(1, c.Removed); Assert.Equal(0, c.Same);
        Assert.Equal("changed", rows[0].Kind);
    }

    [Fact]
    public async Task Catalogue_is_rebuilt_for_data_created_before_this_feature_existed()
    {
        using var sb = new Sandbox(); string b;
        using (var st = sb.OpenStore()) { await Load(Imp(sb, st), "viejo.json", FileB(2001, 2002)); b = st.Catalog.ActiveId; }
        Directory.Delete(Path.Combine(sb.Work, "datasets"), true);                          // the layout of an older version: source.json only
        using var again = sb.OpenStore();
        Assert.Equal(2, again.Catalog.Items.Count); Assert.Equal("viejo.json", again.Catalog.Items.Single(d => d.Id == again.Catalog.ActiveId).Name);
        Assert.Equal(2, again.Current.Active.Count);                                         // live data untouched by the migration
        await again.ActivateAsync("seed", false, "t"); Assert.Equal(50, again.Current.Active.Count);
    }
}

public class HistoryTests
{
    [Fact]
    public async Task History_explains_edits_field_by_field_and_offers_restore_only_while_still_deleted()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        var svc = new ExpenseService(st, Sandbox.Clock); var hist = new HistoryService(st);
        await svc.UpdateAsync(3, new(Sandbox.Input("Nueva descripción", 321m), 1), "ana");
        await svc.DeleteAsync(5, 1, "ana");

        var all = hist.Get(50, "all");
        var edit = all.Single(i => i.Action == "update");
        Assert.Equal("ana", edit.Actor); Assert.Contains(edit.Changes, c => c.Field == "monto" && c.After == "321");
        Assert.Contains(edit.Changes, c => c.Field == "descripcion" && c.After == "Nueva descripción");
        Assert.True(all.Single(i => i.Action == "delete").CanRestore);
        Assert.Single(hist.Get(50, "deletes")); Assert.Single(hist.Get(50, "edits"));

        await svc.RestoreAsync(5, "ana");
        Assert.False(hist.Get(50, "deletes").Single().CanRestore);                          // already restored: no second restore button
        Assert.Single(hist.Get(50, "restores"));
    }

    [Fact]
    public async Task Trash_lists_deleted_expenses_and_counts_them()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        var svc = new ExpenseService(st, Sandbox.Clock); var q = new ExpenseQueryService(st);
        await svc.DeleteAsync(5, 1, "t"); await svc.DeleteAsync(7, 1, "t");
        var trash = q.Query(new ExpensePageQuery(ExpenseFilter.None, "id", false, 1, 25, Deleted: true));
        Assert.Equal([5, 7], trash.Items.Select(e => e.Id).ToArray());
        Assert.Equal(48, q.Query(new ExpensePageQuery(ExpenseFilter.None)).Total);
        Assert.Equal(2, q.GetFacets().DeletedCount);
    }
}
