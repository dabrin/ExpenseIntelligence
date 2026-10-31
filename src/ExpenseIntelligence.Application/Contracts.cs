using ExpenseIntelligence.Domain;

namespace ExpenseIntelligence.Application;

/// <summary>Immutable, consistent view of the whole dataset. Replaced atomically after each successful write.</summary>
public sealed class DatasetSnapshot
{
    public DatasetSnapshot(IReadOnlyList<Expense> all, long revision, DateTimeOffset createdAt)
    {
        All = all;
        Revision = revision;
        CreatedAt = createdAt;
        var byId = new Dictionary<int, Expense>(all.Count);
        var active = new List<Expense>(all.Count);
        foreach (var e in all)
        {
            byId[e.Id] = e;
            if (!e.Eliminado) active.Add(e);
        }
        ById = byId;
        Active = active;
        NextId = all.Count == 0 ? 1 : all.Max(e => e.Id) + 1;
    }

    /// <summary>All records including logically deleted ones (what is stored in the file).</summary>
    public IReadOnlyList<Expense> All { get; }
    /// <summary>Records visible to queries.</summary>
    public IReadOnlyList<Expense> Active { get; }
    public IReadOnlyDictionary<int, Expense> ById { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAt { get; }
    public int NextId { get; }
}

public sealed record ExpenseInput(
    string? Descripcion, decimal? Monto, string? Moneda, string? Categoria, string? Departamento,
    string? MetodoPago, DateOnly? Fecha, bool Aprobado, string? CodigoFactura);

public sealed record ExpenseUpdateInput(ExpenseInput Data, int Version);

public sealed record AuditEntry(
    DateTimeOffset At, string Action, int? ExpenseId, string Actor, long Revision, string? Before, string? After, string? Note);

public sealed record StorageStatus(
    string WorkingFile, bool WorkingFileExists, long WorkingFileBytes, string? WorkingFileSha256,
    bool BackupExists, string SeedFile, string SeedSha256, bool SeedUnchanged,
    long Revision, int TotalRecords, int ActiveRecords, int DeletedRecords,
    DateTimeOffset? LastPersistedAt, double? LastPersistMs, long Writes, long WriteFailures, long AuditFailures,
    string ConcurrencyModel, LoadReport LoadReport);

public sealed record IntegrityReport(bool Ok, DateTimeOffset CheckedAt, double ElapsedMs, int RecordsInMemory, int RecordsOnDisk,
    bool IdsUnique, bool MemoryMatchesDisk, bool SeedUnchanged, List<string> Problems);

public sealed record LoadIssue(int Index, int Line, string Reason, string? RecordIdHint, string Raw);
public sealed record LoadRepair(int Line, string Description, string Before, string After, int? RecordId);

public sealed record LoadReport(
    string Source, string Strategy, long Bytes, string Sha256, bool Truncated,
    int ObjectsFound, int Loaded, int Repaired, int Quarantined, int DuplicateIds,
    List<LoadRepair> Repairs, List<LoadIssue> Issues, DateTimeOffset LoadedAt, double ElapsedMs);

/// <summary>What a bulk operation wants to change, decided against one consistent snapshot. Changes are keyed by id (new values; version/timestamp are stamped by the store).</summary>
public sealed record BatchPlan<T>(IReadOnlyDictionary<int, Expense> Changes, T Result, string? Note);

/// <summary>Persistence port. Implementations guarantee: serialized writes, atomic durable commit, snapshot swap only after success.</summary>
public interface IExpenseStore
{
    DatasetSnapshot Current { get; }
    Task<Expense> CreateAsync(Func<DatasetSnapshot, Expense> build, string actor, CancellationToken ct = default);
    Task<Expense> UpdateAsync(int id, int expectedVersion, Func<Expense, Expense> change, string actor, CancellationToken ct = default);
    Task<Expense> DeleteAsync(int id, int expectedVersion, string actor, CancellationToken ct = default);
    Task<Expense> RestoreAsync(int id, string actor, CancellationToken ct = default);
    /// <summary>Id of the dataset (uploaded file) whose rows are currently live.</summary>
    string ActiveDatasetId { get; }
    /// <summary>Plans and applies many changes inside ONE critical section with ONE durable commit and ONE audit append. If <paramref name="plan"/> throws, nothing is written.</summary>
    Task<T> ApplyBatchAsync<T>(Func<DatasetSnapshot, BatchPlan<T>> plan, string action, string actor, CancellationToken ct = default);
    Task<int> BulkUpdateAsync(Func<Expense, Expense?> change, string action, string actor, CancellationToken ct = default);
    StorageStatus GetStatus();
    Task<IntegrityReport> VerifyIntegrityAsync(CancellationToken ct = default);
    IReadOnlyList<AuditEntry> ReadAudit(int take);
}
