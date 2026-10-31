using System.Text;
using ExpenseIntelligence.Infrastructure.Storage;

namespace ExpenseIntelligence.Tests;

public class RecoveryReaderTests
{
    private static (List<ExpenseIntelligence.Domain.Expense> Items, ExpenseIntelligence.Application.LoadReport R) Read(string json) =>
        RecoveryReader.Read(Encoding.UTF8.GetBytes(json), "test", Sandbox.Clock);

    [Fact]
    public void Valid_file_loads_everything_without_repairs()
    {
        var (items, r) = Read(Sandbox.Json(Sandbox.Gen(20)));
        Assert.Equal(20, items.Count); Assert.Equal(0, r.Repaired); Assert.Equal(0, r.Quarantined); Assert.False(r.Truncated);
    }

    [Fact]
    public void Key_missing_opening_quote_is_repaired_deterministically_and_reported()
    {
        var broken = Sandbox.Rec(7).Replace("\"id\": 7", "id\": 7");
        var (items, r) = Read(Sandbox.Json([Sandbox.Rec(1), broken, Sandbox.Rec(2)]));
        Assert.Equal(3, items.Count);
        var rep = Assert.Single(r.Repairs);
        Assert.Equal(7, rep.RecordId); Assert.Equal("id\":", rep.Before); Assert.Equal("\"id\":", rep.After); Assert.Equal(15, rep.Line); // record 2 starts on line 14, its id is on line 15
    }

    [Fact]
    public void A_broken_record_does_not_take_down_the_records_after_it()
    {
        // stray quote that cannot be repaired deterministically -> quarantined; neighbours survive
        var bad = Sandbox.Rec(5).Replace("\"moneda\": \"USD\"", "\"moneda\": \"USD");
        var (items, r) = Read(Sandbox.Json([Sandbox.Rec(1), bad, Sandbox.Rec(2), Sandbox.Rec(3)]));
        Assert.Equal([1, 2, 3], items.Select(i => i.Id).ToArray());
        var issue = Assert.Single(r.Issues);
        Assert.Contains("\"id\": 5", issue.Raw);
    }

    [Fact]
    public void Duplicate_ids_keep_the_first_and_quarantine_the_rest()
    {
        var (items, r) = Read(Sandbox.Json([Sandbox.Rec(1, monto: 1), Sandbox.Rec(2), Sandbox.Rec(1, monto: 999)]));
        Assert.Equal(2, items.Count); Assert.Equal(1m, items[0].Monto); Assert.Equal(1, r.DuplicateIds); Assert.Equal(1, r.Quarantined);
    }

    [Fact]
    public void Wrong_types_and_missing_fields_are_quarantined_with_a_reason()
    {
        var noMonto = Sandbox.Rec(2).Replace("\"monto\": 100.5", "\"monto\": \"cien\"");
        var noFecha = Sandbox.Rec(3).Replace("2026-03-01", "2026-13-45");
        var (items, r) = Read(Sandbox.Json([Sandbox.Rec(1), noMonto, noFecha]));
        Assert.Single(items); Assert.Equal(2, r.Quarantined);
        Assert.All(r.Issues, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
    }

    [Fact]
    public void Truncated_file_recovers_complete_records_and_flags_the_tail()
    {
        var json = Sandbox.Json([Sandbox.Rec(1), Sandbox.Rec(2), Sandbox.Rec(3)]);
        var (items, r) = Read(json[..(json.Length - 120)]);
        Assert.Equal(2, items.Count); Assert.True(r.Truncated); Assert.Contains(r.Issues, i => i.Reason.Contains("truncado"));
    }

    [Fact]
    public void Bom_is_tolerated()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Sandbox.Json(Sandbox.Gen(3)))).ToArray();
        Assert.Equal(3, RecoveryReader.Read(bytes, "t", Sandbox.Clock).Items.Count);
    }

    [Fact]
    public void Not_an_array_is_reported_not_thrown()
    {
        var (items, r) = Read("{\"a\":1}");
        Assert.Empty(items); Assert.NotEmpty(r.Issues);
    }

    [Fact]
    public void Real_dataset_if_present_loads_10000_records_with_exactly_one_documented_repair()
    {
        var path = FindOriginal();
        if (path is null) return; // dataset not shipped in this checkout
        var (items, r) = RecoveryReader.Read(File.ReadAllBytes(path), path, Sandbox.Clock);
        Assert.Equal(10_000, items.Count); Assert.Equal(0, r.Quarantined); Assert.Equal(1, r.Repaired);
        Assert.Equal(1407, r.Repairs[0].RecordId); Assert.Equal(3, r.Repairs[0].Line);
        Assert.Equal(10_000, items.Select(i => i.Id).Distinct().Count());
    }

    internal static string? FindOriginal()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "data", "original", "gastos_desordenados.json");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
