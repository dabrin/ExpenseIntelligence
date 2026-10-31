using System.Text.Json;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Infrastructure.Storage;

/// <summary>Streams records to disk with the same shape/field order as the source file; optional fields only when meaningful.</summary>
internal static class ExpenseFileWriter
{
    public static void Write(Stream stream, IReadOnlyList<Expense> items)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartArray();
        for (var i = 0; i < items.Count; i++)
        {
            var e = items[i];
            w.WriteStartObject();
            w.WriteNumber("id", e.Id);
            w.WriteString("descripcion", e.Descripcion);
            w.WriteNumber("monto", e.Monto);
            w.WriteString("moneda", e.Moneda);
            w.WriteString("categoria", e.Categoria);
            w.WriteString("departamento", e.Departamento);
            w.WriteString("metodo_pago", e.MetodoPago);
            w.WriteString("fecha", e.Fecha.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteBoolean("aprobado", e.Aprobado);
            w.WriteString("codigo_factura", e.CodigoFactura);
            if (e.Version > 1) w.WriteNumber("version", e.Version);
            if (e.Eliminado) w.WriteBoolean("eliminado", true);
            if (e.ActualizadoEn is { } u) w.WriteString("actualizado_en", u);
            if (e.Rechazado) { w.WriteBoolean("rechazado", true); w.WriteString("motivo_rechazo", e.MotivoRechazo); }
            w.WriteEndObject();
            if ((i & 0x1FF) == 0x1FF) w.Flush(); // keep the writer buffer small: no multi-MB allocations per save
        }
        w.WriteEndArray();
        w.Flush();
    }
}
