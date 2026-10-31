using System.Text.RegularExpressions;
using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

/// <summary>Use-cases for writing expenses: validation + canonicalisation, then delegation to the durable store.</summary>
public sealed partial class ExpenseService(IExpenseStore store, TimeProvider clock)
{
    public static readonly DateOnly MinDate = new(2000, 1, 1);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_/]{2,29}$")]
    private static partial Regex InvoiceRegex();

    public async Task<Expense> CreateAsync(ExpenseInput input, string actor, CancellationToken ct = default)
    {
        var clean = Validate(input);
        return await store.CreateAsync(snap => Build(snap.NextId, clean), actor, ct);
    }

    public async Task<Expense> UpdateAsync(int id, ExpenseUpdateInput input, string actor, CancellationToken ct = default)
    {
        var clean = Validate(input.Data);
        return await store.UpdateAsync(id, input.Version, cur =>
        {
            // Checked against the stored record inside the writer lock, so approving and changing the amount in one request cannot bypass it.
            if (cur.Aprobado && (clean.Monto!.Value != cur.Monto || !string.Equals(clean.Moneda, cur.Moneda, StringComparison.Ordinal)))
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["monto"] = [LockedMessage] });
            return cur with
            {
                Descripcion = clean.Descripcion!, Monto = clean.Monto!.Value, Moneda = clean.Moneda!, Categoria = clean.Categoria!,
                Departamento = clean.Departamento!, MetodoPago = clean.MetodoPago!, Fecha = clean.Fecha!.Value,
                Aprobado = clean.Aprobado, CodigoFactura = clean.CodigoFactura!,
                // approving a rejected expense by hand closes the rejection; saving it still unapproved keeps it rejected
                Rechazado = !clean.Aprobado && cur.Rechazado, MotivoRechazo = !clean.Aprobado && cur.Rechazado ? cur.MotivoRechazo : null,
            };
        }, actor, ct);
    }

    public const string LockedMessage = "Un gasto aprobado no permite cambiar el monto ni la moneda. Quita primero la aprobación y guarda.";

    public Task<Expense> DeleteAsync(int id, int version, string actor, CancellationToken ct = default) => store.DeleteAsync(id, version, actor, ct);
    public Task<Expense> RestoreAsync(int id, string actor, CancellationToken ct = default) => store.RestoreAsync(id, actor, ct);

    /// <summary>Whether another active record already uses this invoice code (a warning, not a blocker: the source data already has such cases).</summary>
    public bool InvoiceCodeInUse(string code, int? exceptId = null) =>
        store.Current.Active.Any(e => e.Id != exceptId && string.Equals(e.CodigoFactura, code, StringComparison.OrdinalIgnoreCase));

    private static Expense Build(int id, ExpenseInput i) => new()
    {
        Id = id, Descripcion = i.Descripcion!, Monto = i.Monto!.Value, Moneda = i.Moneda!, Categoria = i.Categoria!, Departamento = i.Departamento!,
        MetodoPago = i.MetodoPago!, Fecha = i.Fecha!.Value, Aprobado = i.Aprobado, CodigoFactura = i.CodigoFactura!, Version = 1,
    };

    /// <summary>Validates and returns a canonicalised copy (trimmed, catalogue-matching casing). Throws <see cref="ValidationFailedException"/>.</summary>
    public ExpenseInput Validate(ExpenseInput i)
    {
        var errors = new Dictionary<string, List<string>>();
        void Err(string f, string m) { if (!errors.TryGetValue(f, out var l)) errors[f] = l = []; l.Add(m); }

        var snap = store.Current;
        string? Canon(string field, string? v, Func<Expense, string> sel, string label, string required)
        {
            var t = v?.Trim();
            if (string.IsNullOrEmpty(t)) { Err(field, required); return null; }
            if (t.Length > 80) { Err(field, $"{label} no puede superar 80 caracteres."); return null; }
            var key = TextNorm.Key(t);
            var existing = snap.Active.Select(sel).FirstOrDefault(x => TextNorm.Key(x) == key);
            return existing ?? t;
        }

        var desc = i.Descripcion?.Trim();
        if (string.IsNullOrEmpty(desc)) Err("descripcion", "La descripción es obligatoria.");
        else if (desc.Length is < 3 or > 200) Err("descripcion", "La descripción debe tener entre 3 y 200 caracteres.");

        decimal? monto = i.Monto;
        if (monto is null) Err("monto", "El monto es obligatorio.");
        else if (monto <= 0) Err("monto", "El monto debe ser mayor que cero.");
        else if (monto > 1_000_000_000m) Err("monto", "El monto excede el máximo permitido (1.000.000.000).");
        else if (decimal.Round(monto.Value, 2) != monto) Err("monto", "El monto admite como máximo 2 decimales.");

        var mon = i.Moneda?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(mon)) Err("moneda", "La moneda es obligatoria.");
        else if (!Catalog.Currencies.Contains(mon)) Err("moneda", $"Moneda no soportada. Use: {string.Join(", ", Catalog.Currencies)}.");

        var cat = Canon("categoria", i.Categoria, e => e.Categoria, "La categoría", "La categoría es obligatoria.");
        var dep = Canon("departamento", i.Departamento, e => e.Departamento, "El departamento", "El departamento es obligatorio.");
        var pay = Canon("metodoPago", i.MetodoPago, e => e.MetodoPago, "El método de pago", "El método de pago es obligatorio.");

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        if (i.Fecha is null) Err("fecha", "La fecha es obligatoria.");
        else if (i.Fecha > today) Err("fecha", "La fecha no puede ser futura.");
        else if (i.Fecha < MinDate) Err("fecha", $"La fecha no puede ser anterior a {MinDate:yyyy-MM-dd}.");

        var fac = i.CodigoFactura?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(fac)) Err("codigoFactura", "El código de factura es obligatorio.");
        else if (!InvoiceRegex().IsMatch(fac)) Err("codigoFactura", "Formato inválido (3-30 caracteres alfanuméricos, p. ej. FAC-12345).");

        if (errors.Count > 0)
            throw new ValidationFailedException(errors.ToDictionary(k => k.Key, k => k.Value.ToArray()));

        return new ExpenseInput(desc, decimal.Round(monto!.Value, 2), mon, cat, dep, pay, i.Fecha, i.Aprobado, fac);
    }
}
