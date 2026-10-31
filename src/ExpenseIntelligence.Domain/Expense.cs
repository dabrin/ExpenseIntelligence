namespace ExpenseIntelligence.Domain;

/// <summary>Immutable expense record. Field names mirror the source dataset (Spanish).</summary>
public sealed record Expense
{
    public required int Id { get; init; }
    public required string Descripcion { get; init; }
    public required decimal Monto { get; init; }
    public required string Moneda { get; init; }
    public required string Categoria { get; init; }
    public required string Departamento { get; init; }
    public required string MetodoPago { get; init; }
    public required DateOnly Fecha { get; init; }
    public required bool Aprobado { get; init; }
    public required string CodigoFactura { get; init; }

    /// <summary>Optimistic concurrency token. Absent in the source file (= 1); incremented on every change.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Logical deletion flag. Deleted records stay in the file (audit/undo) but are invisible to queries.</summary>
    public bool Eliminado { get; init; }

    public DateTimeOffset? ActualizadoEn { get; init; }

    /// <summary>Explicit rejection. Absent in the source files (= false). A rejected expense always has <see cref="Aprobado"/> = false:
    /// historic <c>aprobado:false</c> rows are Pending, never silently rejected.</summary>
    public bool Rechazado { get; init; }
    public string? MotivoRechazo { get; init; }

    /// <summary>Derived workflow state: pendiente | aprobado | rechazado.</summary>
    public string Estado => Aprobado ? ExpenseStates.Approved : Rechazado ? ExpenseStates.Rejected : ExpenseStates.Pending;
}

public static class ExpenseStates
{
    public const string Pending = "pendiente", Approved = "aprobado", Rejected = "rechazado";
    public static bool IsValid(string? s) => s is Pending or Approved or Rejected;
}

public static class Catalog
{
    public static readonly string[] Currencies = ["CRC", "USD", "EUR"];
}

public abstract class DomainException(string message) : Exception(message);

public sealed class NotFoundException(int id) : DomainException($"Expense {id} not found.");

public sealed class ConcurrencyConflictException(int id, int expected, int actual)
    : DomainException($"Expense {id} was modified by someone else (expected version {expected}, current {actual}).")
{
    public int Id { get; } = id;
    public int ActualVersion { get; } = actual;
}

public sealed class DatasetNotFoundException(string id) : DomainException($"El archivo '{id}' no existe en el sistema.");

/// <summary>A business rule about uploaded files was violated (e.g. deleting the active one). The message is meant for the user.</summary>
public sealed class DatasetRuleException(string message) : DomainException(message);

/// <summary>One record that made a bulk operation impossible. Kind: missing | version | selection-changed.</summary>
public sealed record BulkConflict(int? Id, string Kind, int? ExpectedVersion, int? CurrentVersion, string Message);

/// <summary>A bulk operation was refused as a whole (all-or-nothing): nothing was written.</summary>
public sealed class BulkConflictException(string message, int total, IReadOnlyList<BulkConflict> conflicts) : DomainException(message)
{
    public int Total { get; } = total;
    public IReadOnlyList<BulkConflict> Conflicts { get; } = conflicts;
}

public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
    : DomainException("One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>The change could not be made durable. The in-memory state was NOT changed.</summary>
public sealed class PersistenceException(string message, Exception? inner = null) : Exception(message, inner);
