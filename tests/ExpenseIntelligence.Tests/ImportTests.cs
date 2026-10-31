using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;
using ExpenseIntelligence.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ExpenseIntelligence.Tests;

public class ImportTests
{
    private static ImportService Svc(Sandbox sb, JsonExpenseStore st) =>
        new(st, new DatasetService(st), new DataDoctorService(st, Sandbox.Clock), Options.Create(new StorageOptions { SeedPath = sb.Seed, WorkingDirectory = sb.Work }), Sandbox.Clock, NullLogger<ImportService>.Instance);

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    private static string Upload(params string[] records) => Sandbox.Json(records);

    [Fact]
    public async Task Preview_analyses_the_file_but_changes_nothing()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var imp = Svc(sb, st);
        var rev = st.Current.Revision;
        var p = await imp.PreviewAsync(Bytes(Upload(Sandbox.Rec(1000), Sandbox.Rec(1001).Replace("\"id\": 1001", "id\": 1001"), Sandbox.Rec(1002).Replace("2026-03-01", "2026-13-45"))), "nuevo.json");
        Assert.True(p.CanImport);
        Assert.Equal(2, p.Summary.Records); Assert.Equal(1, p.Report.Repaired); Assert.Equal(1, p.Report.Quarantined); Assert.Equal(50, p.Summary.CurrentRecords);
        Assert.Equal(rev, st.Current.Revision); Assert.Equal(50, st.Current.Active.Count); // nothing replaced yet
    }

    [Fact]
    public async Task Commit_replaces_the_dataset_everything_derives_from_the_new_file_and_survives_restart()
    {
        using var sb = new Sandbox();
        var original = File.ReadAllBytes(sb.Seed);
        string saved;
        using (var st = sb.OpenStore())
        {
            var imp = Svc(sb, st);
            var p = await imp.PreviewAsync(Bytes(Upload(Sandbox.Rec(1000, "EUR"), Sandbox.Rec(1001).Replace("\"id\": 1001", "id\": 1001"), Sandbox.Rec(1002, monto: -5m))), "mis gastos (1).json");
            var r = await imp.CommitAsync(p.Token, true, "t");
            saved = r.SavedAs;
            Assert.Equal(3, st.Current.Active.Count); Assert.DoesNotContain(st.Current.Active, e => e.Id <= 50);
            var doc = new DataDoctorService(st, Sandbox.Clock).Analyze();
            Assert.Contains(doc.Anomalies, a => a.Code == "INGEST_REPAIRED"); Assert.Contains(doc.Anomalies, a => a.Code == "NONPOSITIVE_AMOUNT");
            Assert.Equal("import", st.ReadAudit(1)[0].Action);
            Assert.True((await st.VerifyIntegrityAsync()).Ok);
            await Assert.ThrowsAsync<ValidationFailedException>(() => imp.CommitAsync(p.Token, true, "t")); // token is single use
        }
        Assert.Equal(original, File.ReadAllBytes(sb.Seed)); // the previous original is untouched
        Assert.Equal("mis_gastos__1_.json", Path.GetFileName(saved)[15..]);

        using var again = sb.OpenStore(); // restart
        Assert.Equal(3, again.Current.Active.Count);
        var st2 = again.GetStatus();
        Assert.Equal(saved, st2.SeedFile); Assert.True(st2.SeedUnchanged); Assert.Equal(1, st2.LoadReport.Repaired); // ingest findings survive the restart
    }

    [Fact]
    public async Task A_file_with_no_readable_records_cannot_be_imported()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var imp = Svc(sb, st);
        var p = await imp.PreviewAsync(Bytes("esto no es json"), "basura.json");
        Assert.False(p.CanImport); Assert.NotNull(p.Blocker);
        await Assert.ThrowsAsync<ValidationFailedException>(() => imp.CommitAsync(p.Token, true, "t"));
        Assert.Equal(50, st.Current.Active.Count);
    }

    [Theory] [InlineData("../../etc/passwd")] [InlineData("zzz")] [InlineData("")]
    public async Task Invalid_tokens_are_rejected(string token)
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore();
        await Assert.ThrowsAsync<ValidationFailedException>(() => Svc(sb, st).CommitAsync(token, true, "t"));
    }

    [Fact]
    public async Task Upload_over_HTTP_preview_then_commit_then_dashboard_reflects_the_new_file()
    {
        using var sb = new Sandbox(); using var f = new ApiFactory(sb); var c = f.CreateClient();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Upload(Sandbox.Rec(7, "CRC", monto: 1000m), Sandbox.Rec(8, "CRC", monto: 500m))));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "file", "datos.json");
        var prev = await c.PostAsync("/api/import/preview", form);
        Assert.Equal(HttpStatusCode.OK, prev.StatusCode);
        var pj = await prev.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, pj.GetProperty("summary").GetProperty("records").GetInt32());

        var commit = await c.PostAsJsonAsync("/api/import/commit", new { token = pj.GetProperty("token").GetString() });
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        var sum = await c.GetFromJsonAsync<JsonElement>("/api/analytics/summary");
        Assert.Equal(2, sum.GetProperty("totalRecords").GetInt32());
        Assert.Equal(1500m, sum.GetProperty("totals")[0].GetProperty("total").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/import/commit", new { token = "nope" })).StatusCode);
    }

    [Fact]
    public async Task Merge_keeps_previous_data_adds_new_ids_and_never_overwrites_existing_ones()
    {
        using var sb = new Sandbox(); using var st = sb.OpenStore(); var imp = Svc(sb, st);
        var p = await imp.PreviewAsync(Bytes(Upload(Sandbox.Rec(5, monto: 999m), Sandbox.Rec(2000), Sandbox.Rec(2001))), "extra.json");
        Assert.Equal(1, p.Summary.IdCollisions);
        var before5 = st.Current.ById[5];
        var r = await imp.CommitAsync(p.Token, false, "t");
        Assert.Equal("merge", r.Mode); Assert.Equal(2, r.Records); Assert.Equal(1, r.Skipped);
        Assert.Equal(52, st.Current.Active.Count); Assert.Equal(before5, st.Current.ById[5]);
        Assert.True((await st.VerifyIntegrityAsync()).Ok);
        Assert.Equal("import-merge", st.ReadAudit(1)[0].Action);
    }
}
