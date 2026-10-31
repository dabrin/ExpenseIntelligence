using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseIntelligence.Api;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;
using ExpenseIntelligence.Infrastructure;
using ExpenseIntelligence.Infrastructure.Assistant;
using ExpenseIntelligence.Infrastructure.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; o.UseUtcTimestamp = true; });

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});
builder.Services.AddExpenseIntelligence(builder.Configuration);
// relative storage paths in appsettings are resolved against the content root, not the process CWD
builder.Services.PostConfigure<StorageOptions>(o =>
{
    o.SeedPath = Path.GetFullPath(o.SeedPath, builder.Environment.ContentRootPath);
    o.WorkingDirectory = Path.GetFullPath(o.WorkingDirectory, builder.Environment.ContentRootPath);
});
builder.Services.AddSingleton<RequestMetrics>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "Insta Expenses API", Version = "v1" }));
builder.Services.AddProblemDetails();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = ImportService.MaxBytes + 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = ImportService.MaxBytes + 1024 * 1024);

var app = builder.Build();
_ = app.Services.GetRequiredService<IExpenseStore>(); // fail fast: load + integrity of the store at startup

app.UseExceptionHandler(err => err.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Errors");
    var (status, title, extra) = ex switch
    {
        ValidationFailedException v => (400, "Validation failed", new Dictionary<string, object?> { ["errors"] = v.Errors }),
        NotFoundException n => (404, n.Message, new Dictionary<string, object?>()),
        DatasetNotFoundException d => (404, d.Message, new Dictionary<string, object?>()),
        DatasetRuleException r => (409, r.Message, new Dictionary<string, object?>()),
        BulkConflictException b => (409, b.Message, new Dictionary<string, object?> { ["conflicts"] = b.Conflicts, ["conflictCount"] = b.Total }),
        ConcurrencyConflictException c => (409, c.Message, new Dictionary<string, object?> { ["currentVersion"] = c.ActualVersion }),
        PersistenceException p => (503, p.Message, new Dictionary<string, object?>()),
        OperationCanceledException => (499, "Request cancelled", new Dictionary<string, object?>()),
        _ => (500, "Unexpected server error", new Dictionary<string, object?>()),
    };
    if (status >= 500) log.LogError(ex, "Unhandled error {TraceId}", ctx.TraceIdentifier);
    else log.LogWarning("Request rejected {Status} {Title}", status, title);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/problem+json";
    extra["traceId"] = ctx.TraceIdentifier;
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object?> { ["status"] = status, ["title"] = title }.Concat(extra).ToDictionary(k => k.Key, k => k.Value),
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}));

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.Use(async (ctx, next) =>
{
    var start = Stopwatch.GetTimestamp();
    try { await next(); }
    finally
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            var route = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? ctx.Request.Path.Value ?? "?";
            var ms = RequestMetrics.Elapsed(start);
            ctx.RequestServices.GetRequiredService<RequestMetrics>().Record($"{ctx.Request.Method} {route}", ms);
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Http")
                .LogInformation("{Method} {Route} -> {Status} in {Ms} ms", ctx.Request.Method, route, ctx.Response.StatusCode, Math.Round(ms, 2));
        }
    }
});

// Optional protection for public deployments. APP_PASSWORD enables HTTP Basic auth (user: APP_USER, default "evaluador");
// PUBLIC_DEMO=true additionally disables Swagger and the destructive dataset endpoints. /api/health stays open for the platform probe.
var appPassword = builder.Configuration["APP_PASSWORD"];
var appUser = builder.Configuration["APP_USER"] is { Length: > 0 } u ? u : "evaluador";
var publicDemo = bool.TryParse(builder.Configuration["PUBLIC_DEMO"], out var pd) && pd;

if (!string.IsNullOrEmpty(appPassword))
{
    var expected = System.Text.Encoding.UTF8.GetBytes($"{appUser}:{appPassword}");
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)) { await next(); return; }
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var given = Convert.FromBase64String(header[6..].Trim());
                if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(given, expected)) { await next(); return; }
            }
            catch (FormatException) { }
        }
        if (header.Length > 0) await Task.Delay(500); // slows down password guessing
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Insta Expenses\", charset=\"UTF-8\"";
    });
}

if (publicDemo)
{
    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path;
        var destructive = path.StartsWithSegments("/api/datasets/factory-reset")
            || (HttpMethods.IsDelete(ctx.Request.Method) && path.StartsWithSegments("/api/datasets"));
        if (destructive)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { title = "Acción deshabilitada en el modo demo público.", status = 403 });
            return;
        }
        await next();
    });
}
else
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
// With authentication on, the audit actor starts with the authenticated user: the X-Actor header (client-controlled) is only a suffix.
string Actor(HttpContext c)
{
    var label = c.Request.Headers.TryGetValue("X-Actor", out var a) && !string.IsNullOrWhiteSpace(a) ? a.ToString()[..Math.Min(a.ToString().Length, 40)] : "api";
    return string.IsNullOrEmpty(appPassword) ? label : $"{appUser}/{label}";
}

// ---------- expenses ----------
api.MapGet("/expenses", ([AsParameters] ExpenseQueryParams p, ExpenseQueryService q) => q.Query(p.ToPageQuery())).WithTags("Expenses");
api.MapGet("/expenses/export", ([AsParameters] ExpenseQueryParams p, string? format, ExpenseIntelligence.Infrastructure.Export.ExportService x) =>
{
    var f = x.Export(p.ToFilter(), format);
    return Results.File(f.Content, f.ContentType, f.FileName);
}).WithTags("Expenses");
api.MapGet("/expenses/bulk/limits", (BulkService b) => new { maxItems = b.MaxItems, datasetId = b.DatasetId }).WithTags("Expenses");
api.MapPost("/expenses/bulk/{operation}/preview", (string operation, BulkRequest body, BulkService b) => b.Preview(operation, body)).WithTags("Expenses");
api.MapPost("/expenses/bulk/{operation}", async (string operation, BulkRequest body, BulkService b, HttpContext ctx, CancellationToken ct) =>
    await b.RunAsync(operation, body, Actor(ctx), ct)).WithTags("Expenses");
api.MapPost("/expenses/{id:int}/state", async (int id, StateChange body, ExpenseService svc, HttpContext ctx, CancellationToken ct) =>
    await svc.ChangeStateAsync(id, body, Actor(ctx), ct)).WithTags("Expenses");
api.MapGet("/expenses/{id:int}", (int id, ExpenseQueryService q) => q.Get(id)).WithTags("Expenses");
api.MapPost("/expenses", async (ExpenseInput input, ExpenseService svc, HttpContext ctx, CancellationToken ct) =>
{
    var created = await svc.CreateAsync(input, Actor(ctx), ct);
    return Results.Created($"/api/expenses/{created.Id}", new { expense = created, warnings = Warnings(svc, created) });
}).WithTags("Expenses");
api.MapPut("/expenses/{id:int}", async (int id, ExpenseUpdateInput input, ExpenseService svc, HttpContext ctx, CancellationToken ct) =>
{
    var updated = await svc.UpdateAsync(id, input, Actor(ctx), ct);
    return Results.Ok(new { expense = updated, warnings = Warnings(svc, updated) });
}).WithTags("Expenses");
api.MapDelete("/expenses/{id:int}", async (int id, int version, ExpenseService svc, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(await svc.DeleteAsync(id, version, Actor(ctx), ct))).WithTags("Expenses");
api.MapPost("/expenses/{id:int}/restore", async (int id, ExpenseService svc, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(await svc.RestoreAsync(id, Actor(ctx), ct))).WithTags("Expenses");
api.MapGet("/facets", (ExpenseQueryService q) => q.GetFacets()).WithTags("Expenses");
static string[] Warnings(ExpenseService svc, Expense e) => svc.InvoiceCodeInUse(e.CodigoFactura, e.Id) ? [$"El código de factura {e.CodigoFactura} ya existe en otro gasto."] : [];

// ---------- analytics ----------
api.MapGet("/analytics/summary", ([AsParameters] ExpenseQueryParams p, string? granularity, AnalyticsService a) => a.Summary(p.ToFilter(), granularity ?? "month")).WithTags("Analytics");
api.MapGet("/analytics/timeline", ([AsParameters] ExpenseQueryParams p, AnalyticsService a) => a.Timeline(p.ToFilter())).WithTags("Analytics");
api.MapGet("/insights", ([AsParameters] ExpenseQueryParams p, InsightsService i) =>
{
    var (rev, list) = i.Generate(p.ToFilter());
    return new { revision = rev, insights = list };
}).WithTags("Analytics");
api.MapPost("/assistant/query", async (AssistantRequest r, LlmAssistant a, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(r.Question) ? Results.BadRequest(new { title = "La pregunta es obligatoria." }) : Results.Ok(await a.AskAsync(r.Question, r.History, r.LastIntent, r.LastQuestion, ct)))
    .WithTags("Assistant");
api.MapGet("/assistant/status", (LlmAssistant a) => new { llm = a.Enabled, model = a.Enabled ? a.Model : null }).WithTags("Assistant");
api.MapGet("/assistant/examples", () => AssistantService.Examples).WithTags("Assistant");

// ---------- data doctor ----------
api.MapGet("/data-doctor", (DataDoctorService d) => d.Analyze()).WithTags("DataDoctor");
api.MapPost("/data-doctor/fix/{code}", async (string code, DataDoctorService d, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(new { code, fixedRecords = await d.FixAsync(code, Actor(ctx), ct) })).WithTags("DataDoctor");

// ---------- import ----------
api.MapPost("/import/preview", async (IFormFile file, ImportService imp, CancellationToken ct) =>
{
    await using var stream = file.OpenReadStream();
    return Results.Ok(await imp.PreviewAsync(stream, file.FileName, ct));
}).DisableAntiforgery().WithTags("Import");
api.MapPost("/import/commit", async (ImportCommit body, ImportService imp, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(await imp.CommitAsync(body.Token ?? "", body.Mode ?? (body.Replace == false ? "merge" : "new"), body.DatasetId, Actor(ctx), ct))).WithTags("Import");

// ---------- uploaded files (datasets) ----------
api.MapGet("/datasets", (DatasetService d) => d.List()).WithTags("Datasets");
api.MapPost("/datasets/{id}/activate", async (string id, DatasetActivate? body, IExpenseStore store, DatasetService d, JsonExpenseStore js, HttpContext ctx, CancellationToken ct) =>
{
    var ds = await js.ActivateAsync(id, body?.Reset ?? false, Actor(ctx), ct);
    return Results.Ok(d.Summarize(ds));
}).WithTags("Datasets");
api.MapDelete("/datasets/{id}", async (string id, JsonExpenseStore js, HttpContext ctx, CancellationToken ct) =>
{
    await js.DeleteDatasetAsync(id, Actor(ctx), ct);
    return Results.NoContent();
}).WithTags("Datasets");
api.MapPost("/datasets/factory-reset", async (FactoryReset body, JsonExpenseStore js, HttpContext ctx, CancellationToken ct) =>
{
    if (!string.Equals(body.Confirm?.Trim(), "LIMPIAR", StringComparison.Ordinal)) throw new DatasetRuleException("Escribe LIMPIAR para confirmar.");
    await js.FactoryResetAsync(Actor(ctx), ct);
    return Results.Ok(new { ok = true });
}).WithTags("Datasets");
api.MapGet("/datasets/compare", (DatasetService d, string a, string b, string? aVariant, string? bVariant, string? key, string? kind, string? q, int? page, int? pageSize) =>
    d.Compare(a, aVariant ?? "actual", b, bVariant ?? "actual", key, kind, q, page ?? 1, pageSize ?? 25)).WithTags("Datasets");

// ---------- engineering diagnostics ----------
api.MapGet("/diagnostics", (IExpenseStore store, RequestMetrics m) =>
{
    var gc = GC.GetGCMemoryInfo();
    return new
    {
        storage = store.GetStatus(),
        timings = m.Snapshot(),
        process = new
        {
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - m.StartedAt).TotalSeconds,
            workingSetMb = Math.Round(Environment.WorkingSet / 1048576.0, 1),
            gcHeapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
            pauseTimePercent = Math.Round(gc.PauseTimePercentage, 2),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        },
    };
}).WithTags("Diagnostics");
api.MapPost("/diagnostics/integrity-check", async (IExpenseStore store, CancellationToken ct) => await store.VerifyIntegrityAsync(ct)).WithTags("Diagnostics");
api.MapGet("/history", (HistoryService h, int? take, string? tipo) => h.Get(take ?? 100, tipo)).WithTags("Diagnostics");
api.MapGet("/audit", (IExpenseStore store, int? take) => store.ReadAudit(take ?? 50)).WithTags("Diagnostics");
// Read-only micro-benchmark over the live snapshot (never writes).
api.MapPost("/diagnostics/benchmark", (ExpenseQueryService q, AnalyticsService a, InsightsService i, DataDoctorService d, IExpenseStore store) =>
{
    var f = new ExpenseFilter(Monedas: ["USD"], Aprobado: true);
    (string Name, Action Run)[] cases =
    [
        ("query page (filtered+sorted)", () => q.Query(new ExpensePageQuery(f, "monto", true, 1, 25))),
        ("analytics summary (no filter)", () => a.Summary(ExpenseFilter.None)),
        ("analytics summary (filtered)", () => a.Summary(f)),
        ("insights", () => i.Generate(ExpenseFilter.None)),
        ("data doctor", () => d.Analyze()),
    ];
    var results = cases.Select(c =>
    {
        c.Run(); // warm-up
        var s = new double[30];
        for (var n = 0; n < s.Length; n++) { var t = Stopwatch.GetTimestamp(); c.Run(); s[n] = RequestMetrics.Elapsed(t); }
        Array.Sort(s);
        return new { name = c.Name, iterations = s.Length, p50Ms = Math.Round(s[s.Length / 2], 2), p95Ms = Math.Round(s[(int)(s.Length * 0.95) - 1], 2), maxMs = Math.Round(s[^1], 2) };
    }).ToList();
    return new { records = store.Current.Active.Count, results };
}).WithTags("Diagnostics");

api.MapGet("/health", () => new { status = "ok" });
api.MapFallback(() => Results.NotFound(new { title = "Unknown API route" }));
app.MapFallbackToFile("index.html");

app.Run();

public sealed record AssistantRequest(string? Question, string? LastIntent, string? LastQuestion, List<ChatTurn>? History = null);
public sealed record ImportCommit(string? Token, bool? Replace, string? Mode = null, string? DatasetId = null);
public sealed record DatasetActivate(bool Reset);
public sealed record FactoryReset(string? Confirm);

public sealed record ExpenseQueryParams(
    DateOnly? From, DateOnly? To, string[]? Departamentos, string[]? Categorias, string[]? Monedas, string[]? MetodosPago,
    bool? Aprobado, string? Q, string? Sort, bool? Desc, int? Page, int? PageSize, bool? Eliminados = null, string? Estado = null, int[]? Ids = null)
{
    public ExpenseFilter ToFilter() => new(From, To, Departamentos, Categorias, Monedas, MetodosPago, Aprobado, Q, Estado, Ids);
    public ExpensePageQuery ToPageQuery() => new(ToFilter(), Sort ?? "fecha", Desc ?? true, Page ?? 1, PageSize ?? 25, Eliminados ?? false);
}

public partial class Program;
