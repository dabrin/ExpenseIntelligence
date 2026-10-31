namespace ExpenseIntelligence.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>Immutable seed (the original file as received). Never written by the app.</summary>
    public string SeedPath { get; set; } = "data/original/gastos_desordenados.json";
    /// <summary>Directory for the live store, backup, quarantine, ingest report, audit log and process lock.</summary>
    public string WorkingDirectory { get; set; } = "data/working";
}
