using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Infrastructure.Export;

public sealed record ExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>Exports exactly what a filter selects: a data sheet plus a summary sheet (xlsx) or flat rows (csv). Amounts are never summed across currencies.</summary>
public sealed class ExportService(IExpenseStore store, AnalyticsService analytics, TimeProvider clock)
{
    public const int MaxRows = 200_000;
    private static readonly string[] Headers = ["ID", "Fecha", "Descripción", "Departamento", "Categoría", "Método de pago", "Factura", "Moneda", "Monto", "Estado", "Motivo del rechazo"];

    public ExportFile Export(ExpenseFilter filter, string? format)
    {
        format = (format ?? "xlsx").Trim().ToLowerInvariant();
        if (format is not ("xlsx" or "csv"))
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["format"] = ["Formato desconocido: usa xlsx o csv."] });
        var rows = filter.Apply(store.Current.Active).OrderByDescending(e => e.Fecha).ThenBy(e => e.Id).ToList();
        if (rows.Count > MaxRows)
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["filtros"] = [$"El filtro devuelve {rows.Count} gastos; el máximo por exportación es {MaxRows}. Acota el filtro."] });
        var stamp = clock.GetUtcNow().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return format == "csv"
            ? new ExportFile(Csv(rows), "text/csv; charset=utf-8", $"gastos_{stamp}.csv")
            : new ExportFile(Xlsx(filter, rows), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"gastos_{stamp}.xlsx");
    }

    /// <summary>Spreadsheet apps run a cell that starts with = + - @ as a formula: neutralise it so imported text stays text.</summary>
    internal static string Safe(string? s) => string.IsNullOrEmpty(s) ? "" : s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + s : s;

    private static byte[] Csv(List<Expense> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Headers)).Append("\r\n");
        static string Q(string? v) { v = Safe(v); return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v; }
        foreach (var e in rows)
            sb.Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append(',').Append(e.Fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(Q(e.Descripcion)).Append(',').Append(Q(e.Departamento)).Append(',').Append(Q(e.Categoria)).Append(',').Append(Q(e.MetodoPago)).Append(',')
              .Append(Q(e.CodigoFactura)).Append(',').Append(e.Moneda).Append(',').Append(e.Monto.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Estado).Append(',').Append(Q(e.MotivoRechazo)).Append("\r\n");
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(); // BOM so Excel reads accents correctly
    }

    private byte[] Xlsx(ExpenseFilter filter, List<Expense> rows)
    {
        using var wb = new XLWorkbook();
        var navy = XLColor.FromHtml("#0A2250");
        void Head(IXLRange r) { r.Style.Font.Bold = true; r.Style.Font.FontColor = XLColor.White; r.Style.Fill.BackgroundColor = navy; }

        // ---- Resumen
        var sum = analytics.Summary(filter);
        var rs = wb.AddWorksheet("Resumen");
        var r = 1;
        rs.Cell(r, 1).Value = "Insta Expenses · Exportación"; rs.Cell(r, 1).Style.Font.Bold = true; rs.Cell(r, 1).Style.Font.FontSize = 16; r++;
        rs.Cell(r++, 1).Value = $"Generado: {clock.GetUtcNow():yyyy-MM-dd HH:mm} UTC · {rows.Count} gastos exportados";
        r++;
        rs.Cell(r, 1).Value = "Filtros aplicados"; rs.Cell(r++, 1).Style.Font.Bold = true;
        foreach (var line in Describe(filter)) rs.Cell(r++, 1).Value = line;
        r++;

        rs.Cell(r, 1).Value = "Totales por moneda"; rs.Cell(r++, 1).Style.Font.Bold = true;
        var h = new[] { "Moneda", "Gastos", "Total", "Promedio", "Máximo", "Aprobado", "Pendiente", "Rechazado" };
        for (var c = 0; c < h.Length; c++) rs.Cell(r, c + 1).Value = h[c];
        Head(rs.Range(r, 1, r, h.Length)); r++;
        var top = r;
        foreach (var t in sum.Totals)
        {
            var rej = sum.Approval.ByCurrency.FirstOrDefault(x => x.Moneda == t.Moneda)?.RejectedTotal ?? 0;
            rs.Cell(r, 1).Value = t.Moneda; rs.Cell(r, 2).Value = t.Count; rs.Cell(r, 3).Value = t.Total; rs.Cell(r, 4).Value = t.Average; rs.Cell(r, 5).Value = t.Max;
            rs.Cell(r, 6).Value = t.ApprovedTotal; rs.Cell(r, 7).Value = t.PendingTotal; rs.Cell(r, 8).Value = rej; r++;
        }
        rs.Range(top, 3, r - 1, 8).Style.NumberFormat.Format = "#,##0.00";
        rs.Cell(r++, 1).Value = "Las monedas nunca se suman entre sí.";
        r++;

        void Dim(string title, List<DimensionStat> data)
        {
            rs.Cell(r, 1).Value = title; rs.Cell(r++, 1).Style.Font.Bold = true;
            var hh = new[] { "Clave", "Moneda", "Gastos", "Total", "Promedio" };
            for (var c = 0; c < hh.Length; c++) rs.Cell(r, c + 1).Value = hh[c];
            Head(rs.Range(r, 1, r, hh.Length)); r++;
            var first = r;
            foreach (var d in data.OrderBy(x => x.Moneda, StringComparer.Ordinal).ThenByDescending(x => x.Total))
            { rs.Cell(r, 1).Value = Safe(d.Key); rs.Cell(r, 2).Value = d.Moneda; rs.Cell(r, 3).Value = d.Count; rs.Cell(r, 4).Value = d.Total; rs.Cell(r, 5).Value = d.Average; r++; }
            if (r > first) rs.Range(first, 4, r - 1, 5).Style.NumberFormat.Format = "#,##0.00";
            r++;
        }
        Dim("Por departamento", sum.ByDepartment); Dim("Por categoría", sum.ByCategory); Dim("Por método de pago", sum.ByPaymentMethod);
        rs.Column(1).Width = 34; for (var c = 2; c <= 8; c++) rs.Column(c).Width = 16;

        // ---- Gastos
        var ws = wb.AddWorksheet("Gastos");
        for (var c = 0; c < Headers.Length; c++) ws.Cell(1, c + 1).Value = Headers[c];
        Head(ws.Range(1, 1, 1, Headers.Length));
        var row = 2;
        foreach (var e in rows)
        {
            ws.Cell(row, 1).Value = e.Id;
            ws.Cell(row, 2).Value = e.Fecha.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 3).Value = Safe(e.Descripcion); ws.Cell(row, 4).Value = Safe(e.Departamento); ws.Cell(row, 5).Value = Safe(e.Categoria);
            ws.Cell(row, 6).Value = Safe(e.MetodoPago); ws.Cell(row, 7).Value = Safe(e.CodigoFactura); ws.Cell(row, 8).Value = e.Moneda;
            ws.Cell(row, 9).Value = e.Monto; ws.Cell(row, 10).Value = e.Estado; ws.Cell(row, 11).Value = Safe(e.MotivoRechazo);
            row++;
        }
        if (rows.Count > 0)
        {
            ws.Range(2, 2, row - 1, 2).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Range(2, 9, row - 1, 9).Style.NumberFormat.Format = "#,##0.00";
        }
        ws.Range(1, 1, Math.Max(row - 1, 1), Headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        // fixed widths: AdjustToContents needs system fonts, which a slim Linux container does not have
        double[] widths = [8, 12, 52, 20, 20, 20, 14, 9, 14, 12, 40];
        for (var c = 0; c < widths.Length; c++) ws.Column(c + 1).Width = widths[c];

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    internal static List<string> Describe(ExpenseFilter f)
    {
        var l = new List<string>();
        string J(string[]? v) => string.Join(", ", v!.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (f.From is not null || f.To is not null) l.Add($"Fechas: {f.From?.ToString("yyyy-MM-dd") ?? "inicio"} a {f.To?.ToString("yyyy-MM-dd") ?? "hoy"}");
        if (f.Departamentos is { Length: > 0 }) l.Add($"Departamento: {J(f.Departamentos)}");
        if (f.Categorias is { Length: > 0 }) l.Add($"Categoría: {J(f.Categorias)}");
        if (f.Monedas is { Length: > 0 }) l.Add($"Moneda: {J(f.Monedas)}");
        if (f.MetodosPago is { Length: > 0 }) l.Add($"Método de pago: {J(f.MetodosPago)}");
        if (!string.IsNullOrWhiteSpace(f.Estado)) l.Add($"Estado: {f.Estado}");
        if (f.Aprobado is { } a) l.Add($"Aprobado: {(a ? "sí" : "no")}");
        if (!string.IsNullOrWhiteSpace(f.Search)) l.Add($"Búsqueda: {f.Search}");
        if (f.Ids is { Length: > 0 }) l.Add($"Solo los gastos: {string.Join(", ", f.Ids.Take(20).Select(i => "#" + i))}{(f.Ids.Length > 20 ? "…" : "")}");
        if (l.Count == 0) l.Add("Sin filtros (todos los gastos activos)");
        return l;
    }
}
