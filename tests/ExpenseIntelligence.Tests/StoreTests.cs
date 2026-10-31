using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Tests;

public class StoreTests
{
    private static ExpenseService Svc(ExpenseIntelligence.Infrastructure.Storage.JsonExpenseStore s) => new(s, Sandbox.Clock);

    [Fact]
    public async Task First_boot_creates_working_copy_and_never_touches_the_seed()
    {
        using var sb = new Sandbox();
        var before = File.ReadAllBytes(sb.Seed);
        using var store = sb.OpenStore();
        await Svc(store).CreateAsync(Sandbox.Input(), "t");
        Assert.True(File.Exists(Path.Combine(sb.Work, "expenses.json")));
        Assert.Equal(before, File.ReadAllBytes(sb.Seed));
        Assert.True(store.GetStatus().SeedUnchanged);
    }

    [Fact]
    public async Task CRUD_persists_and_survives_a_restart()
    {
        using var sb = new Sandbox();
        int createdId;
        using (var s1 = sb.OpenStore())
        {
            var svc = Svc(s1);
            var c = await svc.CreateAsync(Sandbox.Input("Nuevo", 77.77m), "t");
            createdId = c.Id;
            var u = await svc.UpdateAsync(c.Id, new ExpenseUpdateInput(Sandbox.Input("Editado", 88.88m), c.Version), "t");
            Assert.Equal(2, u.Version);
            await svc.DeleteAsync(1, 1, "t");
        }
        using var s2 = sb.OpenStore(); // "restart"
        Assert.Equal(51, s2.Current.All.Count); // 50 + 1 created; deletion is logical
        Assert.Equal("Editado", s2.Current.ById[createdId].Descripcion);
        Assert.Equal(88.88m, s2.Current.ById[createdId].Monto);
        Assert.True(s2.Current.ById[1].Eliminado);
        Assert.Equal(50, s2.Current.Active.Count);
        Assert.Equal(["create", "update", "delete"], s2.ReadAudit(10).Select(a => a.Action).Reverse().ToArray());
    }

    [Fact]
    public async Task Restore_brings_a_deleted_record_back()
    {
        using var sb = new Sandbox(); using var s = sb.OpenStore();
        var svc = Svc(s);
        await svc.DeleteAsync(3, 1, "t");
        Assert.DoesNotContain(s.Current.Active, e => e.Id == 3);
        await svc.RestoreAsync(3, "t");
        Assert.Contains(s.Current.Active, e => e.Id == 3);
    }

    [Fact]
    public async Task Stale_version_is_rejected_and_nothing_is_written()
    {
        using var sb = new Sandbox(); using var s = sb.OpenStore();
        var svc = Svc(s);
        await svc.UpdateAsync(5, new ExpenseUpdateInput(Sandbox.Input("A A A"), 1), "t");
        var rev = s.Current.Revision;
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => svc.UpdateAsync(5, new ExpenseUpdateInput(Sandbox.Input("B B B"), 1), "t"));
        Assert.Equal(2, ex.ActualVersion);
        Assert.Equal(rev, s.Current.Revision);
        Assert.Equal("A A A", s.Current.ById[5].Descripcion);
    }

    [Fact]
    public async Task Concurrent_updates_of_the_same_record_have_exactly_one_winner_no_lost_updates()
    {
        using var sb = new Sandbox(); using var s = sb.OpenStore();
        var svc = Svc(s);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            try { await svc.UpdateAsync(9, new ExpenseUpdateInput(Sandbox.Input($"escritor {i}"), 1), $"w{i}"); return true; }
            catch (ConcurrencyConflictException) { return false; }
        }));
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(2, s.Current.ById[9].Version);
    }

    [Fact]
    public async Task Concurrent_creates_get_unique_ids_and_all_reach_the_file()
    {
        using var sb = new Sandbox(); using var s = sb.OpenStore();
        var svc = Svc(s);
        var created = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => svc.CreateAsync(Sandbox.Input($"paralelo {i}", 1 + i), "t")));
        Assert.Equal(100, created.Select(c => c.Id).Distinct().Count());
        s.Dispose();
        using var reopened = sb.OpenStore();
        Assert.Equal(150, reopened.Current.All.Count);
        Assert.Equal(150, reopened.Current.All.Select(e => e.Id).Distinct().Count());
        Assert.Equal(150, reopened.GetStatus().LoadReport.Loaded);
    }

    [Fact]
    public async Task Failed_persistence_leaves_memory_untouched_and_surfaces_an_error()
    {
        if (OperatingSystem.IsWindows()) return;
        using var sb = new Sandbox(); using var s = sb.OpenStore();
        var svc = Svc(s);
        var rev = s.Current.Revision; var count = s.Current.All.Count;
        File.SetUnixFileMode(sb.Work, UnixFileMode.UserRead | UnixFileMode.UserExecute); // no new files -> cannot create the .tmp
        try
        {
            await Assert.ThrowsAsync<PersistenceException>(() => svc.CreateAsync(Sandbox.Input(), "t"));
            Assert.Equal(rev, s.Current.Revision); Assert.Equal(count, s.Current.All.Count);
            Assert.Equal(1, s.GetStatus().WriteFailures);
        }
        finally { File.SetUnixFileMode(sb.Work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        await svc.CreateAsync(Sandbox.Input(), "t"); // recovers once the disk is writable again
        Assert.Equal(count + 1, s.Current.All.Count);
    }

    [Fact]
    public async Task Leftover_tmp_from_a_crash_is_discarded_and_committed_data_wins()
    {
        using var sb = new Sandbox();
        using (var s = sb.OpenStore()) await Svc(s).CreateAsync(Sandbox.Input(), "t");
        File.WriteAllText(Path.Combine(sb.Work, "expenses.json.tmp"), "[ {\"id\": 1, ");
        using var s2 = sb.OpenStore();
        Assert.Equal(51, s2.Current.All.Count);
        Assert.False(File.Exists(Path.Combine(sb.Work, "expenses.json.tmp")));
    }

    [Fact]
    public async Task Corrupted_working_file_is_preserved_and_the_backup_is_used()
    {
        using var sb = new Sandbox();
        using (var s = sb.OpenStore()) { var svc = Svc(s); await svc.CreateAsync(Sandbox.Input("a a a"), "t"); await svc.CreateAsync(Sandbox.Input("b b b"), "t"); }
        var file = Path.Combine(sb.Work, "expenses.json");
        var text = File.ReadAllText(file);
        File.WriteAllText(file, text[..(text.Length / 2)]); // truncate: simulates a torn write by something else
        using var s2 = sb.OpenStore();
        Assert.Equal(51, s2.Current.All.Count); // .bak holds the previous generation (50 + first create)
        Assert.NotEmpty(Directory.GetFiles(sb.Work, "expenses.corrupt-*.json"));
    }

    [Fact]
    public void A_second_instance_on_the_same_directory_is_refused()
    {
        using var sb = new Sandbox(); using var first = sb.OpenStore();
        Assert.Throws<InvalidOperationException>(() => sb.OpenStore());
    }

    [Fact]
    public async Task Integrity_check_matches_memory_and_disk()
    {
        using var sb = new Sandbox(); using var s = sb.OpenStore();
        await Svc(s).CreateAsync(Sandbox.Input(), "t");
        var r = await s.VerifyIntegrityAsync();
        Assert.True(r.Ok, string.Join("; ", r.Problems)); Assert.Equal(51, r.RecordsOnDisk);
    }

    [Fact]
    public async Task Readers_always_see_a_consistent_snapshot_while_writers_run()
    {
        using var sb = new Sandbox(); using var s = sb.OpenStore();
        var svc = Svc(s);
        var stop = false; var bad = 0;
        var reader = Task.Run(() => { while (!Volatile.Read(ref stop)) { var snap = s.Current; if (snap.All.Count != snap.All.Select(e => e.Id).Distinct().Count() || snap.Active.Count > snap.All.Count) bad++; } });
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => svc.CreateAsync(Sandbox.Input($"w {i}", 1 + i), "t")));
        Volatile.Write(ref stop, true); await reader;
        Assert.Equal(0, bad);
    }
}

public class ApprovedExpenseLockTests : IDisposable
{
    private readonly Sandbox _sb = new(Sandbox.Json([Sandbox.Rec(1, aprobado: true, monto: 100.5m), Sandbox.Rec(2, aprobado: false, monto: 80m)]));
    private readonly ExpenseIntelligence.Infrastructure.Storage.JsonExpenseStore _store;
    private readonly ExpenseService _svc;

    public ApprovedExpenseLockTests() { _store = _sb.OpenStore(); _svc = new(_store, Sandbox.Clock); }
    public void Dispose() { _store.Dispose(); _sb.Dispose(); }

    private static ExpenseInput Same(decimal monto, string moneda = "USD", bool aprobado = true) => Sandbox.Input("Descripción corregida", monto, moneda) with { Aprobado = aprobado };

    [Fact]
    public async Task Approved_expense_rejects_a_new_amount_or_currency_and_stays_untouched()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => _svc.UpdateAsync(1, new(Same(999m), 1), "t"));
        Assert.Equal([ExpenseService.LockedMessage], ex.Errors["monto"]);
        await Assert.ThrowsAsync<ValidationFailedException>(() => _svc.UpdateAsync(1, new(Same(100.5m, "EUR"), 1), "t"));
        Assert.Equal(100.5m, _store.Current.ById[1].Monto);
        Assert.Equal(1, _store.Current.ById[1].Version);
    }

    [Fact]
    public async Task Changing_the_amount_and_unapproving_in_one_request_cannot_bypass_the_lock() =>
        await Assert.ThrowsAsync<ValidationFailedException>(() => _svc.UpdateAsync(1, new(Same(999m, aprobado: false), 1), "t"));

    [Fact]
    public async Task Approved_expense_still_allows_other_fields_and_keeps_its_amount()
    {
        var u = await _svc.UpdateAsync(1, new(Same(100.5m), 1), "t");
        Assert.Equal("Descripción corregida", u.Descripcion);
        Assert.Equal(100.5m, u.Monto);
        Assert.True(u.Aprobado);
    }

    [Fact]
    public async Task Removing_approval_first_unlocks_the_amount()
    {
        var pending = await _svc.UpdateAsync(1, new(Same(100.5m, aprobado: false), 1), "t");
        var changed = await _svc.UpdateAsync(1, new(Same(999m, aprobado: false), pending.Version), "t");
        Assert.Equal(999m, changed.Monto);
    }

    [Fact]
    public async Task Pending_expense_amount_is_freely_editable()
    {
        var u = await _svc.UpdateAsync(2, new(Same(123.45m, aprobado: false), 1), "t");
        Assert.Equal(123.45m, u.Monto);
    }
}
