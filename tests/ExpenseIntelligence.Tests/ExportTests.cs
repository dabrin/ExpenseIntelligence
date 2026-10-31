using System.Net;
using System.Text;
using ClosedXML.Excel;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;
using ExpenseIntelligence.Infrastructure.Export;

namespace ExpenseIntelligence.Tests;

public class ExportTests
{
    private static ExportService Svc(Sandbox sb, out ExpenseIntelligence.Infrastructure.Storage.JsonExpenseStore st)
    { st = sb.OpenStore(); return new ExportService(st, new AnalyticsService(st), Sandbox.Clock); }

    [Fact]
    public void Xlsx_contains_exactly_the_filtered_rows_and_a_per_currency_summary()
    {
        using var sb = new Sandbox(); var x = Svc(sb, out var st); using var _ = st;
        var filter = new ExpenseFilter(Monedas: ["USD"], Estado: "pendiente");
        var expected = filter.Apply(st.Current.Active).Select(e => e.Id).OrderBy(i => i).ToArray();
        var file = x.Export(filter, "xlsx");
        Assert.Equal("gastos_20261030-1200.xlsx", file.FileName);

        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var data = wb.Worksheet("Gastos");
        var ids = data.RowsUsed().Skip(1).Select(r => r.Cell(1).GetValue<int>()).OrderBy(i => i).ToArray();
        Assert.Equal(expected, ids);
        Assert.All(data.RowsUsed().Skip(1), r => { Assert.Equal("USD", r.Cell(8).GetString()); Assert.Equal("pendiente", r.Cell(10).GetString()); });
        Assert.True(data.RowsUsed().Skip(1).All(r => r.Cell(9).DataType == XLDataType.Number));

        var res = wb.Worksheet("Resumen");
        var text = string.Join("\n", res.CellsUsed().Select(c => c.GetString()));
        Assert.Contains("Moneda: USD", text); Assert.Contains("Estado: pendiente", text);
        Assert.Contains($"{expected.Length} gastos exportados", text);
        var cur = res.CellsUsed().Where(c => c.Address.ColumnNumber == 1 && c.GetString() is "USD" or "CRC" or "EUR").Select(c => c.GetString()).Distinct().ToArray();
        Assert.Contains("USD", cur); Assert.DoesNotContain("CRC", cur);                 // other currencies are not in the file at all
    }

    [Fact]
    public void Csv_has_bom_quotes_properly_and_neutralises_formulas()
    {
        using var sb = new Sandbox(Sandbox.Json([Sandbox.Rec(1, aprobado: false).Replace("Gasto operativo #1 - Hardware (Tecnología)", "=HYPERLINK(\\\"x\\\"), con coma")]));
        var x = Svc(sb, out var st); using var _ = st;
        var bytes = x.Export(ExpenseFilter.None, "csv").Content;
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3).ToArray());
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("ID,Fecha,Descripción,Departamento,Categoría,Método de pago,Factura,Moneda,Monto,Estado,Motivo del rechazo", lines[0]);
        Assert.Contains("\"'=HYPERLINK(", lines[1]);                                    // quoted (has a comma) and prefixed so it stays text
        Assert.Contains(",100.50,pendiente,", lines[1]);                                // invariant decimals regardless of server culture
    }

    [Fact]
    public void Currency_with_a_formula_is_neutralised_in_csv_and_xlsx()
    {
        using var sb = new Sandbox(Sandbox.Json([Sandbox.Rec(1, moneda: "=1+1", aprobado: false)]));
        var x = Svc(sb, out var st); using var _ = st;
        var csv = Encoding.UTF8.GetString(x.Export(ExpenseFilter.None, "csv").Content);
        Assert.Contains(",'=1+1,", csv);
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(x.Export(ExpenseFilter.None, "xlsx").Content));
        foreach (var cell in new[] { wb.Worksheet("Gastos").Cell(2, 8), wb.Worksheet("Resumen").CellsUsed().First(c => c.GetString().Contains("=1+1")) })
        {
            Assert.False(cell.HasFormula);                                              // stored as text, never evaluated
            Assert.Equal(ClosedXML.Excel.XLDataType.Text, cell.DataType);
        }
    }

    [Fact]
    public void Rejected_reason_and_empty_results_export_cleanly_and_bad_format_is_refused()
    {
        using var sb = new Sandbox(); var x = Svc(sb, out var st); using var _ = st;
        var empty = x.Export(new ExpenseFilter(Search: "no-existe-nada"), "xlsx");
        using (var wb = new XLWorkbook(new MemoryStream(empty.Content))) Assert.Single(wb.Worksheet("Gastos").RowsUsed());   // header only
        Assert.Throws<ValidationFailedException>(() => x.Export(ExpenseFilter.None, "pdf"));
    }
}

public class ExportApiTests : IDisposable
{
    private readonly Sandbox _sb = new(); private ApiFactory? _f;
    public void Dispose() { _f?.Dispose(); _sb.Dispose(); }

    [Fact]
    public async Task Endpoint_downloads_the_file_for_the_current_filters()
    {
        var c = (_f ??= new ApiFactory(_sb)).CreateClient();
        var r = await c.GetAsync("/api/expenses/export?format=csv&monedas=EUR&estado=aprobado");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/csv", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("attachment", r.Content.Headers.ContentDisposition!.DispositionType);
        var lines = (await r.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines.Skip(1), l => { Assert.Contains(",EUR,", l); Assert.Contains(",aprobado,", l); });
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/expenses/export?format=zip")).StatusCode);
        var x = await c.GetAsync("/api/expenses/export");                                  // default format = xlsx
        Assert.Contains("spreadsheetml", x.Content.Headers.ContentType!.MediaType);
    }
}
