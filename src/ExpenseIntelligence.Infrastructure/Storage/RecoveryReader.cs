using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Infrastructure.Storage;

/// <summary>
/// Fault-tolerant reader for the expense file. It scans the top-level array object by object, so a defect in one
/// record can never take the rest of the file down. Nothing is invented and nothing is dropped silently:
/// every record is either loaded, loaded after a documented deterministic repair, or quarantined with its raw text.
/// </summary>
public static partial class RecoveryReader
{
    private const int MaxIssues = 200;           // issues kept in the report; the full count is still reported
    private const int MaxRawChars = 2000;
    private const decimal MaxAbsMonto = 1e12m;   // far above any real expense, far below what makes decimal sums overflow
    private static string Clip(string s) => s.Length <= MaxRawChars ? s : s[..MaxRawChars];

    // JSON strings can never contain a raw newline, so resetting the "in string" state at '\n' is exact for valid JSON
    // and keeps a stray quote (e.g.  id": 1407 ) from desynchronising the scanner for the rest of the file.
    public static (List<Expense> Items, LoadReport Report) Read(byte[] bytes, string source, TimeProvider clock)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var items = new List<Expense>();
        var repairs = new List<LoadRepair>();
        var issues = new List<LoadIssue>();
        var seen = new Dictionary<int, int>();
        int objects = 0, dupIds = 0, line = 1, pos = 0;
        bool truncated = false, closed = false;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) pos = 3;
        void SkipWs() { while (pos < bytes.Length && (bytes[pos] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',')) { if (bytes[pos] == '\n') line++; pos++; } }

        SkipWs();
        if (pos >= bytes.Length || bytes[pos] != '[')
        {
            issues.Add(new(0, line, "El archivo no comienza con un arreglo JSON '['.", null, Encoding.UTF8.GetString(bytes, pos, Math.Min(200, bytes.Length - pos))));
            return (items, Build(source, bytes.Length, sha, true, 0, 0, repairs, issues, 0, sw, clock));
        }
        pos++;

        while (true)
        {
            SkipWs();
            if (pos >= bytes.Length) { truncated = true; break; }
            if (bytes[pos] == ']') { closed = true; break; }
            if (bytes[pos] != '{')
            {
                var s = pos; var sl = line;
                while (pos < bytes.Length && bytes[pos] != '{' && bytes[pos] != ']') { if (bytes[pos] == '\n') line++; pos++; }
                issues.Add(new(objects, sl, "Contenido inesperado entre registros.", null, Encoding.UTF8.GetString(bytes, s, Math.Min(pos - s, 500))));
                continue;
            }

            // find matching '}' (string aware, newline resets string state)
            int start = pos, startLine = line, depth = 0; bool inStr = false, esc = false, found = false;
            for (; pos < bytes.Length; pos++)
            {
                var b = bytes[pos];
                if (b == '\n') { line++; inStr = false; esc = false; continue; }
                if (inStr) { if (esc) esc = false; else if (b == '\\') esc = true; else if (b == '"') inStr = false; continue; }
                if (b == '"') inStr = true;
                else if (b == '{') depth++;
                else if (b == '}' && --depth == 0) { pos++; found = true; break; }
            }
            if (!found)
            {
                truncated = true; objects++;
                issues.Add(new(objects - 1, startLine, "Registro final sin cerrar (archivo truncado).", null, Encoding.UTF8.GetString(bytes, start, Math.Min(bytes.Length - start, 2000))));
                break;
            }

            objects++;
            var chunk = bytes.AsSpan(start, pos - start);
            Expense? e = null; string? failure = null;
            try { e = Map(chunk); }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
            {
                failure = ex.Message;
                var text = Encoding.UTF8.GetString(chunk);
                var fixedText = TryRepair(text, startLine, out var fixes);
                if (fixedText is not null)
                {
                    try
                    {
                        e = Map(Encoding.UTF8.GetBytes(fixedText));
                        foreach (var f in fixes) repairs.Add(f with { RecordId = e.Id });
                        failure = null;
                    }
                    catch (Exception ex2) when (ex2 is JsonException or FormatException or InvalidDataException) { failure = ex2.Message; }
                }
                if (failure is not null)
                    issues.Add(new(objects - 1, startLine, failure, TryIdHint(text), Clip(text)));
            }
            if (e is null) continue;

            if (seen.TryGetValue(e.Id, out var firstLine))
            {
                dupIds++;
                issues.Add(new(objects - 1, startLine, $"ID duplicado {e.Id} (primera aparición en línea {firstLine}); se conserva la primera.", e.Id.ToString(), Clip(Encoding.UTF8.GetString(chunk))));
                continue;
            }
            seen[e.Id] = startLine;
            items.Add(e);
        }

        if (!closed && !truncated) truncated = true;
        return (items, Build(source, bytes.Length, sha, truncated, objects, dupIds, repairs, issues, items.Count, sw, clock));
    }

    private static LoadReport Build(string source, long len, string sha, bool truncated, int objects, int dupIds, List<LoadRepair> repairs,
        List<LoadIssue> issues, int loaded, System.Diagnostics.Stopwatch sw, TimeProvider clock) =>
        new(source, "object-scan + deterministic-repair + quarantine", len, sha, truncated, objects, loaded, repairs.Count, issues.Count, dupIds, repairs, issues.Take(MaxIssues).ToList(),
            clock.GetUtcNow(), Math.Round(sw.Elapsed.TotalMilliseconds, 1));

    [GeneratedRegex(@"(?m)^(?<ws>[ \t]*)(?<k>[A-Za-z_][A-Za-z0-9_]*)""(?<rest>[ \t]*:)")]
    private static partial Regex MissingOpenQuote();
    [GeneratedRegex(@"(?m)^(?<ws>[ \t]*)""(?<k>[A-Za-z_][A-Za-z0-9_]*)(?<rest>[ \t]*:)")]
    private static partial Regex MissingCloseQuote();
    [GeneratedRegex(@"""id""\s*:\s*(\d+)|(?m)^\s*id""\s*:\s*(\d+)")]
    private static partial Regex IdHintRx();

    private static string? TryIdHint(string text) { var m = IdHintRx().Match(text); return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null; }

    /// <summary>Only repairs a property key that lost one of its quotes. Anything else stays untouched and goes to quarantine.</summary>
    private static string? TryRepair(string text, int startLine, out List<LoadRepair> fixes)
    {
        var found = new List<LoadRepair>();
        string Apply(Regex rx, string input, string label) => rx.Replace(input, m =>
        {
            var replacement = m.Result("${ws}\"${k}\"${rest}");
            var ln = startLine + input.AsSpan(0, m.Index).Count('\n');
            found.Add(new LoadRepair(ln, label, m.Value.Trim(), replacement.Trim(), null));
            return replacement;
        });
        var t = Apply(MissingOpenQuote(), text, "Clave de propiedad sin comilla de apertura");
        t = Apply(MissingCloseQuote(), t, "Clave de propiedad sin comilla de cierre");
        fixes = found;
        return found.Count == 0 ? null : t;
    }

    private static Expense Map(ReadOnlySpan<byte> chunk)
    {
        var rd = new Utf8JsonReader(chunk, new JsonReaderOptions { AllowTrailingCommas = true });
        using var doc = JsonDocument.ParseValue(ref rd);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) throw new InvalidDataException("El elemento no es un objeto.");

        JsonElement Req(string n, JsonValueKind kind)
        {
            if (!r.TryGetProperty(n, out var p)) throw new InvalidDataException($"Falta el campo obligatorio '{n}'.");
            var ok = p.ValueKind == kind || (kind == JsonValueKind.True && p.ValueKind == JsonValueKind.False);
            if (!ok) throw new InvalidDataException($"Campo '{n}' con tipo {p.ValueKind}; se esperaba {(kind == JsonValueKind.True ? "boolean" : kind.ToString())}.");
            return p;
        }

        var id = Req("id", JsonValueKind.Number);
        if (!id.TryGetInt32(out var idv)) throw new InvalidDataException("'id' no es un entero.");
        var monto = Req("monto", JsonValueKind.Number);
        if (!monto.TryGetDecimal(out var montov) || Math.Abs(montov) > MaxAbsMonto) throw new InvalidDataException("'monto' no es un decimal válido.");
        var fechaS = Req("fecha", JsonValueKind.String).GetString()!;
        if (!DateOnly.TryParseExact(fechaS, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
            throw new InvalidDataException($"'fecha' inválida: '{fechaS}'.");
        if (fecha.Year is < 1900 or > 2100) throw new InvalidDataException($"'fecha' fuera de rango: '{fechaS}'.");

        DateTimeOffset? upd = null;
        if (r.TryGetProperty("actualizado_en", out var u) && u.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var du)) upd = du;

        var approved = Req("aprobado", JsonValueKind.True).GetBoolean();
        var rejected = !approved && r.TryGetProperty("rechazado", out var rj) && rj.ValueKind == JsonValueKind.True;

        return new Expense
        {
            Id = idv,
            Descripcion = Req("descripcion", JsonValueKind.String).GetString()!,
            Monto = montov,
            Moneda = Req("moneda", JsonValueKind.String).GetString()!,
            Categoria = Req("categoria", JsonValueKind.String).GetString()!,
            Departamento = Req("departamento", JsonValueKind.String).GetString()!,
            MetodoPago = Req("metodo_pago", JsonValueKind.String).GetString()!,
            Fecha = fecha,
            Aprobado = approved,
            CodigoFactura = Req("codigo_factura", JsonValueKind.String).GetString()!,
            Version = r.TryGetProperty("version", out var v) && v.TryGetInt32(out var vv) ? vv : 1,
            Eliminado = r.TryGetProperty("eliminado", out var d) && d.ValueKind == JsonValueKind.True,
            ActualizadoEn = upd,
            // optional: only present in files written after the approval workflow existed; "rechazado" can never coexist with "aprobado"
            Rechazado = rejected,
            MotivoRechazo = rejected && r.TryGetProperty("motivo_rechazo", out var mr) && mr.ValueKind == JsonValueKind.String ? mr.GetString() : null,
        };
    }
}
