using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExpenseIntelligence.Tests;

public class ProtectionTests : IDisposable
{
    private readonly Sandbox _sb = new();
    private WebApplicationFactory<Program>? _f;
    public void Dispose() { _f?.Dispose(); _sb.Dispose(); }

    private HttpClient Client(string? password, bool demo)
    {
        _f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Testing");
            b.UseSetting("Storage:SeedPath", _sb.Seed);
            b.UseSetting("Storage:WorkingDirectory", _sb.Work);
            if (password is not null) b.UseSetting("APP_PASSWORD", password);
            if (demo) b.UseSetting("PUBLIC_DEMO", "true");
        });
        return _f.CreateClient();
    }

    private static AuthenticationHeaderValue Basic(string user, string pass) =>
        new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{pass}")));

    [Fact]
    public async Task Without_password_the_app_stays_open()
        => Assert.Equal(HttpStatusCode.OK, (await Client(null, false).GetAsync("/api/expenses")).StatusCode);

    [Fact]
    public async Task Password_requires_credentials_but_health_stays_open()
    {
        var c = Client("s3cret", false);
        var denied = await c.GetAsync("/api/expenses");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains("Basic", denied.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/health")).StatusCode);

        c.DefaultRequestHeaders.Authorization = Basic("evaluador", "mala");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/expenses")).StatusCode);
        c.DefaultRequestHeaders.Authorization = Basic("evaluador", "s3cret");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/expenses")).StatusCode);
    }

    [Fact]
    public async Task Public_demo_blocks_destructive_dataset_endpoints()
    {
        var c = Client(null, true);
        var reset = await c.PostAsync("/api/datasets/factory-reset", JsonContent.Create(new { confirm = "LIMPIAR" }));
        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync("/api/datasets/x")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/expenses")).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_health_is_minimal()
    {
        var r = await Client(null, false).GetAsync("/api/health");
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("""{"status":"ok"}""", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Audit_actor_is_prefixed_with_the_authenticated_user_and_cannot_be_spoofed()
    {
        var c = Client("s3cret", false);
        c.DefaultRequestHeaders.Authorization = Basic("evaluador", "s3cret");
        c.DefaultRequestHeaders.Add("X-Actor", "director-general");
        var body = JsonContent.Create(new { descripcion = "Prueba actor", monto = 10m, moneda = "usd", categoria = "hardware", departamento = "tecnologia", metodoPago = "paypal", fecha = "2026-10-01", aprobado = false, codigoFactura = "fac-77777" });
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/api/expenses", body)).StatusCode);
        var audit = await c.GetStringAsync("/api/audit");
        Assert.Contains("evaluador/director-general", audit);
        Assert.DoesNotContain("\"director-general\"", audit);
    }
}
