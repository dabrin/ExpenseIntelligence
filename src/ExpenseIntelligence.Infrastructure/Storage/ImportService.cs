using System.Security.Cryptography;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExpenseIntelligence.Infrastructure.Storage;

public sealed record ImportSummary(int Records, DateOnly? MinFecha, DateOnly? MaxFecha, Dictionary<string, int> ByCurrency, int CurrentRecords, int IdCollisions);
public sealed record ImportPreview(string Token, string FileName, long Bytes, string Sha256, bool CanImport, string? Blocker, ImportSummary Summary, LoadReport Report, DoctorReport Doctor,
    DatasetSummary? Duplicate, List<DatasetSummary> SameName);
public sealed record ImportResult(string Mode, int Records, int Skipped, int Repaired, int Quarantined, string SavedAs);

/// <summary>
/// Two-step import: Preview analyses the uploaded file without touching any data; Commit replaces the dataset only after an explicit confirmation.
/// The uploaded bytes are kept untouched (read-only) as the new "original".
/// </summary>
public sealed class ImportService(JsonExpenseStore store, DatasetService datasets, DataDoctorService doctor, IOptions<StorageOptions> options, TimeProvider clock, ILogger<ImportService> log)
{
    public const long MaxBytes = 25L * 1024 * 1024;
    private const int MaxPendingFiles = 20;
    private static readonly TimeSpan PendingTtl = TimeSpan.FromHours(1);
    private string Dir => Path.Combine(Path.GetFullPath(options.Value.WorkingDirectory), "imports");
    private string Pending => Path.Combine(Dir, "pending");

    public async Task<ImportPreview> PreviewAsync(Stream upload, string fileName, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int n;
        while ((n = await upload.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > MaxBytes) throw Invalid($"El archivo supera el máximo de {MaxBytes / 1048576} MB.");
            ms.Write(buf, 0, n);
        }
        var bytes = ms.ToArray();
        if (bytes.Length == 0) throw Invalid("El archivo está vacío.");

        var name = SafeName(fileName);
        PrunePending();
        var (items, report) = RecoveryReader.Read(bytes, name, clock);
        var active = items.Where(e => !e.Eliminado).ToList();
        var token = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Pending);
        await File.WriteAllBytesAsync(Path.Combine(Pending, token + ".json"), bytes, ct);
        File.WriteAllText(Path.Combine(Pending, token + ".name"), name);

        var summary = new ImportSummary(active.Count, active.Count == 0 ? null : active.Min(e => e.Fecha), active.Count == 0 ? null : active.Max(e => e.Fecha),
            active.GroupBy(e => e.Moneda).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()), store.Current.Active.Count, active.Count(e => store.Current.ById.ContainsKey(e.Id)));
        var blocker = items.Count == 0 ? "No se pudo leer ningún registro válido; revisa los motivos de cuarentena." : null;
        // Same bytes seen before? Same name with different content (a newer version of a file)?
        var sha = JsonExpenseStore.Sha(bytes);
        var dup = store.FindBySha(sha) is { } known ? datasets.Summarize(known) : null;
        var sameName = store.Catalog.Items.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(d.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.AddedAt).Select(datasets.Summarize).ToList();
        return new ImportPreview(token, name, bytes.Length, report.Sha256, blocker is null || dup is not null, dup is not null ? null : blocker, summary, report, doctor.Analyze(active, report, 0), dup, sameName);
    }

    public Task<ImportResult> CommitAsync(string token, bool replace, string actor, CancellationToken ct = default) =>
        CommitAsync(token, replace ? "new" : "merge", null, actor, ct);

    /// <param name="mode">new = register as another file and activate it (the previous one is kept) | merge = add its records to the active file |
    /// switch = go back to an already-loaded identical file, keeping its edits | reset = same, but discard its edits</param>
    public async Task<ImportResult> CommitAsync(string token, string mode, string? datasetId, string actor, CancellationToken ct = default)
    {
        if (token.Length != 32 || !token.All(Uri.IsHexDigit)) throw Invalid("Token de importación inválido.");
        var file = Path.Combine(Pending, token + ".json");
        if (!File.Exists(file)) throw Invalid("La carga expiró o ya fue aplicada. Vuelve a subir el archivo.");

        var bytes = await File.ReadAllBytesAsync(file, ct);
        var name = File.Exists(Path.Combine(Pending, token + ".name")) ? File.ReadAllText(Path.Combine(Pending, token + ".name")) : "import.json";
        var (items, report) = RecoveryReader.Read(bytes, name, clock);
        var sha = JsonExpenseStore.Sha(bytes);

        ImportResult result;
        if (mode is "switch" or "reset")
        {
            var known = store.FindBySha(sha) ?? throw Invalid("Ese archivo ya no está cargado en el sistema.");
            if (datasetId is not null && datasetId != known.Id) throw Invalid("El archivo no coincide con el indicado.");
            await store.ActivateAsync(known.Id, mode == "reset", actor, ct);
            result = new ImportResult(mode, store.Current.Active.Count, 0, 0, 0, known.OriginalPath);
        }
        else
        {
            if (items.Count == 0) throw Invalid("No hay registros válidos para importar.");
            Directory.CreateDirectory(Dir);
            var saved = Path.Combine(Dir, $"{clock.GetUtcNow():yyyyMMddHHmmss}-{name}");
            await File.WriteAllBytesAsync(saved, bytes, ct);
            File.SetAttributes(saved, FileAttributes.ReadOnly);

            int added = items.Count, skipped = 0;
            if (mode == "merge") (added, skipped) = await store.MergeAsync(items, name, actor, ct);
            else
            {
                var info = new DatasetInfo(JsonExpenseStore.NewDatasetId(), name, sha, bytes.Length, clock.GetUtcNow(), saved, false, items.Count(e => !e.Eliminado));
                try { await store.AddAndActivateAsync(info, items, report, actor, ct); }
                catch { File.SetAttributes(saved, FileAttributes.Normal); File.Delete(saved); throw; }
            }
            result = new ImportResult(mode == "merge" ? "merge" : "new", added, skipped, report.Repaired, report.Quarantined, saved);
        }
        foreach (var f in Directory.GetFiles(Pending, token + ".*")) File.Delete(f);
        log.LogInformation("Import {Name} applied as {Mode}: {Loaded} loaded, {Repaired} repaired, {Quarantined} quarantined", name, mode, report.Loaded, report.Repaired, report.Quarantined);
        return result;
    }

    /// <summary>Uploads that were previewed but never confirmed would pile up on disk: drop the expired ones and keep at most <see cref="MaxPendingFiles"/> (oldest first).</summary>
    private void PrunePending()
    {
        if (!Directory.Exists(Pending)) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var files = new DirectoryInfo(Pending).GetFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var keep = files.Where(f => now - f.LastWriteTimeUtc <= PendingTtl).ToList();
        foreach (var f in files.Except(keep).Concat(keep.Take(Math.Max(0, keep.Count - (MaxPendingFiles - 1)))))
            try { File.Delete(f.FullName); File.Delete(Path.ChangeExtension(f.FullName, ".name")); } catch (IOException) { /* best effort */ }
    }

    private static string SafeName(string n)
    {
        var f = Path.GetFileName(n ?? "import.json");
        var clean = new string(f.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "import.json" : clean[..Math.Min(clean.Length, 80)];
    }

    private static ValidationFailedException Invalid(string m) => new(new Dictionary<string, string[]> { ["file"] = [m] });
}
