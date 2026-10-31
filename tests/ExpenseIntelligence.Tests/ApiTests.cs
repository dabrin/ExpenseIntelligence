using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExpenseIntelligence.Tests;

public sealed class ApiFactory(Sandbox sb) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder b)
    {
        b.UseEnvironment("Testing");
        b.UseSetting("Storage:SeedPath", sb.Seed);
        b.UseSetting("Storage:WorkingDirectory", sb.Work);
    }
}

public class ApiTests : IDisposable
{
    private readonly Sandbox _sb = new();
    private ApiFactory? _f;
    private HttpClient Client() => (_f ??= new ApiFactory(_sb)).CreateClient();
    public void Dispose() { _f?.Dispose(); _sb.Dispose(); }

    private static object Body(string desc = "Alta por API", decimal monto = 12.34m, string fecha = "2026-10-01") =>
        new { descripcion = desc, monto, moneda = "usd", categoria = "hardware", departamento = "tecnologia", metodoPago = "paypal", fecha, aprobado = false, codigoFactura = "fac-55555" };

    private static async Task<JsonElement> J(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task Full_CRUD_cycle_through_HTTP()
    {
        var c = Client();
        var created = await c.PostAsJsonAsync("/api/expenses", Body());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var e = (await J(created)).GetProperty("expense");
        var id = e.GetProperty("id").GetInt32();
        Assert.Equal("Hardware", e.GetProperty("categoria").GetString()); // canonicalised

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/expenses/{id}")).StatusCode);

        var upd = await c.PutAsJsonAsync($"/api/expenses/{id}", new { version = 1, data = Body("Editada por API", 99.99m) });
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        Assert.Equal(2, (await J(upd)).GetProperty("expense").GetProperty("version").GetInt32());

        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync($"/api/expenses/{id}", new { version = 1, data = Body("tarde tarde") })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/expenses/{id}?version=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/expenses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/expenses/{id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/expenses/{id}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_payload_returns_400_with_field_errors()
    {
        var r = await Client().PostAsJsonAsync("/api/expenses", Body(monto: -1, fecha: "2099-01-01"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var errors = (await J(r)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("monto", out _)); Assert.True(errors.TryGetProperty("fecha", out _));
    }

    [Fact]
    public async Task Missing_record_is_404_and_unknown_route_is_404()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/expenses/424242")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/nope")).StatusCode);
    }

    [Fact]
    public async Task Filters_pagination_and_summary_work_over_the_wire()
    {
        var c = Client();
        var page = await J(await c.GetAsync("/api/expenses?monedas=USD&monedas=EUR&aprobado=true&pageSize=5&sort=monto&desc=true"));
        Assert.Equal(5, page.GetProperty("items").GetArrayLength());
        var total = page.GetProperty("total").GetInt32();
        var sum = await J(await c.GetAsync("/api/analytics/summary?monedas=USD&monedas=EUR&aprobado=true"));
        Assert.Equal(total, sum.GetProperty("totalRecords").GetInt32());
        Assert.DoesNotContain(sum.GetProperty("totals").EnumerateArray(), t => t.GetProperty("moneda").GetString() == "CRC");
        var monto = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("monto").GetDecimal()).ToList();
        Assert.Equal(monto.OrderByDescending(x => x), monto);
    }

    [Fact]
    public async Task Data_written_through_the_API_is_visible_after_a_full_restart()
    {
        var c = Client();
        var id = (await J(await c.PostAsJsonAsync("/api/expenses", Body("Persistente")))).GetProperty("expense").GetProperty("id").GetInt32();
        _f!.Dispose(); _f = null; // stop the host: releases the lock file

        var again = await J(await Client().GetAsync($"/api/expenses/{id}"));
        Assert.Equal("Persistente", again.GetProperty("descripcion").GetString());
        var diag = await J(await Client().GetAsync("/api/diagnostics"));
        Assert.True(diag.GetProperty("storage").GetProperty("seedUnchanged").GetBoolean());
    }

    [Fact]
    public async Task Doctor_diagnostics_integrity_audit_and_assistant_endpoints_respond()
    {
        var c = Client();
        await c.PostAsJsonAsync("/api/expenses", Body());
        Assert.True((await J(await c.GetAsync("/api/data-doctor"))).GetProperty("qualityScore").GetDouble() > 0);
        Assert.True((await J(await c.PostAsync("/api/diagnostics/integrity-check", null))).GetProperty("ok").GetBoolean());
        Assert.Equal("create", (await J(await c.GetAsync("/api/audit?take=1")))[0].GetProperty("action").GetString());
        var a = await J(await c.PostAsJsonAsync("/api/assistant/query", new { question = "¿Qué gastos siguen pendientes?" }));
        Assert.Equal("rules", a.GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/assistant/query", new { question = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/insights")).StatusCode);
        Assert.True((await J(await c.PostAsync("/api/diagnostics/benchmark", null))).GetProperty("results").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Concurrent_HTTP_creates_all_succeed_with_unique_ids()
    {
        var c = Client();
        var ids = await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
            (await J(await c.PostAsJsonAsync("/api/expenses", Body($"http {i}", 1 + i)))).GetProperty("expense").GetProperty("id").GetInt32()));
        Assert.Equal(40, ids.Distinct().Count());
    }
}
