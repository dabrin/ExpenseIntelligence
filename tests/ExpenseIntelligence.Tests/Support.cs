using System.Text;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ExpenseIntelligence.Tests;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>Isolated sandbox: every test gets its own seed + working directory. Tests never touch data/original or data/working.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ei-tests-" + Guid.NewGuid().ToString("N"));
    public string Seed => Path.Combine(Root, "seed", "gastos.json");
    public string Work => Path.Combine(Root, "work");
    public static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    public Sandbox(string? seedJson = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Seed)!);
        File.WriteAllText(Seed, seedJson ?? Json(Gen(50)));
    }

    public JsonExpenseStore OpenStore(TimeProvider? clock = null) =>
        new(Options.Create(new StorageOptions { SeedPath = Seed, WorkingDirectory = Work }), clock ?? Clock, NullLogger<JsonExpenseStore>.Instance);

    public static string Rec(int id, string moneda = "USD", string cat = "Hardware", string dep = "Tecnología", string pago = "PayPal", string fecha = "2026-03-01",
        bool aprobado = true, decimal monto = 100.5m, string? factura = null) =>
        $$"""
          {
            "id": {{id}},
            "descripcion": "Gasto operativo #{{id}} - {{cat}} ({{dep}})",
            "monto": {{monto.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
            "moneda": "{{moneda}}",
            "categoria": "{{cat}}",
            "departamento": "{{dep}}",
            "metodo_pago": "{{pago}}",
            "fecha": "{{fecha}}",
            "aprobado": {{(aprobado ? "true" : "false")}},
            "codigo_factura": "{{factura ?? $"FAC-{10000 + id}"}}"
          }
          """;

    public static string Json(IEnumerable<string> records) => "[\n" + string.Join(",\n", records) + "\n]";

    public static IEnumerable<string> Gen(int n)
    {
        string[] mon = ["CRC", "USD", "EUR"], cat = ["Hardware", "Servidores", "Viáticos"], dep = ["Tecnología", "Finanzas", "Ventas"], pay = ["PayPal", "Caja Chica"];
        for (var i = 1; i <= n; i++)
            yield return Rec(i, mon[i % 3], cat[i % 3], dep[(i / 3) % 3], pay[i % 2], $"2026-{1 + i % 9:00}-{1 + i % 27:00}", i % 2 == 0, 10 + i * 3.25m);
    }

    public static ExpenseInput Input(string desc = "Gasto de prueba", decimal monto = 50m, string moneda = "USD", string fecha = "2026-10-01", string factura = "FAC-99999") =>
        new(desc, monto, moneda, "Hardware", "Tecnología", "PayPal", DateOnly.Parse(fecha), false, factura);

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
}
