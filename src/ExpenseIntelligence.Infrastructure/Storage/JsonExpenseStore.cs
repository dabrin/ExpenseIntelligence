using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExpenseIntelligence.Infrastructure.Storage;

/// <summary>
/// JSON-file repository.
/// Reads: lock-free against an immutable <see cref="DatasetSnapshot"/> (never re-deserialises the file per request).
/// Writes: serialised by a SemaphoreSlim -> stream to .tmp -> fsync -> File.Replace (atomic, keeps .bak) -> only then swap the snapshot.
/// If any step fails the snapshot is untouched and a <see cref="PersistenceException"/> is raised, so the UI never shows an unconfirmed change.
/// Multi-process safety: an exclusive lock file makes a second instance fail fast (supported model: ONE API process per working directory).
/// </summary>
public sealed partial class JsonExpenseStore : IExpenseStore, IDisposable
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonExpenseStore> _log;
    private readonly TimeProvider _clock;
    private string _seed;
    private readonly string _dir, _file, _tmp, _bak, _audit, _origSeed, _datasetsDir, _catalogFile, _importsDir;
    private CatalogFile _catalog = null!;
    private string _sourceName = "";
    private readonly FileStream _lock;
    private string _seedSha;
    private LoadReport _loadReport;
    private DatasetSnapshot _current;
    private DateTimeOffset? _lastPersistedAt;
    private double? _lastPersistMs;
    private long _writes, _failures, _auditFailures;

    public JsonExpenseStore(IOptions<StorageOptions> options, TimeProvider clock, ILogger<JsonExpenseStore> log)
    {
        _log = log; _clock = clock;
        _seed = _origSeed = Path.GetFullPath(options.Value.SeedPath);
        _dir = Path.GetFullPath(options.Value.WorkingDirectory);
        _file = Path.Combine(_dir, "expenses.json");
        _tmp = _file + ".tmp"; _bak = _file + ".bak"; _audit = Path.Combine(_dir, "audit.jsonl");
        _datasetsDir = Path.Combine(_dir, "datasets"); _catalogFile = Path.Combine(_datasetsDir, "catalog.json"); _importsDir = Path.Combine(_dir, "imports");
        Directory.CreateDirectory(_dir);

        try { _lock = new FileStream(Path.Combine(_dir, ".store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Another process already owns the data directory '{_dir}'. Only one API instance per directory is supported.", ex);
        }

        var srcFile = Path.Combine(_dir, "source.json");
        if (File.Exists(srcFile) && JsonSerializer.Deserialize<SourcePointer>(File.ReadAllText(srcFile), AuditJson) is { Path: { Length: > 0 } sp } src && File.Exists(sp)) { _seed = sp; _sourceName = src.Name; } // last imported file
        _seedSha = File.Exists(_seed) ? Hex(File.ReadAllBytes(_seed)) : "";
        var (items, report) = Load();
        _loadReport = report;
        _current = new DatasetSnapshot(items, 1, clock.GetUtcNow());
        _catalog = LoadCatalog();
        _log.LogInformation("Store ready: {Loaded} records ({Active} active), {Repaired} repaired, {Quarantined} quarantined, strategy {Strategy}, {Ms} ms",
            report.Loaded, _current.Active.Count, report.Repaired, report.Quarantined, report.Strategy, report.ElapsedMs);
    }

    public DatasetSnapshot Current => Volatile.Read(ref _current);

    // ---------- startup / recovery ----------
    private (List<Expense>, LoadReport) Load()
    {
        if (File.Exists(_tmp))
        {
            _log.LogWarning("Leftover {Tmp} found: a previous write did not complete. It is discarded; the committed file is authoritative.", _tmp);
            File.Delete(_tmp);
        }

        if (File.Exists(_file))
        {
            var (items, rep) = RecoveryReader.Read(File.ReadAllBytes(_file), _file, _clock);
            if (rep.Quarantined == 0 && !rep.Truncated)
            {
                // keep the ingest findings of the file this data came from (repairs/quarantine), not the clean re-read of our own output
                var persisted = Path.Combine(_dir, "ingest-report.json");
                if (File.Exists(persisted) && JsonSerializer.Deserialize<LoadReport>(File.ReadAllText(persisted), AuditJson) is { } prev && prev.Loaded == items.Count) rep = prev;
                return (items, rep);
            }

            _log.LogError("Working file damaged ({Q} quarantined, truncated={T}). Preserving a copy and trying the backup.", rep.Quarantined, rep.Truncated);
            File.Copy(_file, Path.Combine(_dir, $"expenses.corrupt-{_clock.GetUtcNow():yyyyMMddHHmmss}.json"), true);
            if (File.Exists(_bak))
            {
                var (bi, br) = RecoveryReader.Read(File.ReadAllBytes(_bak), _bak, _clock);
                if (br.Quarantined == 0 && !br.Truncated && bi.Count >= items.Count) { _log.LogWarning("Recovered from backup {Bak}", _bak); return (bi, br); }
            }
            WriteIngestArtifacts(rep);
            return (items, rep); // best effort: what could be parsed; the rest is in quarantine.json
        }

        if (!File.Exists(_seed)) throw new FileNotFoundException($"Seed file not found: {_seed}");
        var (seedItems, seedRep) = RecoveryReader.Read(File.ReadAllBytes(_seed), _seed, _clock);
        // First boot: materialise a clean working copy. The seed itself is never modified.
        Commit(seedItems);
        WriteIngestArtifacts(seedRep);
        _log.LogInformation("Working copy created from seed {Seed}", _seed);
        return (seedItems, seedRep);
    }

    private void WriteIngestArtifacts(LoadReport rep)
    {
        File.WriteAllText(Path.Combine(_dir, "ingest-report.json"), JsonSerializer.Serialize(rep, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        if (rep.Issues.Count > 0)
            File.WriteAllText(Path.Combine(_dir, "quarantine.json"), JsonSerializer.Serialize(rep.Issues, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    // ---------- durable commit ----------
    private void Commit(IReadOnlyList<Expense> items)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using (var fs = new FileStream(_tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                ExpenseFileWriter.Write(fs, items);
                fs.Flush(flushToDisk: true); // fsync before the rename
            }
            if (File.Exists(_file)) File.Replace(_tmp, _file, _bak, ignoreMetadataErrors: true);
            else File.Move(_tmp, _file);
            _lastPersistedAt = _clock.GetUtcNow();
            _lastPersistMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            Interlocked.Increment(ref _writes);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            try { if (File.Exists(_tmp)) File.Delete(_tmp); } catch { /* best effort */ }
            _log.LogError(ex, "Persistence failed; in-memory state left unchanged");
            throw new PersistenceException("No se pudo guardar el cambio en el archivo JSON. No se aplicó ninguna modificación.", ex);
        }
    }

    private async Task<T> MutateAsync<T>(Func<DatasetSnapshot, (List<Expense> Next, T Result, List<AuditEntry> Audit)> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cur = _current;
            var (next, result, audit) = change(cur);
            Commit(next);                                   // commit point: durable on disk...
            var snap = new DatasetSnapshot(next, cur.Revision + 1, _clock.GetUtcNow());
            Volatile.Write(ref _current, snap);             // ...then visible to readers
            AppendAudit(audit.Select(a => a with { Revision = snap.Revision }));
            return result;
        }
        finally { _gate.Release(); }
    }

    private void AppendAudit(IEnumerable<AuditEntry> entries)
    {
        try
        {
            using var fs = new FileStream(_audit, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var w = new StreamWriter(fs);
            foreach (var e in entries) w.WriteLine(JsonSerializer.Serialize(e, AuditJson));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _auditFailures);
            _log.LogError(ex, "Audit append failed (data change was committed)");
        }
    }

    private sealed record SourcePointer(string Path, string Name);

    /// <summary>Replaces the whole dataset with an imported one (atomic, audited, serialised with every other write).</summary>
    public async Task<int> ReplaceAllAsync(List<Expense> items, LoadReport report, string sourceName, string sourcePath, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cur = _current; var previous = cur.Active.Count;
            Commit(items);
            var snap = new DatasetSnapshot(items, cur.Revision + 1, _clock.GetUtcNow());
            Volatile.Write(ref _current, snap);
            _seed = sourcePath; _seedSha = Hex(File.ReadAllBytes(sourcePath)); _loadReport = report;
            try
            {
                File.WriteAllText(Path.Combine(_dir, "source.json"), JsonSerializer.Serialize(new SourcePointer(sourcePath, sourceName), AuditJson));
                WriteIngestArtifacts(report);
                if (report.Issues.Count == 0) File.Delete(Path.Combine(_dir, "quarantine.json"));
            }
            catch (Exception ex) { _log.LogError(ex, "Import committed but its ingest artifacts could not be written"); }
            AppendAudit([new AuditEntry(_clock.GetUtcNow(), "import", null, actor, snap.Revision, $"{previous} registros", $"{items.Count} registros", $"{sourceName}; reparados={report.Repaired}; cuarentena={report.Quarantined}")]);
            _log.LogWarning("Dataset replaced by import {Name}: {Old} -> {New} records", sourceName, previous, items.Count);
            return items.Count;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Adds imported records to the current dataset. IDs that already exist (even logically deleted) are skipped, never overwritten.</summary>
    public async Task<(int Added, int Skipped)> MergeAsync(List<Expense> items, string sourceName, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cur = _current;
            var fresh = items.Where(e => !cur.ById.ContainsKey(e.Id)).ToList();
            if (fresh.Count == 0) return (0, items.Count);
            var next = new List<Expense>(cur.All.Count + fresh.Count);
            next.AddRange(cur.All); next.AddRange(fresh);
            Commit(next);
            var snap = new DatasetSnapshot(next, cur.Revision + 1, _clock.GetUtcNow());
            Volatile.Write(ref _current, snap);
            AppendAudit([new AuditEntry(_clock.GetUtcNow(), "import-merge", null, actor, snap.Revision, $"{cur.Active.Count} registros", $"{snap.Active.Count} registros", $"{sourceName}; agregados={fresh.Count}; omitidos por ID existente={items.Count - fresh.Count}")]);
            _log.LogWarning("Import {Name} merged: +{Added}, skipped {Skipped}", sourceName, fresh.Count, items.Count - fresh.Count);
            return (fresh.Count, items.Count - fresh.Count);
        }
        finally { _gate.Release(); }
    }

    private static string Snap(Expense e) => JsonSerializer.Serialize(e, AuditJson);

    // ---------- operations ----------
    public Task<Expense> CreateAsync(Func<DatasetSnapshot, Expense> build, string actor, CancellationToken ct = default) =>
        MutateAsync<Expense>(s =>
        {
            var e = build(s);
            if (s.ById.ContainsKey(e.Id)) throw new ValidationFailedException(new Dictionary<string, string[]> { ["id"] = [$"El ID {e.Id} ya existe."] });
            e = e with { ActualizadoEn = _clock.GetUtcNow() };
            var next = new List<Expense>(s.All.Count + 1);
            next.AddRange(s.All); next.Add(e);
            return (next, e, [new AuditEntry(_clock.GetUtcNow(), "create", e.Id, actor, 0, null, Snap(e), null)]);
        }, ct);

    public Task<Expense> UpdateAsync(int id, int expectedVersion, Func<Expense, Expense> change, string actor, CancellationToken ct = default) =>
        MutateAsync(s => Replace(s, id, expectedVersion, change, "update", actor), ct);

    public Task<Expense> DeleteAsync(int id, int expectedVersion, string actor, CancellationToken ct = default) =>
        MutateAsync(s => Replace(s, id, expectedVersion, e => e with { Eliminado = true }, "delete", actor), ct);

    public Task<Expense> RestoreAsync(int id, string actor, CancellationToken ct = default) =>
        MutateAsync<Expense>(s =>
        {
            if (!s.ById.TryGetValue(id, out var e) || !e.Eliminado) throw new NotFoundException(id);
            return Replace(s, id, e.Version, x => x with { Eliminado = false }, "restore", actor);
        }, ct);

    private (List<Expense>, Expense, List<AuditEntry>) Replace(DatasetSnapshot s, int id, int expectedVersion, Func<Expense, Expense> change, string action, string actor)
    {
        if (!s.ById.TryGetValue(id, out var old) || (old.Eliminado && action != "restore")) throw new NotFoundException(id);
        if (old.Version != expectedVersion) throw new ConcurrencyConflictException(id, expectedVersion, old.Version);
        var updated = change(old) with { Id = old.Id, Version = old.Version + 1, ActualizadoEn = _clock.GetUtcNow() };
        var next = new List<Expense>(s.All);
        next[next.FindIndex(x => x.Id == id)] = updated;
        return (next, updated, [new AuditEntry(_clock.GetUtcNow(), action, id, actor, 0, Snap(old), Snap(updated), null)]);
    }

    public string ActiveDatasetId => Catalog.ActiveId;

    public async Task<T> ApplyBatchAsync<T>(Func<DatasetSnapshot, BatchPlan<T>> plan, string action, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cur = _current;
            var p = plan(cur);                              // validates against the very snapshot we are about to replace; may throw
            if (p.Changes.Count == 0) return p.Result;      // nothing eligible: no write, no revision bump
            var now = _clock.GetUtcNow();
            var next = new List<Expense>(cur.All.Count); var audit = new List<AuditEntry>(p.Changes.Count);
            foreach (var e in cur.All)
            {
                if (!p.Changes.TryGetValue(e.Id, out var n)) { next.Add(e); continue; }
                n = n with { Id = e.Id, Version = e.Version + 1, ActualizadoEn = now };
                next.Add(n);
                audit.Add(new AuditEntry(now, action, e.Id, actor, 0, Snap(e), Snap(n), p.Note));
            }
            Commit(next);                                   // the single durable write for the whole batch
            var snap = new DatasetSnapshot(next, cur.Revision + 1, now);
            Volatile.Write(ref _current, snap);
            AppendAudit(audit.Select(a => a with { Revision = snap.Revision }));
            return p.Result;
        }
        finally { _gate.Release(); }
    }

    public Task<int> BulkUpdateAsync(Func<Expense, Expense?> change, string action, string actor, CancellationToken ct = default) =>
        MutateAsync<int>(s =>
        {
            var next = new List<Expense>(s.All.Count); var audit = new List<AuditEntry>();
            foreach (var e in s.All)
            {
                var n = e.Eliminado ? null : change(e);
                if (n is null) { next.Add(e); continue; }
                n = n with { Id = e.Id, Version = e.Version + 1, ActualizadoEn = _clock.GetUtcNow() };
                next.Add(n);
                audit.Add(new AuditEntry(_clock.GetUtcNow(), action, e.Id, actor, 0, Snap(e), Snap(n), null));
            }
            return (next, audit.Count, audit);
        }, ct);

    // ---------- diagnostics ----------
    public StorageStatus GetStatus()
    {
        var s = Current;
        var exists = File.Exists(_file);
        return new StorageStatus(_file, exists, exists ? new FileInfo(_file).Length : 0, exists ? Hex(File.ReadAllBytes(_file)) : null,
            File.Exists(_bak), _seed, _seedSha, File.Exists(_seed) && Hex(File.ReadAllBytes(_seed)) == _seedSha,
            s.Revision, s.All.Count, s.Active.Count, s.All.Count - s.Active.Count, _lastPersistedAt, _lastPersistMs,
            Interlocked.Read(ref _writes), Interlocked.Read(ref _failures), Interlocked.Read(ref _auditFailures),
            "Single process: SemaphoreSlim-serialised writes + optimistic version per record + exclusive lock file", _loadReport);
    }

    public async Task<IntegrityReport> VerifyIntegrityAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false); // freeze writers so memory and disk are comparable
        try
        {
            var sw = Stopwatch.StartNew();
            var problems = new List<string>();
            var mem = _current;
            var (disk, rep) = RecoveryReader.Read(await File.ReadAllBytesAsync(_file, ct), _file, _clock);
            if (rep.Quarantined > 0) problems.Add($"{rep.Quarantined} registros del archivo no son legibles.");
            if (rep.Truncated) problems.Add("El archivo está truncado.");
            if (rep.DuplicateIds > 0) problems.Add($"{rep.DuplicateIds} IDs duplicados en el archivo.");
            var idsUnique = mem.All.Select(e => e.Id).Distinct().Count() == mem.All.Count;
            if (!idsUnique) problems.Add("IDs duplicados en memoria.");
            var byId = disk.ToDictionary(e => e.Id);
            var match = disk.Count == mem.All.Count && mem.All.All(e => byId.TryGetValue(e.Id, out var d) && d == e);
            if (!match) problems.Add("El contenido en memoria difiere del archivo.");
            var seedOk = !File.Exists(_seed) || Hex(await File.ReadAllBytesAsync(_seed, ct)) == _seedSha;
            if (!seedOk) problems.Add("El archivo original (seed) fue modificado.");
            return new IntegrityReport(problems.Count == 0, _clock.GetUtcNow(), Math.Round(sw.Elapsed.TotalMilliseconds, 1), mem.All.Count, disk.Count, idsUnique, match, seedOk, problems);
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<AuditEntry> ReadAudit(int take)
    {
        if (!File.Exists(_audit)) return [];
        string[] lines;
        using (var fs = new FileStream(_audit, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.TakeLast(Math.Clamp(take, 1, 500)).Reverse()
            .Select(l => JsonSerializer.Deserialize<AuditEntry>(l, AuditJson)!).ToList();
    }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public void Dispose() { _lock.Dispose(); _gate.Dispose(); }
}
