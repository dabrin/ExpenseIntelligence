export interface Expense {
  id: number; descripcion: string; monto: number; moneda: string; categoria: string; departamento: string;
  metodoPago: string; fecha: string; aprobado: boolean; codigoFactura: string; version: number; eliminado: boolean; actualizadoEn: string | null;
  rechazado: boolean; motivoRechazo: string | null; estado: ExpenseState;
}
export type ExpenseState = 'pendiente' | 'aprobado' | 'rechazado';
export interface ExpenseInput {
  descripcion: string; monto: number | null; moneda: string; categoria: string; departamento: string;
  metodoPago: string; fecha: string; aprobado: boolean; codigoFactura: string;
}
export interface Filters {
  from: string; to: string; departamentos: string[]; categorias: string[]; monedas: string[]; metodosPago: string[];
  estado: '' | ExpenseState; q: string;
}
export const EMPTY_FILTERS: Filters = { from: '', to: '', departamentos: [], categorias: [], monedas: [], metodosPago: [], estado: '', q: '' };

export interface Facets { departamentos: string[]; categorias: string[]; monedas: string[]; metodosPago: string[]; minFecha: string | null; maxFecha: string | null; revision: number; deletedCount: number }
export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number; totalPages: number; revision: number }

export interface CurrencyTotal { moneda: string; total: number; count: number; average: number; max: number; approvedTotal: number; pendingTotal: number }
export interface DimensionStat { key: string; moneda: string; count: number; total: number; average: number }
export interface TimePoint { period: string; moneda: string; count: number; total: number; approvedTotal: number; pendingTotal: number }
export interface ApprovalStat { approved: number; pending: number; rejected: number; approvedPct: number; pendingPct: number; rejectedPct: number; byCurrency: { moneda: string; approved: number; pending: number; approvedTotal: number; pendingTotal: number }[] }
export interface Summary {
  revision: number; totalRecords: number; totals: CurrencyTotal[]; byDepartment: DimensionStat[]; byCategory: DimensionStat[];
  byPaymentMethod: DimensionStat[]; series: TimePoint[]; granularity: string; approval: ApprovalStat;
}
export interface Insight { id: string; type: string; severity: 'info' | 'warning'; title: string; detail: string; data: Record<string, unknown> }

export interface LoadReport {
  source: string; strategy: string; bytes: number; sha256: string; truncated: boolean; objectsFound: number; loaded: number; repaired: number;
  quarantined: number; duplicateIds: number; repairs: { line: number; description: string; before: string; after: string; recordId: number | null }[];
  issues: { index: number; line: number; reason: string; recordIdHint: string | null; raw: string }[]; loadedAt: string; elapsedMs: number;
}
export interface Anomaly { code: string; severity: 'error' | 'warning' | 'info'; title: string; cause: string; recommendation: string; affectedCount: number; recordIds: number[]; autoFixable: boolean; scope: string }
export interface DoctorReport { revision: number; records: number; affectedRecords: number; qualityScore: number; anomalies: Anomaly[]; ingest: LoadReport }

export interface RouteTiming { route: string; count: number; p50Ms: number; p95Ms: number; maxMs: number; avgMs: number }
export interface Diagnostics {
  storage: {
    workingFile: string; workingFileExists: boolean; workingFileBytes: number; workingFileSha256: string | null; backupExists: boolean; seedFile: string; seedSha256: string;
    seedUnchanged: boolean; revision: number; totalRecords: number; activeRecords: number; deletedRecords: number; lastPersistedAt: string | null; lastPersistMs: number | null;
    writes: number; writeFailures: number; auditFailures: number; concurrencyModel: string; loadReport: LoadReport;
  };
  timings: RouteTiming[];
  process: { uptimeSeconds: number; workingSetMb: number; gcHeapMb: number; gen0: number; gen1: number; gen2: number; pauseTimePercent: number; runtime: string };
}
export interface IntegrityReport { ok: boolean; checkedAt: string; elapsedMs: number; recordsInMemory: number; recordsOnDisk: number; idsUnique: boolean; memoryMatchesDisk: boolean; seedUnchanged: boolean; problems: string[] }
export interface AuditEntry { at: string; action: string; expenseId: number | null; actor: string; revision: number; before: string | null; after: string | null; note: string | null }
export interface Benchmark { records: number; results: { name: string; iterations: number; p50Ms: number; p95Ms: number; maxMs: number }[] }
export interface AssistantAnswer { mode: string; intent: string; answer: string; columns: string[]; rows: string[][]; period: string; source: string; suggestions: string[]; scope: string[] }

export interface ApiProblem { status: number; title: string; errors?: Record<string, string[]>; currentVersion?: number; conflicts?: BulkConflict[]; conflictCount?: number }

export interface BulkConflict { id: number | null; kind: string; expectedVersion: number | null; currentVersion: number | null; message: string }
export interface BulkItem { id: number; version: number }
export interface BulkFilterDto {
  from: string | null; to: string | null; departamentos: string[]; categorias: string[]; monedas: string[]; metodosPago: string[];
  aprobado: boolean | null; search: string | null; estado: string | null;
}
/** Either explicit items (with the version the user saw) or a filter + the count the user saw ("all results"). */
export interface BulkRequest { datasetId: string; items?: BulkItem[]; filter?: BulkFilterDto; expectedCount?: number; reason?: string }
export interface BulkTotal { moneda: string; count: number; total: number }
export interface BulkSkip { id: number; code: string; message: string }
export interface BulkPreview {
  operation: string; datasetId: string; requested: number; eligible: number; skipped: number; duplicates: number;
  eligibleTotals: BulkTotal[]; skippedByReason: Record<string, number>; skippedSample: BulkSkip[]; maxItems: number; revision: number;
}
export interface BulkResult {
  operationId: string; operation: string; requested: number; applied: number; skipped: number; duplicates: number;
  appliedTotals: BulkTotal[]; skippedByReason: Record<string, number>; skippedSample: BulkSkip[]; revision: number; elapsedMs: number;
}

export interface ImportPreview {
  token: string; fileName: string; bytes: number; sha256: string; canImport: boolean; blocker: string | null;
  summary: { records: number; minFecha: string | null; maxFecha: string | null; byCurrency: Record<string, number>; currentRecords: number; idCollisions: number };
  report: LoadReport; doctor: DoctorReport; duplicate: DatasetSummary | null; sameName: DatasetSummary[];
}
export interface ImportResult { mode: string; skipped: number; records: number; repaired: number; quarantined: number; savedAs: string }
export interface DatasetSummary {
  id: string; name: string; sha256: string; bytes: number; addedAt: string; isActive: boolean; isSeed: boolean;
  originalRecords: number; records: number; changed: number; added: number; removed: number; hasEdits: boolean;
}
export interface DiffRow { kind: 'added' | 'removed' | 'changed'; key: string; a: Expense | null; b: Expense | null; changes: FieldChange[] }
export interface SideSummary { label: string; records: number; approvedPct: number; byCurrency: { moneda: string; count: number; total: number }[] }
export interface CompareCounts { added: number; removed: number; changed: number; same: number; ignoredDuplicates: number; fieldChanges: Record<string, number> }
export interface CompareTotals { moneda: string; countA: number; totalA: number; countB: number; totalB: number }
export interface CompareResponse { labelA: string; labelB: string; a: SideSummary; b: SideSummary; totals: CompareTotals[]; counts: CompareCounts; rows: Paged<DiffRow> }

export interface MonthCurrency { moneda: string; count: number; total: number; approved: number; approvedTotal: number }
export interface MonthFrame { period: string; totals: MonthCurrency[]; byDepartment: DimensionStat[]; byCategory: DimensionStat[] }
export interface Timeline { revision: number; currencies: string[]; departments: string[]; categories: string[]; frames: MonthFrame[] }

export interface FieldChange { field: string; label: string; before: string | null; after: string | null }
export interface HistoryItem {
  at: string; action: string; group: string; expenseId: number | null; actor: string; revision: number; title: string; detail: string | null; amount: number | null; currency: string | null;
  changes: FieldChange[]; canRestore: boolean; count: number; note: string | null;
}
