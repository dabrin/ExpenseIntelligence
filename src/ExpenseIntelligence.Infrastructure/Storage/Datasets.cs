using System.Security.Cryptography;
using System.Text.Json;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;
using Microsoft.Extensions.Logging;

namespace ExpenseIntelligence.Infrastructure.Storage;

/// <summary>One uploaded file known to the system. The original bytes are kept read-only; edits live apart from them.</summary>
public sealed record DatasetInfo(string Id, string Name, string Sha256, long Bytes, DateTimeOffset AddedAt, string OriginalPath, bool IsSeed, int OriginalRecords);
public sealed record CatalogFile(string ActiveId, List<DatasetInfo> Items);

public static class RowVariant { public const string Actual = "actual", Original = "original"; }

// Dataset catalogue: several uploaded files can coexist, one is "active" (the live working file). When another file is activated the
// active one's working state is stashed under datasets/{id}/state.json, so edits survive switching and can be discarded explicitly.
public sealed partial class JsonExpenseStore
{
    public CatalogFile Catalog => Volatile.Read(ref _catalog);
    public DatasetInfo? FindDataset(string id) => Catalog.Items.FirstOrDefault(d => d.Id == id);
    public DatasetInfo? FindBySha(string sha) => Catalog.Items.FirstOrDefault(d => string.Equals(d.Sha256, sha, StringComparison.OrdinalIgnoreCase));
    public static string NewDatasetId() => Guid.NewGuid().ToString("N")[..8];
    public static string Sha(byte[] bytes) => Hex(bytes);

    private string StateDir(string id) => Path.Combine(_datasetsDir, id);
    private string StatePath(string id) => Path.Combine(StateDir(id), "state.json");
    public bool HasSavedState(string id) => id != Catalog.ActiveId && File.Exists(StatePath(id));

    // ---------- catalogue file ----------
    private CatalogFile LoadCatalog()
    {
        Directory.CreateDirectory(_datasetsDir);
        if (File.Exists(_catalogFile))
        {
            try
            {
                var c = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(_catalogFile), AuditJson);
                if (c is { Items.Count: > 0 } && c.Items.Any(i => i.Id == c.ActiveId)) return c;
            }
            catch (JsonException ex) { _log.LogError(ex, "Catalogue unreadable; rebuilding it from the files on disk"); }
        }

        // First run with this feature (or damaged catalogue): the configured seed is the first file; a previously imported file becomes the active one.
        var items = new List<DatasetInfo> { Describe(_origSeed, "seed", true, Path.GetFileName(_origSeed)) };
        var active = "seed";
        if (!string.Equals(_seed, _origSeed, StringComparison.OrdinalIgnoreCase) && File.Exists(_seed))
        {
            var imp = Describe(_seed, NewDatasetId(), false, string.IsNullOrEmpty(_sourceName) ? Path.GetFileName(_seed) : _sourceName);
            items.Add(imp); active = imp.Id;
        }
        var cat = new CatalogFile(active, items);
        SaveCatalog(cat);
        return cat;
    }

    private DatasetInfo Describe(string path, string id, bool seed, string name)
    {
        var bytes = File.ReadAllBytes(path);
        var (rows, _) = RecoveryReader.Read(bytes, name, _clock);
        return new DatasetInfo(id, name, Hex(bytes), bytes.Length, seed ? _clock.GetUtcNow() : new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), path, seed, rows.Count(e => !e.Eliminado));
    }

    private void SaveCatalog(CatalogFile c)
    {
        Directory.CreateDirectory(_datasetsDir);
        var tmp = _catalogFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.Move(tmp, _catalogFile, overwrite: true);
    }

    // ---------- reading other datasets ----------
    private readonly Dictionary<string, (List<Expense> Items, LoadReport Report)> _origCache = [];

    private (List<Expense> Items, LoadReport Report) ReadOriginal(DatasetInfo ds)
    {
        lock (_origCache)
        {
            if (_origCache.TryGetValue(ds.Sha256, out var hit)) return hit;
            var r = RecoveryReader.Read(File.ReadAllBytes(ds.OriginalPath), ds.Name, _clock);
            if (_origCache.Count >= 3) _origCache.Clear();
            return _origCache[ds.Sha256] = r;
        }
    }

    /// <summary>Non-deleted rows of a file: its pristine original, or its current working state (live memory if active, the stash otherwise).</summary>
    public IReadOnlyList<Expense> LoadRows(string id, string variant)
    {
        var ds = FindDataset(id) ?? throw new DatasetNotFoundException(id);
        if (variant != RowVariant.Original)
        {
            if (id == Catalog.ActiveId) return Current.Active;
            var st = StatePath(id);
            if (File.Exists(st)) return RecoveryReader.Read(File.ReadAllBytes(st), st, _clock).Items.Where(e => !e.Eliminado).ToList();
        }
        return ReadOriginal(ds).Items.Where(e => !e.Eliminado).ToList();
    }

    // ---------- switching / adding ----------
    private void StashLocked(string id, IReadOnlyList<Expense> rows)
    {
        try
        {
            Directory.CreateDirectory(StateDir(id));
            var st = StatePath(id); var tmp = st + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                ExpenseFileWriter.Write(fs, rows);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, st, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not stash the working state of dataset {Id}", id);
            throw new PersistenceException("No se pudo guardar el estado del archivo actual. No se hizo ningún cambio.", ex);
        }
    }

    /// <summary>Makes <paramref name="target"/> the live dataset. The caller holds the write gate.</summary>
    private void InstallLocked(DatasetInfo target, List<Expense> items, LoadReport report, CatalogFile next, AuditEntry audit)
    {
        var cur = _current;
        var previousId = _catalog.ActiveId;
        if (previousId != target.Id) StashLocked(previousId, cur.All);   // 1) keep what the user did in the file we are leaving
        Commit(items);                                                     // 2) durable switch of the live file
        var snap = new DatasetSnapshot(items, cur.Revision + 1, _clock.GetUtcNow());
        Volatile.Write(ref _current, snap);
        _seed = target.OriginalPath; _seedSha = target.Sha256; _loadReport = report; _sourceName = target.Name;
        Volatile.Write(ref _catalog, next);
        try
        {
            SaveCatalog(next);
            File.WriteAllText(Path.Combine(_dir, "source.json"), JsonSerializer.Serialize(new SourcePointer(target.OriginalPath, target.Name), AuditJson));
            WriteIngestArtifacts(report);
            if (report.Issues.Count == 0) File.Delete(Path.Combine(_dir, "quarantine.json"));
        }
        catch (Exception ex) { _log.LogError(ex, "Dataset switched but its bookkeeping files could not be written"); }
        AppendAudit([audit with { Revision = snap.Revision }]);
    }

    /// <summary>Registers a newly uploaded file and makes it the active one. The previous file is kept (with its edits) and can be re-activated.</summary>
    public async Task AddAndActivateAsync(DatasetInfo info, List<Expense> items, LoadReport report, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cat = _catalog;
            if (cat.Items.Any(d => string.Equals(d.Sha256, info.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new DatasetRuleException("Ya existe un archivo con exactamente este contenido. Puedes volver a él desde Archivos.");
            var previous = _current.Active.Count;
            InstallLocked(info, items, report, new CatalogFile(info.Id, [.. cat.Items, info]),
                new AuditEntry(_clock.GetUtcNow(), "import", null, actor, 0, $"{previous} registros", $"{items.Count} registros", $"{info.Name}; reparados={report.Repaired}; cuarentena={report.Quarantined}"));
            _log.LogWarning("Dataset {Name} added and activated: {Old} -> {New} records", info.Name, previous, items.Count);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Activates a known file. <paramref name="reset"/> discards its saved edits and returns it to the pristine original.</summary>
    public async Task<DatasetInfo> ActivateAsync(string id, bool reset, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cat = _catalog;
            var ds = cat.Items.FirstOrDefault(d => d.Id == id) ?? throw new DatasetNotFoundException(id);
            if (id == cat.ActiveId && !reset) return ds;

            var (original, report) = ReadOriginal(ds);
            List<Expense> items = [.. original];
            var action = reset ? "dataset-reset" : "dataset-switch";
            var st = StatePath(id);
            if (!reset && File.Exists(st))
            {
                var (saved, rep) = RecoveryReader.Read(File.ReadAllBytes(st), st, _clock);
                if (rep.Quarantined == 0 && !rep.Truncated) items = saved;
                else _log.LogError("Saved state of dataset {Id} is damaged; falling back to its original", id);
            }
            var before = _current.Active.Count;
            InstallLocked(ds, items, report, cat with { ActiveId = id },
                new AuditEntry(_clock.GetUtcNow(), action, null, actor, 0, $"{before} registros", $"{items.Count(e => !e.Eliminado)} registros", ds.Name));
            if (reset) try { Directory.Delete(StateDir(id), true); } catch (IOException) { /* stale stash is ignored anyway */ }
            return ds;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes an inactive uploaded file (original copy and saved edits) from the system.</summary>
    public async Task DeleteDatasetAsync(string id, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cat = _catalog;
            var ds = cat.Items.FirstOrDefault(d => d.Id == id) ?? throw new DatasetNotFoundException(id);
            if (ds.IsSeed) throw new DatasetRuleException("El archivo original del sistema no se elimina. Para volver a empezar de cero usa «Dejar el sistema limpio».");
            if (id == cat.ActiveId) throw new DatasetRuleException("Es el archivo activo. Activa otro antes de eliminarlo.");
            var next = cat with { Items = cat.Items.Where(d => d.Id != id).ToList() };
            SaveCatalog(next);
            Volatile.Write(ref _catalog, next);
            try { Directory.Delete(StateDir(id), true); } catch (DirectoryNotFoundException) { }
            DeleteReadOnly(ds.OriginalPath);
            lock (_origCache) _origCache.Remove(ds.Sha256);
            AppendAudit([new AuditEntry(_clock.GetUtcNow(), "dataset-delete", null, actor, _current.Revision, null, null, ds.Name)]);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Leaves the system as if no file had ever been uploaded: the seed becomes the live data again and every uploaded file, saved edit,
    /// pending upload, backup and the audit history are erased. Irreversible.
    /// </summary>
    public async Task FactoryResetAsync(string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seedInfo = Describe(_origSeed, "seed", true, Path.GetFileName(_origSeed));
            var (items, report) = RecoveryReader.Read(File.ReadAllBytes(_origSeed), seedInfo.Name, _clock);
            Commit(items);
            var snap = new DatasetSnapshot(items, _current.Revision + 1, _clock.GetUtcNow());
            Volatile.Write(ref _current, snap);
            _seed = _origSeed; _seedSha = seedInfo.Sha256; _loadReport = report; _sourceName = seedInfo.Name;
            lock (_origCache) _origCache.Clear();

            foreach (var d in new[] { _datasetsDir, _importsDir })
                try { if (Directory.Exists(d)) { ClearReadOnly(d); Directory.Delete(d, true); } } catch (Exception ex) { _log.LogError(ex, "Could not remove {Dir}", d); }
            foreach (var f in new[] { Path.Combine(_dir, "source.json"), Path.Combine(_dir, "quarantine.json"), _bak, _audit })
                try { File.Delete(f); } catch (Exception ex) { _log.LogError(ex, "Could not remove {File}", f); }
            foreach (var f in Directory.GetFiles(_dir, "expenses.corrupt-*.json")) try { File.Delete(f); } catch (IOException) { }

            var cat = new CatalogFile("seed", [seedInfo]);
            Volatile.Write(ref _catalog, cat);
            SaveCatalog(cat);
            WriteIngestArtifacts(report);
            AppendAudit([new AuditEntry(_clock.GetUtcNow(), "factory-reset", null, actor, snap.Revision, null, $"{items.Count} registros", $"Se restauró el archivo original {seedInfo.Name} y se borró todo lo demás")]);
            _log.LogWarning("FACTORY RESET by {Actor}: back to seed {Seed}", actor, _origSeed);
        }
        finally { _gate.Release(); }
    }

    private static void DeleteReadOnly(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch (IOException) { }
    }

    private static void ClearReadOnly(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
    }
}
